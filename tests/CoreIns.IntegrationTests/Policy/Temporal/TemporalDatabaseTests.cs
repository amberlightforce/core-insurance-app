using Npgsql;

namespace CoreIns.IntegrationTests.Policy.Temporal;

/// <summary>
/// The database half of D-SL3-03, probed as the app role the api and worker run as (and, where a privilege would hide the
/// trigger, as the owner): the frozen policy row, the stamp every record row must carry, record periods that only close at the
/// watermark of the closing command, and the one-open-job partial unique indexes (D-SL3-11).
/// </summary>
public sealed class TemporalDatabaseTests(PostgresFixture database) : IClassFixture<PostgresFixture>
{
    private const string T0 = "2026-10-01T00:00:00Z";
    private const string T1 = "2026-10-02T00:00:00Z";
    private const string T2 = "2026-10-03T00:00:00Z";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Raw(Guid Policy, Guid Term, Guid Transaction, Guid Segment);

    private static async Task ExecuteAsync(NpgsqlDataSource source, string sql)
    {
        await using var command = source.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlDataSource source, string sql)
    {
        await using var command = source.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }

    /// <summary>A policy with a term, an issuance transaction and a segment, all recorded at T0 (the policy starts with watermark T0).</summary>
    private static async Task<Raw> SeedAsync(NpgsqlDataSource app)
    {
        var raw = new Raw(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        await ExecuteAsync(app, $"""
            INSERT INTO pol.policy (policy_id, legal_entity_id, jurisdiction, policy_number, product_code, policyholder_party_id, recorded_at, created_by, record_version)
            VALUES ('{raw.Policy}', '{ApiHostFactory.LegalEntityId}', 'GR', 'POLTEST-{raw.Policy:N}', 'P', '{Guid.CreateVersion7()}', '{T0}', 'test', 1);
            INSERT INTO pol.policy_transaction (transaction_id, policy_id, term_id, job_id, legal_entity_id, kind, sequence, effective_at, recorded_at,
                configuration_hash, artefact_hash, resolution_hash, intent, premium, taxes, total, currency, actor, correlation_id, origin)
            VALUES ('{raw.Transaction}', '{raw.Policy}', '{raw.Term}', '{Guid.CreateVersion7()}', '{ApiHostFactory.LegalEntityId}', 'ISSUANCE', 1,
                '2027-01-01T00:00Z', '{T0}', 'c', 'a', 'r', jsonb_build_object(), 100, 10, 110, 'EUR', 'test', 'x', 'LIVE');
            INSERT INTO pol.segment (segment_id, term_id, policy_id, transaction_id, legal_entity_id, valid_from, valid_to, recorded_from, snapshot_hash, snapshot)
            VALUES ('{raw.Segment}', '{raw.Term}', '{raw.Policy}', '{raw.Transaction}', '{ApiHostFactory.LegalEntityId}', '2027-01-01T00:00Z', '2028-01-01T00:00Z',
                '{T0}', '{new string('a', 64)}', jsonb_build_object());
            """);
        return raw;
    }

    private static async Task MoveWatermarkAsync(NpgsqlDataSource app, Raw raw, string to) =>
        await ExecuteAsync(app, $"UPDATE pol.policy SET last_recorded_at = '{to}', record_version = record_version + 1 WHERE policy_id = '{raw.Policy}'");

    /// <summary>What PolicyWriteLock does inside a transaction: advance the watermark (an UPDATE, so the row belongs to this transaction).</summary>
    private static string Bump(Raw raw, string to) =>
        $"UPDATE pol.policy SET last_recorded_at = '{to}', record_version = record_version + 1 WHERE policy_id = '{raw.Policy}'";

    private static string Transaction(Raw raw, int sequence, string recordedAt) => $"""
        INSERT INTO pol.policy_transaction (transaction_id, policy_id, term_id, job_id, legal_entity_id, kind, sequence, effective_at, recorded_at,
            configuration_hash, artefact_hash, resolution_hash, intent, premium, taxes, total, currency, actor, correlation_id, origin)
        VALUES ('{Guid.CreateVersion7()}', '{raw.Policy}', '{raw.Term}', '{Guid.CreateVersion7()}', '{ApiHostFactory.LegalEntityId}', 'CHANGE', {sequence},
            '2027-01-01T00:00Z', '{recordedAt}', 'c', 'a', 'r', jsonb_build_object(), 0, 0, 0, 'EUR', 'test', 'x', 'LIVE')
        """;

    // ---- the freeze trigger ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task REQ_POL_079_the_freeze_refuses_content_updates_and_deletes_as_the_app_role_and_as_the_owner()
    {
        await using var app = NpgsqlDataSource.Create(database.AppConnectionString);
        await using var owner = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        var raw = await SeedAsync(app);
        var where = $"WHERE policy_id = '{raw.Policy}'";

        // As the app role: the grant leaves only the two watermark columns writable, so content updates and deletes never reach a row.
        foreach (var column in new[] { "policy_number = 'X'", "product_code = 'X'", "jurisdiction = 'XX'", "policyholder_party_id = gen_random_uuid()",
                     "legal_entity_id = gen_random_uuid()", "recorded_at = now()", "created_by = 'x'", "account_id = gen_random_uuid()" })
        {
            (await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(app, $"UPDATE pol.policy SET {column} {where}")))
                .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege, column);
        }

