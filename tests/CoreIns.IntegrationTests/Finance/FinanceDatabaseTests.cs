using CoreIns.Modules.Finance.Persistence;
using CoreIns.Modules.Finance.Posting;
using CoreIns.Platform.Context;
using CoreIns.Platform.Events;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static CoreIns.IntegrationTests.Finance.FinanceSlice;

namespace CoreIns.IntegrationTests.Finance;

/// <summary>
/// Database guarantees of the FIN journals on PostgreSQL 17: the deferred double-entry constraint (REQ-FIN-068),
/// append-only journals and rule rows for every role (REQ-FIN-070), app-role privileges, reversal as the only
/// correction (REQ-FIN-072, -073) and out-of-order flagging (D-ARC-26).
/// </summary>
public sealed class FinanceDatabaseTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private ApiHostFactory _factory = null!;
    private FinanceSlice _slice = null!;
    private NpgsqlDataSource _super = null!;
    private NpgsqlDataSource _app = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new ApiHostFactory(database.AppConnectionString);
        _ = _factory.Services;
        _slice = new FinanceSlice(_factory);
        _super = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        _app = NpgsqlDataSource.Create(database.AppConnectionString);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _super.DisposeAsync();
        await _app.DisposeAsync();
        await _factory.DisposeAsync();
    }

    /// <summary>Posts one RECEIVED entry (no policy context needed) and returns its journal id.</summary>
    private async Task<Guid> PostReceiptAsync(string amount = "75.00")
    {
        var account = Guid.CreateVersion7();
        var receipt = Guid.CreateVersion7();
        await _slice.EntryAsync(account, "RECEIVED", "2026-11-03",
            Line("LA-10", "DEBIT", amount, billingAccount: account, receiptId: receipt),
            Line("LA-11", "CREDIT", amount, billingAccount: account, receiptId: receipt));
        await _slice.DrainAsync();
        return await ScalarAsync<Guid>(_super, $"SELECT DISTINCT journal_id FROM fin.journal_line WHERE receipt_id = '{receipt}'");
    }

    private async Task<PostgresException> InsertJournalAsAppAsync(params (string Side, decimal Amount)[] lines)
    {
        var ruleSet = await ScalarAsync<Guid>(_super, "SELECT rule_set_id FROM fin.posting_rule_set WHERE version_no = 1");
        var period = await ScalarAsync<Guid>(_super, "SELECT period_id FROM fin.financial_period LIMIT 1");
        var journal = Guid.CreateVersion7();
        var sql = $"""
            BEGIN;
            INSERT INTO fin.journal_entry (journal_id, journal_number, legal_entity_id, legal_entity_code, jurisdiction, book, accounting_date, business_date,
                period_id, source_type, source_module, source_event_type, source_event_ids, source_ref, rule_set_id, rule_set_version, rule_codes,
                functional_currency, correlation_id, posted_at, posted_by)
            VALUES ('{journal}', 'TEST-{journal:N}', '{ApiHostFactory.LegalEntityId}', 'GR-TEST', 'GR', 'IFRS17', DATE '2026-11-03', DATE '2026-11-03',
                '{period}', 'EVENT', 'BIL', 'BillingEntryPosted', ARRAY[]::uuid[], 'raw', '{ruleSet}', 1, ARRAY['RAW'], 'EUR', 'x', now(), 'test');
            """;
        var no = 0;
        foreach (var (side, amount) in lines)
        {
            sql += $"""
                INSERT INTO fin.journal_line (line_id, journal_id, line_no, legal_entity_id, book, account_code, side, amount, currency, amount_functional,
                    functional_currency, rule_code, business_date)
                VALUES (gen_random_uuid(), '{journal}', {++no}, '{ApiHostFactory.LegalEntityId}', 'IFRS17', 'GL-1110', '{side}', {amount}, 'EUR', {amount}, 'EUR', 'RAW', DATE '2026-11-03');
                """;
        }

        sql += "COMMIT;";
        await using var connection = await _app.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task REQ_FIN_068_an_unbalanced_or_one_line_journal_is_refused_at_commit_by_the_database()
    {
        await PostReceiptAsync();
        (await InsertJournalAsAppAsync(("DEBIT", 10m), ("CREDIT", 9.99m))).SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        (await InsertJournalAsAppAsync(("DEBIT", 10m))).SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        (await ScalarAsync<long>(_super, "SELECT count(*) FROM fin.journal_entry WHERE source_ref = 'raw'")).ShouldBe(0);
    }

    [Fact]
    public async Task REQ_FIN_070_088_journals_and_rules_are_append_only_for_every_role()
    {
        var journal = await PostReceiptAsync();

        // The app role has no UPDATE/DELETE privilege on journals or reference data.
        foreach (var sql in new[]
                 {
                     $"UPDATE fin.journal_line SET amount = amount + 1 WHERE journal_id = '{journal}'",
                     $"DELETE FROM fin.journal_entry WHERE journal_id = '{journal}'",
                     "UPDATE fin.posting_rule SET account_code = 'GL-9999'",
                     "INSERT INTO fin.gl_account (legal_entity_code, book, account_code, name_el, name_en, account_type, normal_balance, code_origin, system_only, status, source) VALUES ('GR-TEST','IFRS17','GL-X','x','x','ASSET','DEBIT','TECHNICAL_PLACEHOLDER',false,'ACTIVE','x')",
                 })
        {
            await using var command = _app.CreateCommand(sql);
            (await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken))).SqlState
                .ShouldBe(PostgresErrorCodes.InsufficientPrivilege, sql);
        }

        // Even the owner/superuser is stopped by the triggers.
        foreach (var sql in new[]
                 {
                     $"UPDATE fin.journal_line SET amount = amount + 1 WHERE journal_id = '{journal}'",
                     $"DELETE FROM fin.journal_line WHERE journal_id = '{journal}'",
                     $"UPDATE fin.journal_entry SET reason = 'edit' WHERE journal_id = '{journal}'",
                     "TRUNCATE fin.journal_line CASCADE",
                     "UPDATE fin.posting_rule SET account_code = 'GL-1110' WHERE rule_code = 'RC-CASH'",
                 })
        {
            (await Should.ThrowAsync<PostgresException>(() => database.ExecuteAsSuperuserAsync(sql, TestContext.Current.CancellationToken))).SqlState
                .ShouldBe(PostgresErrorCodes.ObjectNotInPrerequisiteState, sql);
        }

        (await ScalarAsync<decimal>(_super, $"SELECT sum(amount) FROM fin.journal_line WHERE journal_id = '{journal}'")).ShouldBe(150.00m);
    }

    [Fact]
    public async Task REQ_FIN_002_072_073_a_correction_is_a_mirror_reversal_and_a_journal_is_reversed_once()
    {
        var journal = await PostReceiptAsync("33.30");
        await using (var scope = Scope(out var services))
        {
            var session = services.GetRequiredService<DbSession>();
            await using var transaction = await session.BeginTransactionAsync(TestContext.Current.CancellationToken);
            var reversal = services.GetRequiredService<JournalReversal>();
            var legalEntity = new LegalEntityId(Guid.Parse(ApiHostFactory.LegalEntityId));
            var first = await reversal.ReverseAsync(legalEntity, journal, new BusinessDate(2026, 11, 4), "test correction", TestContext.Current.CancellationToken);
            first.IsSuccess.ShouldBeTrue(first.IsFailure ? first.Error.ToString() : null);
            var second = await reversal.ReverseAsync(legalEntity, journal, new BusinessDate(2026, 11, 4), "again", TestContext.Current.CancellationToken);
            second.IsFailure.ShouldBeTrue();
            second.Error.Code.Value.ShouldBe("FIN-ERR-ALREADY-REVERSED");
            await transaction.CommitAsync(TestContext.Current.CancellationToken);
        }

        (await ScalarAsync<string>(_super, $"""
            SELECT string_agg(l.account_code || ':' || l.side || ':' || l.amount::text, ',' ORDER BY l.line_no)
              FROM fin.journal_entry r JOIN fin.journal_line l USING (journal_id) WHERE r.reverses_journal_id = '{journal}' AND r.source_type = 'REVERSAL'
            """)).ShouldBe("GL-1110:CREDIT:33.3000,GL-2540:DEBIT:33.3000");
        (await ScalarAsync<long>(_super, $"SELECT count(*) FROM fin.journal_entry WHERE reverses_journal_id = '{journal}'")).ShouldBe(1);

        // The unique index stops a second reversal even past the code check.
        var error = await Should.ThrowAsync<PostgresException>(() => database.ExecuteAsSuperuserAsync($"""
            INSERT INTO fin.journal_entry (journal_id, journal_number, legal_entity_id, legal_entity_code, jurisdiction, book, accounting_date,
                business_date, period_id, source_type, source_module, source_event_type, source_event_ids, source_ref, rule_set_id, rule_set_version,
                rule_codes, reverses_journal_id, reason, functional_currency, correlation_id, posted_at, posted_by)
            SELECT gen_random_uuid(), 'DUP-1', legal_entity_id, legal_entity_code, jurisdiction, book, accounting_date,
                business_date, period_id, 'REVERSAL', source_module, source_event_type, source_event_ids, source_ref, rule_set_id, rule_set_version,
                rule_codes, '{journal}', 'dup', functional_currency, correlation_id, posted_at, posted_by
              FROM fin.journal_entry WHERE journal_id = '{journal}'
            """, TestContext.Current.CancellationToken));
        error.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task DARC26_an_event_older_than_one_already_seen_for_its_aggregate_is_flagged_out_of_order()
    {
        var account = Guid.CreateVersion7().ToString();
        var later = Envelope(account, 2);
        var earlier = Envelope(account, 1);
        await using (var scope = Scope(out var services))
        {
            var session = services.GetRequiredService<DbSession>();
            await using var transaction = await session.BeginTransactionAsync(TestContext.Current.CancellationToken);
            var intake = services.GetRequiredService<Intake>();
            (await intake.ReceiveAsync(later, TestContext.Current.CancellationToken))!.OutOfOrder.ShouldBeFalse();
            (await intake.ReceiveAsync(earlier, TestContext.Current.CancellationToken))!.OutOfOrder.ShouldBeTrue();
            (await intake.ReceiveAsync(earlier, TestContext.Current.CancellationToken)).ShouldBeNull("REQ-FIN-031: a redelivery is ignored");
            await transaction.CommitAsync(TestContext.Current.CancellationToken);
        }

        (await ScalarAsync<string>(_super, $"SELECT string_agg(aggregate_sequence || ':' || out_of_order || ':' || status, ',' ORDER BY aggregate_sequence) FROM fin.business_event WHERE aggregate_id = '{account}'"))
            .ShouldBe("1:true:NO_POSTING,2:false:NO_POSTING");
    }

    [Fact]
    public async Task REQ_FIN_032_an_event_for_a_legal_entity_this_stamp_does_not_serve_is_an_intake_exception()
    {
        var account = Guid.CreateVersion7();
        var entry = await _slice.PublishAsync(CoreIns.Modules.Billing.Contracts.Events.BillingEntryPostedV1.Descriptor, "BillingAccount", account.ToString(),
            Sample("bil", "BillingEntryPosted"), BusinessKeys.Empty.With("entryId", Guid.CreateVersion7().ToString()), legalEntity: "ZZ-UNKNOWN");
        await _slice.DrainAsync();
        (await ScalarAsync<string>(_super, $"SELECT status || '/' || exception_reason FROM fin.business_event WHERE source_event_id = '{entry.EventId.Value}'"))
            .ShouldBe("SUSPENDED/INVALID_ENVELOPE");
    }

    private AsyncServiceScope Scope(out IServiceProvider services)
    {
        var scope = _factory.Services.CreateAsyncScope();
        services = scope.ServiceProvider;
        var context = services.GetRequiredService<RequestContext>();
        context.Actor = ActorRef.User("fin-test");
        context.LegalEntity = LegalEntityCode.Parse("GR-TEST");
        context.Jurisdiction = Jurisdiction.Parse("GR");
        context.ConfigurationHash = ConfigurationHash.Parse(new string('d', 64));
        return scope;
    }

    private static EventEnvelope Envelope(string account, long sequence) => new()
    {
        EventId = EventId.New(),
        EventType = EventTypeName.Parse("PaymentReceived"),
        SchemaVersion = "1.0",
        Producer = ModuleCode.BIL,
        AggregateType = "BillingAccount",
        AggregateId = account,
        AggregateSequence = sequence,
        OccurredAt = Instant.FromUtc(2026, 11, 3),
        RecordedAt = Instant.FromUtc(2026, 11, 3),
        LegalEntity = LegalEntityCode.Parse("GR-TEST"),
        Jurisdiction = Jurisdiction.Parse("GR"),
        ConfigurationHash = ConfigurationHash.Parse(new string('e', 64)),
        BusinessKeys = BusinessKeys.Empty.With("billingAccountId", account).With("receiptId", Guid.CreateVersion7().ToString()),
        CorrelationId = RequestContext.CurrentTraceId(),
        Actor = ActorRef.Service("bil-test"),
        Origin = EventOrigin.Live,
        DataClassification = DataClassification.P0,
        Payload = Sample("bil", "PaymentReceived"),
    };
}
