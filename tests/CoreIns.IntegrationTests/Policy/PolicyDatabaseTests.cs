using Npgsql;

namespace CoreIns.IntegrationTests.Policy;

/// <summary>
/// The pol schema's database guarantees (REQ-POL-075, -078, -079 on PostgreSQL 17 per D-ARC-05): least privilege for the
/// app role, append-only transactions and charge deltas, immutable term versions and segments, and bitemporal
/// non-overlap through btree_gist exclusion constraints. Run as the superuser to prove the database itself refuses.
/// </summary>
public sealed class PolicyDatabaseTests(PostgresFixture database) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task REQ_POL_075_the_app_role_cannot_update_or_delete_the_transaction_log()
    {
        await using var dataSource = NpgsqlDataSource.Create(database.AppConnectionString);

        async Task<bool> Can(string table, string privilege)
        {
            await using var command = dataSource.CreateCommand($"SELECT has_table_privilege('app', '{table}', '{privilege}')");
            return (bool)(await command.ExecuteScalarAsync(Ct))!;
        }

        foreach (var table in new[] { "job", "quote_version", "policy", "policy_term", "segment", "policy_transaction", "charge_line" })
        {
            (await Can($"pol.{table}", "SELECT")).ShouldBeTrue(table);
            (await Can($"pol.{table}", "INSERT")).ShouldBeTrue(table);
            (await Can($"pol.{table}", "DELETE")).ShouldBeFalse(table);
        }

        (await Can("pol.policy_transaction", "UPDATE")).ShouldBeFalse();
        (await Can("pol.charge_line", "UPDATE")).ShouldBeFalse();
        (await Can("pol.__ef_migrations_history", "SELECT")).ShouldBeFalse();
    }

    [Fact]
    public async Task REQ_POL_078_079_term_versions_never_overlap_bitemporally_and_change_only_by_closing_their_record_period()
    {
        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        var policy = Guid.CreateVersion7();
        var term = Guid.CreateVersion7();
        var transaction = Guid.CreateVersion7();
        await ExecuteAsync(dataSource, $"""
            INSERT INTO pol.policy (policy_id, legal_entity_id, jurisdiction, policy_number, product_code, policyholder_party_id, recorded_at, created_by, record_version)
            VALUES ('{policy}', '{ApiHostFactory.LegalEntityId}', 'GR', 'POLTEST-{policy:N}', 'P', '{Guid.CreateVersion7()}', '2026-10-01T00:00Z', 'test', 1);
            INSERT INTO pol.policy_transaction (transaction_id, policy_id, term_id, job_id, legal_entity_id, kind, sequence, effective_at, recorded_at,
                configuration_hash, artefact_hash, resolution_hash, intent, premium, taxes, total, currency, actor, correlation_id, origin)
            VALUES ('{transaction}', '{policy}', '{term}', '{Guid.CreateVersion7()}', '{ApiHostFactory.LegalEntityId}', 'ISSUANCE', 1,
                '2026-11-01T00:00Z', '2026-10-01T00:00Z', 'c', 'a', 'r', jsonb_build_object(), 100, 10, 110, 'EUR', 'test', 'x', 'LIVE');
            """);
        string Term(Guid id, int number, string from, string to, string recordedFrom) => $"""
            INSERT INTO pol.policy_term (term_version_id, term_id, policy_id, legal_entity_id, term_number, valid_from, valid_to, recorded_from, state,
                product_version, artefact_hash, resolution_hash, configuration_hash, currency, payment_plan_ref, written_date, head_transaction_id, created_by)
            VALUES ('{Guid.CreateVersion7()}', '{id}', '{policy}', '{ApiHostFactory.LegalEntityId}', {number}, '{from}', '{to}', '{recordedFrom}', 'SCHEDULED',
                '1.0', 'a', 'r', 'c', 'EUR', 'PLAN', '2026-10-01', '{transaction}', 'test')
            """;

        await ExecuteAsync(dataSource, Term(term, 1, "2026-11-01T00:00Z", "2027-11-01T00:00Z", "2026-10-01T00:00Z"));

        // A second term overlapping the first in valid time, both current in record time: refused by the exclusion constraint.
        var overlap = await Should.ThrowAsync<PostgresException>(() =>
            ExecuteAsync(dataSource, Term(Guid.CreateVersion7(), 2, "2027-06-01T00:00Z", "2028-06-01T00:00Z", "2026-10-02T00:00Z")));
        overlap.SqlState.ShouldBe(PostgresErrorCodes.ExclusionViolation);
        overlap.ConstraintName.ShouldBe("ex_policy_term_no_overlap");

        // The adjacent next term (half-open periods) is fine.
        await ExecuteAsync(dataSource, Term(Guid.CreateVersion7(), 2, "2027-11-01T00:00Z", "2028-11-01T00:00Z", "2026-10-02T00:00Z"));

        // Supersession: close the record period of version 1, then a corrected version of the same term is recorded later.
        await ExecuteAsync(dataSource, $"UPDATE pol.policy_term SET recorded_to = '2026-10-05T00:00Z' WHERE term_id = '{term}' AND recorded_to IS NULL");
        await ExecuteAsync(dataSource, Term(term, 1, "2026-11-01T00:00Z", "2027-11-01T00:00Z", "2026-10-05T00:00Z"));

        // Anything else is refused: changing content, reopening, deleting.
        (await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(dataSource, $"UPDATE pol.policy_term SET state = 'IN_FORCE' WHERE term_id = '{term}' AND recorded_to IS NULL")))
            .SqlState.ShouldBe(PostgresErrorCodes.RestrictViolation);
        (await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(dataSource, $"UPDATE pol.policy_term SET recorded_to = NULL WHERE term_id = '{term}' AND recorded_to IS NOT NULL")))
            .SqlState.ShouldBe(PostgresErrorCodes.RestrictViolation);
        (await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(dataSource, $"DELETE FROM pol.policy_term WHERE term_id = '{term}'")))
            .SqlState.ShouldBe(PostgresErrorCodes.RestrictViolation);

        // The transaction log is append-only for every role.
        (await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(dataSource, $"UPDATE pol.policy_transaction SET total = 0 WHERE transaction_id = '{transaction}'")))
            .SqlState.ShouldBe(PostgresErrorCodes.RestrictViolation);
        (await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(dataSource, $"DELETE FROM pol.policy_transaction WHERE transaction_id = '{transaction}'")))
            .SqlState.ShouldBe(PostgresErrorCodes.RestrictViolation);
    }

    private static async Task ExecuteAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(Ct);
    }
}
