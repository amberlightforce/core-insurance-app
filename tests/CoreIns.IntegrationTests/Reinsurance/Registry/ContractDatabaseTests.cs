using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;
using static CoreIns.IntegrationTests.Reinsurance.Registry.RegistrySlice;

namespace CoreIns.IntegrationTests.Reinsurance.Registry;

/// <summary>
/// The <c>ri</c> schema as the migrate job leaves it: least privilege for the app role, and the integrity triggers that bind
/// every writer, run here as the app role itself (REQ-RI-057, -065; PITFALLS 8, 17, 40).
/// </summary>
public sealed class ContractDatabaseTests(PostgresFixture database) : IClassFixture<PostgresFixture>
{
    private static async Task<PostgresException> RefusedAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        return await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Ct), sql);
    }

    private async Task<(RegistrySlice Slice, string Id, NpgsqlDataSource App)> ActiveContractAsync()
    {
        var slice = new RegistrySlice(database);
        var lead = await slice.OrganisationAsync("Synthetic Lead Re " + Guid.NewGuid().ToString("N")[..6]);
        var follow = await slice.OrganisationAsync("Synthetic Follow Re " + Guid.NewGuid().ToString("N")[..6]);
        var (id, approved) = await slice.ApprovedAsync(Body(NewProduct(), "OD", lead, follow));
        approved.Text("contract.status").ShouldBe("ACTIVE");
        return (slice, id, NpgsqlDataSource.Create(database.AppConnectionString));
    }

    [Fact]
    public async Task REQ_RI_065_The_ri_schema_is_migrated_with_no_delete_and_append_only_content_for_the_app_role()
    {
        CoreIns.Host.Hosting.ModuleCatalog.Databases.Select(d => d.Schema).ShouldContain("ri");
        await using var app = NpgsqlDataSource.Create(database.AppConnectionString);

        async Task<bool> Can(string table, string privilege)
        {
            await using var command = app.CreateCommand($"SELECT has_table_privilege('app', '{table}', '{privilege}')");
            return (bool)(await command.ExecuteScalarAsync(Ct))!;
        }

        foreach (var table in new[] { "contract", "contract_version" })
        {
            (await Can($"ri.{table}", "SELECT")).ShouldBeTrue(table);
            (await Can($"ri.{table}", "INSERT")).ShouldBeTrue(table);
            (await Can($"ri.{table}", "UPDATE")).ShouldBeTrue(table);
            (await Can($"ri.{table}", "DELETE")).ShouldBeFalse(table);
            (await Can($"ri.{table}", "TRUNCATE")).ShouldBeFalse(table);
        }

        foreach (var table in new[] { "section", "layer", "clause", "participation" })
        {
            (await Can($"ri.{table}", "SELECT")).ShouldBeTrue(table);
            (await Can($"ri.{table}", "INSERT")).ShouldBeTrue(table);
            (await Can($"ri.{table}", "UPDATE")).ShouldBeFalse(table);
            (await Can($"ri.{table}", "DELETE")).ShouldBeFalse(table);
            (await Can($"ri.{table}", "TRUNCATE")).ShouldBeFalse(table);
        }

        (await Can("ri.__ef_migrations_history", "SELECT")).ShouldBeFalse();
    }

    [Fact]
    public async Task REQ_RI_057_An_approved_version_cannot_be_updated_or_extended_by_the_app_role_PITFALLS_8()
    {
        var (slice, id, app) = await ActiveContractAsync();
        await using var _ = slice;
        await using var __ = app;

        string[] version = ["placed_pct = 50", "content_hash = repeat('a', 64)", "valid_to = '2030-01-01'", "approved_by = 'USER:someone'"];
        foreach (var set in version)
        {
            (await RefusedAsync(app, $"UPDATE ri.contract_version SET {set} WHERE contract_id = '{id}'")).SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege, set);
        }

        // Child rows of an approved version: nothing can be added (even as the version's current revision) or changed.
        var addLayer =
            $"""
            INSERT INTO ri.layer (layer_id, section_id, version_id, rev, layer_no, attachment, limit_amount, aad, currency)
            SELECT gen_random_uuid(), s.section_id, s.version_id, s.rev, 2, 1000000, 500000, 0, 'EUR' FROM ri.section s
              JOIN ri.contract_version v ON v.version_id = s.version_id WHERE v.contract_id = '{id}'
            """;
        (await RefusedAsync(app, addLayer)).SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        (await RefusedAsync(app, $"UPDATE ri.layer SET limit_amount = 1 WHERE version_id IN (SELECT version_id FROM ri.contract_version WHERE contract_id = '{id}')"))
            .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        (await RefusedAsync(app, $"UPDATE ri.participation SET lead = false WHERE version_id IN (SELECT version_id FROM ri.contract_version WHERE contract_id = '{id}')"))
            .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        (await RefusedAsync(app, "DELETE FROM ri.layer")).SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        (await RefusedAsync(app, "DELETE FROM ri.contract_version")).SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        (await RefusedAsync(app, "DELETE FROM ri.contract")).SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);

        // The same holds for the table owner (the trigger binds every role), and for the API.
        (await RefusedAsync(app, $"UPDATE ri.contract SET contract_number = 'RIC999999' WHERE contract_id = '{id}'")).SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        var owner = await Should.ThrowAsync<PostgresException>(() => slice.ExecuteAsync($"UPDATE ri.contract_version SET placed_pct = 50 WHERE contract_id = '{id}'"));
        owner.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        var ownerDelete = await Should.ThrowAsync<PostgresException>(() => slice.ExecuteAsync($"DELETE FROM ri.contract WHERE contract_id = '{id}'"));
        ownerDelete.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        var (patch, body) = await slice.SendAsync(HttpMethod.Patch, $"/api/ri/v1/contracts/{id}", new { expectedRecordVersion = 5, placedPct = "100" });
        patch.StatusCode.ShouldNotBe(System.Net.HttpStatusCode.OK, body?.ToJsonString());

        // What the lifecycle needs still works for the app role: a header column that moves.
        await using var update = app.CreateCommand($"UPDATE ri.contract SET updated_at = now() WHERE contract_id = '{id}'");
        (await update.ExecuteNonQueryAsync(Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task REQ_RI_056_The_status_moves_only_along_the_lifecycle_even_for_a_direct_writer()
    {
        var slice = new RegistrySlice(database);
        await using var _ = slice;
        var lead = await slice.OrganisationAsync("Synthetic Lead Re " + Guid.NewGuid().ToString("N")[..6]);
        var follow = await slice.OrganisationAsync("Synthetic Follow Re " + Guid.NewGuid().ToString("N")[..6]);
        var (id, version, _) = await slice.CreateAsync(Body(NewProduct(), "OD", lead, follow));
        await using var app = NpgsqlDataSource.Create(database.AppConnectionString);

        // Draft cannot jump to Approved / Active / Expired, and an unapproved version cannot be sealed on a draft.
        foreach (var status in new[] { "APPROVED", "ACTIVE", "EXPIRED" })
        {
            (await RefusedAsync(app, $"UPDATE ri.contract SET status = '{status}', decided_by = 'USER:x', decided_at = now(), activated_at = now(), expired_at = now() WHERE contract_id = '{id}'"))
                .SqlState.ShouldBeOneOf(PostgresErrorCodes.CheckViolation, PostgresErrorCodes.InsufficientPrivilege);
        }

        (await RefusedAsync(app, $"UPDATE ri.contract_version SET approved_at = now(), approved_by = 'USER:x' WHERE contract_id = '{id}'")).SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);

        // Child rows must carry the version's current revision.
        var wrongRev =
            $"""
            INSERT INTO ri.clause (clause_id, version_id, rev, alae_included, statutory_interest_included, recoveries_inure)
            SELECT gen_random_uuid(), version_id, content_rev + 1, true, true, 'REALISED_ONLY' FROM ri.contract_version WHERE contract_id = '{id}'
            """;
        (await RefusedAsync(app, wrongRev)).SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        version.ShouldBe(1);
    }

    [Fact]
    public async Task REQ_RI_057_The_valid_period_per_contract_cannot_overlap_among_current_versions()
    {
        var (slice, id, app) = await ActiveContractAsync();
        await using var _ = slice;
        await using var __ = app;
        string Version(string from, string to, int no, string knownTo) =>
            $"""
            INSERT INTO ri.contract_version (version_id, contract_id, version_no, valid_from, valid_to, known_from, known_to, content_rev, placed_pct, content_hash, created_at, created_by)
            VALUES (gen_random_uuid(), '{id}', {no}, '{from}', '{to}', now() - interval '1 hour', {knownTo}, 1, 100, repeat('a', 64), now(), 'USER:test')
            """;

        var overlap = await Should.ThrowAsync<PostgresException>(() => slice.ExecuteAsync(Version("2026-06-01", "2026-12-01", 2, "NULL")));
        overlap.SqlState.ShouldBe(PostgresErrorCodes.ExclusionViolation);
        overlap.ConstraintName.ShouldBe("ex_contract_version_period");

        // A superseded (known_to set) version may overlap, and an adjacent current period is fine: [2027-01-01, …) meets [2026-01-01, 2027-01-01).
        await slice.ExecuteAsync(Version("2026-06-01", "2026-12-01", 3, "now() - interval '30 minutes'"));
        await slice.ExecuteAsync(Version("2027-01-01", "2028-01-01", 4, "NULL"));
    }
}