        (await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(app, $"DELETE FROM pol.policy {where}")))
            .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);

        // As the owner (privileges out of the way) the trigger itself refuses the same.
        foreach (var column in new[] { "policy_number = 'X'", "product_code = 'X'", "jurisdiction = 'XX'", "policyholder_party_id = gen_random_uuid()",
                     "recorded_at = recorded_at + interval '1 second'", "created_by = 'x'", "account_id = gen_random_uuid()" })
        {
            (await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(owner, $"UPDATE pol.policy SET {column} {where}")))
                .SqlState.ShouldBe(PostgresErrorCodes.RestrictViolation, column);
        }

        // A content change smuggled in beside the allowed columns is refused too.
        (await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(owner,
                $"UPDATE pol.policy SET product_code = 'X', last_recorded_at = '{T1}', record_version = record_version + 1 {where}")))
            .SqlState.ShouldBe(PostgresErrorCodes.RestrictViolation);
        (await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(owner, $"DELETE FROM pol.policy {where}")))
            .SqlState.ShouldBe(PostgresErrorCodes.RestrictViolation);

        // Nothing changed.
        (await ScalarAsync<string>(owner, $"SELECT product_code FROM pol.policy {where}")).ShouldBe("P");
        (await ScalarAsync<DateTime>(owner, $"SELECT last_recorded_at FROM pol.policy {where}")).ToUniversalTime().ShouldBe(DateTime.Parse(T0).ToUniversalTime());
    }

    [Fact]
    public async Task REQ_POL_079_the_watermark_moves_forward_only_as_the_app_role()
    {
        await using var app = NpgsqlDataSource.Create(database.AppConnectionString);
        var raw = await SeedAsync(app);

        await MoveWatermarkAsync(app, raw, T1);

        // Not back, and not standing still (a writer's t is strictly greater).
        foreach (var value in new[] { T0, T1 })
        {
            (await Should.ThrowAsync<PostgresException>(() => MoveWatermarkAsync(app, raw, value)))
                .SqlState.ShouldBe(PostgresErrorCodes.RestrictViolation, value);
        }

        await MoveWatermarkAsync(app, raw, T2);
    }

    [Fact]
    public async Task REQ_POL_079_a_new_policy_must_start_with_its_watermark_at_its_recorded_at()
    {
        await using var app = NpgsqlDataSource.Create(database.AppConnectionString);
        var policy = Guid.CreateVersion7();
        var insert = (string watermark) => $"""
            INSERT INTO pol.policy (policy_id, legal_entity_id, jurisdiction, policy_number, product_code, policyholder_party_id, recorded_at, last_recorded_at, created_by, record_version)
            VALUES ('{policy}', '{ApiHostFactory.LegalEntityId}', 'GR', 'POLTEST-{policy:N}', 'P', '{Guid.CreateVersion7()}', '{T1}', '{watermark}', 'test', 1)
            """;

        (await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(app, insert(T2)))).SqlState.ShouldBe(PostgresErrorCodes.RestrictViolation);
        await ExecuteAsync(app, insert(T1));
    }

    [Fact]
    public async Task REQ_POL_079_D4_the_watermark_cannot_run_away_from_the_database_clock()
    {
        await using var app = NpgsqlDataSource.Create(database.AppConnectionString);
        var raw = await SeedAsync(app);

        // Not to the year 2100 (it would poison every later record time of the policy), not even five years ahead.
        foreach (var to in new[] { "'2100-01-01T00:00:00Z'::timestamptz", "now() + interval '5 years'" })
        {
            (await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(app,
                    $"UPDATE pol.policy SET last_recorded_at = {to}, record_version = record_version + 1 WHERE policy_id = '{raw.Policy}'")))
                .SqlState.ShouldBe(PostgresErrorCodes.RestrictViolation, to);
        }

        // A new policy cannot start there either.
        var policy = Guid.CreateVersion7();
        (await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(app, $"""
            INSERT INTO pol.policy (policy_id, legal_entity_id, jurisdiction, policy_number, product_code, policyholder_party_id, recorded_at, created_by, record_version)
            VALUES ('{policy}', '{ApiHostFactory.LegalEntityId}', 'GR', 'POLTEST-{policy:N}', 'P', '{Guid.CreateVersion7()}', '2100-01-01T00:00:00Z', 'test', 1)
            """))).SqlState.ShouldBe(PostgresErrorCodes.RestrictViolation);

        // A bounded skew (an application clock a little ahead) is fine.
        await ExecuteAsync(app, $"UPDATE pol.policy SET last_recorded_at = now() + interval '1 hour', record_version = record_version + 1 WHERE policy_id = '{raw.Policy}'");

        // The Development relaxation: the application clock runs ahead of the database clock by the dev clock offset (plt.dev_clock), so
        // the cap follows it. Thirty days ahead is refused until the dev clock has been advanced that far, then accepted.
        var thirtyDays = $"UPDATE pol.policy SET last_recorded_at = now() + interval '30 days', record_version = record_version + 1 WHERE policy_id = '{raw.Policy}'";
        (await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(app, thirtyDays))).SqlState.ShouldBe(PostgresErrorCodes.RestrictViolation);
        await using var owner = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await ExecuteAsync(owner, "UPDATE plt.dev_clock SET offset_micros = 31 * 86400 * 1000000::bigint, version = version + 1");
        await ExecuteAsync(app, thirtyDays);
    }

    // ---- stamping -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task REQ_POL_079_a_record_row_stamped_otherwise_than_exactly_at_the_watermark_is_refused()
    {
        await using var app = NpgsqlDataSource.Create(database.AppConnectionString);
        var raw = await SeedAsync(app); // the seed rows were stamped T0 = the watermark at the time: accepted
        await MoveWatermarkAsync(app, raw, T1);

        // A writer that skipped the protocol (stamp = its own clock, behind or ahead of the watermark): refused, for every record table.
        foreach (var stamp in new[] { T0, T2, "2026-10-02T00:00:00.000001Z", "2026-10-01T23:59:59.999999Z" })
        {
            (await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(app, Transaction(raw, 2, stamp)))).SqlState.ShouldBe(PostgresErrorCodes.RestrictViolation, "transaction " + stamp);
            (await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(app, $"""
                INSERT INTO pol.segment (segment_id, term_id, policy_id, transaction_id, legal_entity_id, valid_from, valid_to, recorded_from, snapshot_hash, snapshot)
                VALUES (gen_random_uuid(), '{raw.Term}', '{raw.Policy}', '{raw.Transaction}', '{ApiHostFactory.LegalEntityId}', '2028-01-01T00:00Z', '2029-01-01T00:00Z',
                    '{stamp}', '{new string('a', 64)}', jsonb_build_object())
                """))).SqlState.ShouldBe(PostgresErrorCodes.RestrictViolation, "segment " + stamp);
            (await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(app, $"""
                INSERT INTO pol.policy_term (term_version_id, term_id, policy_id, legal_entity_id, term_number, valid_from, valid_to, recorded_from, state,
                    product_version, artefact_hash, resolution_hash, configuration_hash, currency, payment_plan_ref, written_date, head_transaction_id, created_by)
                VALUES (gen_random_uuid(), gen_random_uuid(), '{raw.Policy}', '{ApiHostFactory.LegalEntityId}', 2, '2029-01-01T00:00Z', '2030-01-01T00:00Z', '{stamp}', 'SCHEDULED',
                    '1.0', 'a', 'r', 'c', 'EUR', 'PLAN', '2026-10-01', '{raw.Transaction}', 'test')
                """))).SqlState.ShouldBe(PostgresErrorCodes.RestrictViolation, "term " + stamp);
        }

        // Exactly the watermark, but by a writer that did not advance it in this transaction (D1: it reuses a committed watermark,
        // or only took SELECT ... FOR UPDATE): refused for every record table.
        (await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(app, Transaction(raw, 2, T1)))).SqlState.ShouldBe(PostgresErrorCodes.RestrictViolation, "unlocked transaction");
        (await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(app, $"""
            SELECT 1 FROM pol.policy WHERE policy_id = '{raw.Policy}' FOR UPDATE;
            {Transaction(raw, 2, T1)};
            """))).SqlState.ShouldBe(PostgresErrorCodes.RestrictViolation, "select for update is not the stamp");
        (await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(app, $"""
            INSERT INTO pol.segment (segment_id, term_id, policy_id, transaction_id, legal_entity_id, valid_from, valid_to, recorded_from, snapshot_hash, snapshot)
            VALUES (gen_random_uuid(), '{raw.Term}', '{raw.Policy}', '{raw.Transaction}', '{ApiHostFactory.LegalEntityId}', '2028-01-01T00:00Z', '2029-01-01T00:00Z',
                '{T1}', '{new string('a', 64)}', jsonb_build_object())
            """))).SqlState.ShouldBe(PostgresErrorCodes.RestrictViolation, "unlocked segment");

        // Advancing the watermark and writing at it in one transaction (lock-then-stamp): accepted.
        await ExecuteAsync(app, $"{Bump(raw, T2)}; {Transaction(raw, 2, T2)}");
    }

    [Fact]
    public async Task REQ_POL_079_D1_an_unlocked_writer_cannot_reuse_a_committed_watermark_to_rewrite_what_a_reference_pinned()
    {
        await using var app = NpgsqlDataSource.Create(database.AppConnectionString);
        var raw = await SeedAsync(app);
        await MoveWatermarkAsync(app, raw, T1); // committed by an earlier command: W = T1
        var current = $"WHERE segment_id = '{raw.Segment}'";

        // The reviewer's probe: no bump in this transaction; insert a transaction at W, close the segment at W, re-stamp a successor at W.
        var probe = $"""
            BEGIN;
            {Transaction(raw, 2, T1)};
            UPDATE pol.segment SET recorded_to = '{T1}' {current};
            INSERT INTO pol.segment (segment_id, term_id, policy_id, transaction_id, legal_entity_id, valid_from, valid_to, recorded_from, snapshot_hash, snapshot)
            SELECT gen_random_uuid(), term_id, policy_id, transaction_id, legal_entity_id, valid_from, valid_to, '{T1}', '{new string('b', 64)}', snapshot
              FROM pol.segment {current};
            COMMIT;
            """;
        (await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(app, probe))).SqlState.ShouldBe(PostgresErrorCodes.RestrictViolation);

        (await ScalarAsync<long>(app, $"SELECT count(*) FROM pol.segment WHERE term_id = '{raw.Term}'")).ShouldBe(1);
        (await ScalarAsync<long>(app, $"SELECT count(*) FROM pol.segment {current} AND recorded_to IS NULL")).ShouldBe(1);
        (await ScalarAsync<long>(app, $"SELECT count(*) FROM pol.policy_transaction WHERE policy_id = '{raw.Policy}'")).ShouldBe(1);
    }

    // ---- record periods never close retroactively -----------------------------------------------------------------------------

    [Fact]
    public async Task REQ_POL_078_PITFALL_17_a_record_period_closes_only_at_the_watermark_of_the_closing_command_with_a_successor()
    {
        await using var app = NpgsqlDataSource.Create(database.AppConnectionString);
        var raw = await SeedAsync(app);
        await MoveWatermarkAsync(app, raw, T1);
        var current = $"WHERE segment_id = '{raw.Segment}'";

        // Never in the past (here: the instant the row was recorded plus 1 µs), never ahead of the watermark, never without a successor.
        foreach (var closeAt in new[] { "2026-10-01T00:00:00.000001Z", T0, "2026-10-01T12:00:00Z", T2 })
        {
            (await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(app, $"UPDATE pol.segment SET recorded_to = '{closeAt}' {current}")))
                .SqlState.ShouldBe(PostgresErrorCodes.RestrictViolation, closeAt);
        }

        // Closing at the committed watermark without having advanced it in this transaction (D1) is refused, successor or not.
        (await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(app, $"UPDATE pol.segment SET recorded_to = '{T1}' {current}")))
            .SqlState.ShouldBe(PostgresErrorCodes.RestrictViolation, "unlocked close at the committed watermark");

        // Locked, at the new watermark, but without recording a successor: refused at commit.
        (await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(app, $"{Bump(raw, T2)}; UPDATE pol.segment SET recorded_to = '{T2}' {current}")))
            .SqlState.ShouldBe(PostgresErrorCodes.RestrictViolation, "closing at the watermark without recording a successor");

        // Lock-then-stamp, at the watermark of this command, together with the successor and the transaction that carries it: accepted.
        await ExecuteAsync(app, $"""
            BEGIN;
            {Bump(raw, T2)};
            {Transaction(raw, 2, T2)};
            UPDATE pol.segment SET recorded_to = '{T2}' {current};
            INSERT INTO pol.segment (segment_id, term_id, policy_id, transaction_id, legal_entity_id, valid_from, valid_to, recorded_from, snapshot_hash, snapshot)
            SELECT gen_random_uuid(), term_id, policy_id, (SELECT transaction_id FROM pol.policy_transaction WHERE term_id = '{raw.Term}' AND sequence = 2),
                   legal_entity_id, valid_from, valid_to, '{T2}', snapshot_hash, snapshot
              FROM pol.segment {current};
            COMMIT;
            """);

        // The closed row is final: not moved earlier or later, not reopened, not deleted, and its start was never rewritten.
        foreach (var statement in new[]
                 {
                     $"UPDATE pol.segment SET recorded_to = '{T0}' {current}",
                     $"UPDATE pol.segment SET recorded_to = '{T1}' {current}",
                     $"UPDATE pol.segment SET recorded_to = NULL {current}",
                     $"UPDATE pol.segment SET recorded_from = '{T1}' {current}",
                 })
        {
            (await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(app, statement))).SqlState.ShouldBe(PostgresErrorCodes.RestrictViolation, statement);
        }

        (await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(app, $"DELETE FROM pol.segment {current}")))
            .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        (await ScalarAsync<long>(app, $"SELECT count(*) FROM pol.segment WHERE term_id = '{raw.Term}' AND recorded_to IS NULL")).ShouldBe(1);
        (await ScalarAsync<DateTime>(app, $"SELECT recorded_to FROM pol.segment {current}")).ToUniversalTime().ShouldBe(DateTime.Parse(T2).ToUniversalTime());
        (await ScalarAsync<DateTime>(app, $"SELECT recorded_from FROM pol.segment {current}")).ToUniversalTime().ShouldBe(DateTime.Parse(T0).ToUniversalTime());
    }

    // ---- one open job per term --------------------------------------------------------------------------------------------------

    private static string Job(string type, string state, Guid? targetTerm = null, Guid? expiringTerm = null) => $"""
        INSERT INTO pol.job (job_id, legal_entity_id, jurisdiction, job_number, job_type, state, referred, policy_id, policyholder_party_id, product_code,
            product_version, artefact_hash, resolution_hash, resolution_manifest, channel, quote_type, effective_at, expiration_at, currency,
            current_version_no, record_version, created_at, created_by, updated_at, target_term_id, expiring_term_id)
        VALUES (gen_random_uuid(), '{ApiHostFactory.LegalEntityId}', 'GR', 'JTEST-' || replace(gen_random_uuid()::text, '-', ''), '{type}', '{state}', false,
            gen_random_uuid(), gen_random_uuid(), 'P', '1.0', 'a', 'r', jsonb_build_object(), 'STAFF', 'FULL', now(), now() + interval '1 year', 'EUR',
            1, 1, now(), 'test', now(), {(targetTerm is { } t ? $"'{t}'" : "NULL")}, {(expiringTerm is { } e ? $"'{e}'" : "NULL")})
        """;

    [Theory]
    [InlineData("POLICY_CHANGE", "DRAFT", "QUOTED", "ux_job_open_servicing")]
    [InlineData("CANCELLATION", "QUOTED", "DRAFT", "ux_job_open_servicing")]
    [InlineData("POLICY_CHANGE", "SCHEDULED", "SCHEDULED", "ux_job_open_servicing")]
    [InlineData("CANCELLATION", "DRAFT", "DRAFT", "ux_job_open_servicing")]
    [InlineData("RENEWAL", "DRAFT", "SCHEDULED", "ux_job_open_renewal")]
    [InlineData("RENEWAL", "QUOTED", "QUOTED", "ux_job_open_renewal")]
    public async Task REQ_POL_086_D_SL3_11_a_second_open_change_cancellation_or_renewal_per_term_is_rejected(string type, string first, string second, string index)
    {
        await using var app = NpgsqlDataSource.Create(database.AppConnectionString);
        var term = Guid.CreateVersion7();
        var otherTerm = Guid.CreateVersion7();
        string Insert(string state, Guid on) => type == "RENEWAL" ? Job(type, state, expiringTerm: on) : Job(type, state, targetTerm: on);

        await ExecuteAsync(app, Insert(first, term));
        var duplicate = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(app, Insert(second, term)));
        duplicate.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
        duplicate.ConstraintName.ShouldBe(index);

        // Another term is independent; and once the first is no longer open, a new one may start on the same term.
        await ExecuteAsync(app, Insert(second, otherTerm));
        await ExecuteAsync(app, $"UPDATE pol.job SET state = 'BOUND' WHERE state = '{first}' AND {(type == "RENEWAL" ? "expiring_term_id" : "target_term_id")} = '{term}' AND job_type = '{type}'");
        await ExecuteAsync(app, Insert(second, term));
    }

    [Theory]
    [InlineData("POLICY_CHANGE")]
    [InlineData("CANCELLATION")]
    [InlineData("RENEWAL")]
    public async Task REQ_POL_086_a_change_cancellation_or_renewal_without_its_term_is_rejected(string type)
    {
        await using var app = NpgsqlDataSource.Create(database.AppConnectionString);

        var missing = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(app, Job(type, "DRAFT")));
        missing.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        missing.ConstraintName.ShouldBe("ck_job_target_term");

        // The renewal's term is the expiring one; a change or cancellation's is the target.
        var wrong = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(app,
            type == "RENEWAL" ? Job(type, "DRAFT", targetTerm: Guid.CreateVersion7()) : Job(type, "DRAFT", expiringTerm: Guid.CreateVersion7())));
        wrong.ConstraintName.ShouldBe("ck_job_target_term");

        await ExecuteAsync(app, type == "RENEWAL" ? Job(type, "DRAFT", expiringTerm: Guid.CreateVersion7()) : Job(type, "DRAFT", targetTerm: Guid.CreateVersion7()));
    }

    [Fact]
    public async Task REQ_POL_086_D_SL3_11_a_change_and_a_cancellation_may_be_open_on_the_same_term_at_once_and_terminal_jobs_do_not_count()
    {
        await using var app = NpgsqlDataSource.Create(database.AppConnectionString);
        var term = Guid.CreateVersion7();

        await ExecuteAsync(app, Job("POLICY_CHANGE", "DRAFT", targetTerm: term));
        await ExecuteAsync(app, Job("CANCELLATION", "DRAFT", targetTerm: term));
        await ExecuteAsync(app, Job("RENEWAL", "DRAFT", expiringTerm: term));

        // Terminal jobs do not count: any number of them next to the open one.
        await ExecuteAsync(app, Job("POLICY_CHANGE", "BOUND", targetTerm: term));
        await ExecuteAsync(app, Job("POLICY_CHANGE", "BOUND", targetTerm: term));
    }
}
