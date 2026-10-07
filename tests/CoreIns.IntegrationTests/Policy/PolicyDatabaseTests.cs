using Npgsql;

namespace CoreIns.IntegrationTests.Policy;

/// <summary>
/// The pol schema's database guarantees (REQ-POL-075, -078, -079 on PostgreSQL 17 per D-ARC-05), probed **as the app
/// role** the api and worker run as: least privilege, append-only transactions, as-known history that only moves forward
/// (no retroactive or successor-less closing of a record period, which would act as a DELETE), bitemporal non-overlap
/// through btree_gist exclusion constraints, and final job states.
/// </summary>
public sealed class PolicyDatabaseTests(PostgresFixture database) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task REQ_POL_075_the_app_role_cannot_update_or_delete_the_transaction_log_or_the_policy()
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

        foreach (var table in new[] { "policy", "policy_transaction", "charge_line" })
        {
            (await Can($"pol.{table}", "UPDATE")).ShouldBeFalse(table);
        }

        (await Can("pol.__ef_migrations_history", "SELECT")).ShouldBeFalse();
    }

    [Fact]
    public async Task REQ_POL_078_079_as_the_app_role_known_history_only_moves_forward()
    {
        await using var dataSource = NpgsqlDataSource.Create(database.AppConnectionString);
        var policy = Guid.CreateVersion7();
        var term = Guid.CreateVersion7();
        var transaction = Guid.CreateVersion7();
        var segment = Guid.CreateVersion7();
        await ExecuteAsync(dataSource, $"""
            INSERT INTO pol.policy (policy_id, legal_entity_id, jurisdiction, policy_number, product_code, policyholder_party_id, recorded_at, created_by, record_version)
            VALUES ('{policy}', '{ApiHostFactory.LegalEntityId}', 'GR', 'POLTEST-{policy:N}', 'P', '{Guid.CreateVersion7()}', now() - interval '1 day', 'test', 1);
            INSERT INTO pol.policy_transaction (transaction_id, policy_id, term_id, job_id, legal_entity_id, kind, sequence, effective_at, recorded_at,
                configuration_hash, artefact_hash, resolution_hash, intent, premium, taxes, total, currency, actor, correlation_id, origin)
            VALUES ('{transaction}', '{policy}', '{term}', '{Guid.CreateVersion7()}', '{ApiHostFactory.LegalEntityId}', 'ISSUANCE', 1,
                '2027-01-01T00:00Z', now() - interval '1 day', 'c', 'a', 'r', jsonb_build_object(), 100, 10, 110, 'EUR', 'test', 'x', 'LIVE');
            INSERT INTO pol.segment (segment_id, term_id, policy_id, transaction_id, legal_entity_id, valid_from, valid_to, recorded_from, snapshot_hash, snapshot)
            VALUES ('{segment}', '{term}', '{policy}', '{transaction}', '{ApiHostFactory.LegalEntityId}', '2027-01-01T00:00Z', '2028-01-01T00:00Z',
                now() - interval '1 day', '{new string('a', 64)}', jsonb_build_object());
            """);
        string Term(Guid id, int number, string from, string to, string recordedFrom) => $"""
            INSERT INTO pol.policy_term (term_version_id, term_id, policy_id, legal_entity_id, term_number, valid_from, valid_to, recorded_from, state,
                product_version, artefact_hash, resolution_hash, configuration_hash, currency, payment_plan_ref, written_date, head_transaction_id, created_by)
            VALUES ('{Guid.CreateVersion7()}', '{id}', '{policy}', '{ApiHostFactory.LegalEntityId}', {number}, '{from}', '{to}', {recordedFrom}, 'SCHEDULED',
                '1.0', 'a', 'r', 'c', 'EUR', 'PLAN', '2026-10-01', '{transaction}', 'test')
            """;

        await ExecuteAsync(dataSource, Term(term, 1, "2027-01-01T00:00Z", "2028-01-01T00:00Z", "now() - interval '1 day'"));

        // Overlapping valid time while both are current in record time: refused by the exclusion constraint.
        var overlap = await Should.ThrowAsync<PostgresException>(() =>
            ExecuteAsync(dataSource, Term(Guid.CreateVersion7(), 2, "2027-06-01T00:00Z", "2028-06-01T00:00Z", "now()")));
        overlap.SqlState.ShouldBe(PostgresErrorCodes.ExclusionViolation);
        overlap.ConstraintName.ShouldBe("ex_policy_term_no_overlap");

        // The adjacent next term (half-open periods) is fine.
        await ExecuteAsync(dataSource, Term(Guid.CreateVersion7(), 2, "2028-01-01T00:00Z", "2029-01-01T00:00Z", "now()"));

        // Review M1: a retroactive close (here 1 µs after the row was recorded) would erase what was known: refused.
        foreach (var table in new[] { "policy_term", "segment" })
        {
            (await Should.ThrowAsync<PostgresException>(() =>
                    ExecuteAsync(dataSource, $"UPDATE pol.{table} SET recorded_to = recorded_from + interval '1 microsecond' WHERE term_id = '{term}' AND recorded_to IS NULL")))
                .SqlState.ShouldBe(PostgresErrorCodes.RestrictViolation, table);
        }

        // Closing now without a successor version or a recorded termination: refused when the transaction commits.
        (await Should.ThrowAsync<PostgresException>(() =>
                ExecuteAsync(dataSource, $"UPDATE pol.segment SET recorded_to = transaction_timestamp() WHERE segment_id = '{segment}'")))
            .SqlState.ShouldBe(PostgresErrorCodes.RestrictViolation);

        // Supersession: close now and record the successor at the same instant, in one transaction.
        await ExecuteAsync(dataSource, $"""
            BEGIN;
            UPDATE pol.policy_term SET recorded_to = transaction_timestamp() WHERE term_id = '{term}' AND recorded_to IS NULL;
            {Term(term, 1, "2027-01-01T00:00Z", "2028-01-01T00:00Z", "transaction_timestamp()")};
            COMMIT;
            """);
        await using (var versions = dataSource.CreateCommand(
                         $"SELECT count(*) FILTER (WHERE recorded_to IS NULL), count(*) FROM pol.policy_term WHERE term_id = '{term}'"))
        await using (var reader = await versions.ExecuteReaderAsync(Ct))
        {
            (await reader.ReadAsync(Ct)).ShouldBeTrue();
            reader.GetInt64(0).ShouldBe(1);
            reader.GetInt64(1).ShouldBe(2);
        }

        // Content changes, reopening and deletes are refused.
        (await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(dataSource, $"UPDATE pol.policy_term SET state = 'IN_FORCE' WHERE term_id = '{term}' AND recorded_to IS NULL")))
            .SqlState.ShouldBe(PostgresErrorCodes.RestrictViolation);
        (await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(dataSource, $"UPDATE pol.policy_term SET recorded_to = NULL WHERE term_id = '{term}' AND recorded_to IS NOT NULL")))
            .SqlState.ShouldBe(PostgresErrorCodes.RestrictViolation);
        (await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(dataSource, $"DELETE FROM pol.policy_term WHERE term_id = '{term}'")))
            .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        (await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(dataSource, $"UPDATE pol.policy_transaction SET total = 0 WHERE transaction_id = '{transaction}'")))
            .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        (await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(dataSource, $"UPDATE pol.policy SET product_code = 'X' WHERE policy_id = '{policy}'")))
            .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);

        // The append-only trigger holds for every role, the owner included.
        await using var superuser = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        (await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(superuser, $"DELETE FROM pol.policy_transaction WHERE transaction_id = '{transaction}'")))
            .SqlState.ShouldBe(PostgresErrorCodes.RestrictViolation);
    }

    [Fact]
    public async Task REQ_POL_004_a_job_never_leaves_a_terminal_state()
    {
        await using var dataSource = NpgsqlDataSource.Create(database.AppConnectionString);
        var job = Guid.CreateVersion7();
        await ExecuteAsync(dataSource, $"""
            INSERT INTO pol.job (job_id, legal_entity_id, jurisdiction, job_number, job_type, state, referred, policy_id, policyholder_party_id, product_code,
                product_version, artefact_hash, resolution_hash, resolution_manifest, channel, quote_type, effective_at, expiration_at, currency,
                current_version_no, record_version, created_at, created_by, updated_at)
            VALUES ('{job}', '{ApiHostFactory.LegalEntityId}', 'GR', 'QTEST-{job:N}', 'SUBMISSION', 'BOUND', false, '{Guid.CreateVersion7()}', '{Guid.CreateVersion7()}',
                'P', '1.0', 'a', 'r', jsonb_build_object(), 'STAFF', 'FULL', now(), now() + interval '1 year', 'EUR', 1, 1, now(), 'test', now());
            """);
        (await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(dataSource, $"UPDATE pol.job SET state = 'QUOTED' WHERE job_id = '{job}'")))
            .SqlState.ShouldBe(PostgresErrorCodes.RestrictViolation);
        await ExecuteAsync(dataSource, $"UPDATE pol.job SET referred = true WHERE job_id = '{job}'"); // other columns may still change
    }

    private static async Task ExecuteAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(Ct);
    }
}
