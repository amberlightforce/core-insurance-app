using System.Net;
using CoreIns.Host.Hosting;
using Npgsql;
using static CoreIns.IntegrationTests.Claims.ClaimsSlice;

namespace CoreIns.IntegrationTests.Claims;

/// <summary>The migrate job creates <c>clm</c> with least privilege for the app role, and the immutability triggers hold for every role.</summary>
public sealed class ClaimsDatabaseTests(PostgresFixture database) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task The_clm_schema_is_migrated_with_no_delete_and_an_insert_only_FNOL_snapshot_for_the_app_role()
    {
        ModuleCatalog.Databases.Select(d => d.Schema).ShouldContain("clm");
        await using var dataSource = NpgsqlDataSource.Create(database.AppConnectionString);

        async Task<bool> Can(string table, string privilege)
        {
            await using var command = dataSource.CreateCommand($"SELECT has_table_privilege('app', '{table}', '{privilege}')");
            return (bool)(await command.ExecuteScalarAsync(Ct))!;
        }

        foreach (var table in new[] { "claim", "exposure", "claimant", "incident" })
        {
            (await Can($"clm.{table}", "SELECT")).ShouldBeTrue(table);
            (await Can($"clm.{table}", "INSERT")).ShouldBeTrue(table);
            (await Can($"clm.{table}", "UPDATE")).ShouldBeTrue(table);
            (await Can($"clm.{table}", "DELETE")).ShouldBeFalse(table);
            (await Can($"clm.{table}", "TRUNCATE")).ShouldBeFalse(table);
        }

        (await Can("clm.fnol_snapshot", "INSERT")).ShouldBeTrue();
        (await Can("clm.fnol_snapshot", "UPDATE")).ShouldBeFalse();
        (await Can("clm.fnol_snapshot", "DELETE")).ShouldBeFalse();
        (await Can("clm.__ef_migrations_history", "SELECT")).ShouldBeFalse();

        // Every business row carries legal entity, jurisdiction, created_at, created_by and record_version (PRD-07 §7.0).
        await using var columns = dataSource.CreateCommand(
            """
            SELECT count(*) FROM information_schema.columns
             WHERE table_schema = 'clm' AND table_name IN ('claim', 'exposure', 'claimant', 'incident', 'fnol_snapshot')
               AND column_name IN ('legal_entity_id', 'jurisdiction', 'created_at', 'created_by', 'record_version')
            """);
        ((long)(await columns.ExecuteScalarAsync(Ct))!).ShouldBe(25);
    }

    [Fact]
    public async Task REQ_CLM_044_The_FNOL_snapshot_and_the_claim_identity_cannot_be_changed_even_by_the_owner()
    {
        await using var slice = new ClaimsSlice(database.AppConnectionString);
        var (response, body) = await slice.SubmitAsync(Fnol(slice.Policy()));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        var claimId = body.Text("claimId");

        foreach (var sql in new[]
                 {
                     $"UPDATE clm.fnol_snapshot SET channel = 'APP' WHERE claim_id = '{claimId}'",
                     $"DELETE FROM clm.fnol_snapshot WHERE claim_id = '{claimId}'",
                     $"UPDATE clm.claim SET claim_number = 'CLM999999999' WHERE claim_id = '{claimId}'",
                     $"UPDATE clm.claim SET policy_id = gen_random_uuid() WHERE claim_id = '{claimId}'",
                 })
        {
            var refused = await Should.ThrowAsync<PostgresException>(() => database.ExecuteAsSuperuserAsync(sql, Ct));
            refused.SqlState.ShouldBe(PostgresErrorCodes.RestrictViolation, sql);
        }

        // Moving columns still move.
        await database.ExecuteAsSuperuserAsync($"UPDATE clm.claim SET handler = 'USER:someone' WHERE claim_id = '{claimId}'", Ct);
    }
}
