using System.Text.Json.Nodes;
using CoreIns.IntegrationTests.Party;
using CoreIns.Modules.Claims.Contracts.Events;
using CoreIns.Modules.Finance.Domain;
using CoreIns.Modules.Finance.Persistence;
using CoreIns.Modules.Reinsurance.Contracts.Events;
using CoreIns.Platform.Events;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static CoreIns.IntegrationTests.Finance.FinanceSlice;

namespace CoreIns.IntegrationTests.Finance.V4;

/// <summary>
/// SL4-FIN-V4 on a real PostgreSQL 17 through the real Host (REQ-FIN-037, -159, -160, -164, -299, BR-FIN-097, GF-05/GF-10 subsets):
/// recovery reserves, recoveries by type, the recovery clearing and GL-2515 netting, RI recoverables on paid and outstanding, payment
/// voids by reversal, fail-closed cases and rule set v4 next to v1-v3. Amounts are the PRD examples (illustrative, D-SL4-20).
/// </summary>
public sealed class FinanceV4Tests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Day = "2026-11-05";
    private ApiHostFactory _factory = null!;
    private FinanceSlice _slice = null!;
    private NpgsqlDataSource _db = null!;
    private NpgsqlDataSource _app = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new ApiHostFactory(database.AppConnectionString);
        _ = _factory.CreateClient();
        _slice = new FinanceSlice(_factory);
        _db = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        _app = NpgsqlDataSource.Create(database.AppConnectionString);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        await _app.DisposeAsync();
        await _factory.DisposeAsync();
    }

    // ---- event builders -------------------------------------------------------------------------------------------------------

    private static JsonObject Eur3(string amount) => new()
    {
        ["transaction"] = new JsonObject { ["amount"] = amount, ["currency"] = "EUR" },
        ["functional"] = new JsonObject { ["amount"] = amount, ["currency"] = "EUR" },
        ["group"] = new JsonObject { ["amount"] = amount, ["currency"] = "EUR" },
    };

    private static JsonObject Eur4(string amount) => new()
    {
        ["transaction"] = new JsonObject { ["amount"] = amount, ["currency"] = "EUR" },
        ["functional"] = new JsonObject { ["amount"] = amount, ["currency"] = "EUR" },
        ["group"] = new JsonObject { ["amount"] = amount, ["currency"] = "EUR" },
        ["contract"] = new JsonObject { ["amount"] = amount, ["currency"] = "EUR" },
    };

    private Task<EventEnvelope> RecoveryReserveAsync(Guid claim, Guid? recovery, string delta, string newOpen)
    {
        var payload = Sample("clm", "ReserveChanged");
        var setId = Guid.CreateVersion7();
        var term = Guid.CreateVersion7();
        payload["claimId"] = claim.ToString();
        payload["exposureId"] = Guid.CreateVersion7().ToString();
        payload["reserveLineId"] = Guid.CreateVersion7().ToString();
        payload["reserveLine"] = new JsonObject { ["costType"] = "RECOVERY", ["category"] = "SUBROGATION" };
        payload["kind"] = "RECOVERY_RESERVE";
        payload["delta"] = Eur3(delta);
        payload["newOpenAmount"] = Eur3(newOpen);
        payload["setId"] = setId.ToString();
        payload["accidentDate"] = "2026-11-01";
        payload["policyTermId"] = term.ToString();
        payload["productCode"] = "MOTOR-GR";
        payload["siiLob"] = "UNMAPPED";
        payload["ifrs17GroupRef"] = null;
        payload["catCode"] = null;
        payload["handlingSegment"] = "STANDARD";
        payload["accountingDate"] = Day;
        payload["recoveryId"] = recovery?.ToString();
        return _slice.PublishAsync(ReserveChangedV1.Descriptor, "Claim", claim.ToString(), payload,
            BusinessKeys.Empty.With("claimId", claim.ToString()).With("exposureId", payload["exposureId"]!.GetValue<string>())
                .With("setId", setId.ToString()).With("policyTermId", term.ToString()));
    }

    private Task<EventEnvelope> RecoveryAsync(Guid claim, Guid recovery, string type, Guid counterparty, string amount, Guid? statement = null, Action<JsonObject>? edit = null)
    {
        var payload = new JsonObject
        {
            ["recoveryId"] = recovery.ToString(),
            ["recoveryType"] = type,
            ["counterpartyPartyId"] = counterparty.ToString(),
            ["amount"] = Eur3(amount),
            ["allocationByLine"] = new JsonArray(new JsonObject { ["lineKey"] = "L1", ["amount"] = Eur3(amount) }),
            ["claimId"] = claim.ToString(),
            ["accountingDate"] = Day,
            ["receivableId"] = null,
            ["fsStatementId"] = statement?.ToString(),
            ["evidenceRef"] = null,
        };
        edit?.Invoke(payload);
        return _slice.PublishAsync(RecoveryRecordedV1.Descriptor, "Claim", claim.ToString(), payload,
            BusinessKeys.Empty.With("claimId", claim.ToString()).With("recoveryId", recovery.ToString()));
    }

    private Task<EventEnvelope> PaymentAsync(Guid claim, Guid payment, string amount, string method, Guid? statement = null, Guid? payee = null, Guid? disbursement = null, Action<JsonObject>? edit = null)
    {
        var transaction = Guid.CreateVersion7();
        var payload = Sample("clm", "PaymentIssued");
        payload["claimId"] = claim.ToString();
        payload["paymentId"] = payment.ToString();
        payload["transactionIds"] = new JsonArray(JsonValue.Create(transaction.ToString()));
        payload["lines"] = new JsonArray(new JsonObject
        {
            ["lineKey"] = transaction.ToString(),
            ["amount"] = Eur3(amount),
            ["reserveLineId"] = Guid.CreateVersion7().ToString(),
            ["exposureId"] = Guid.CreateVersion7().ToString(),
            ["costType"] = "INDEMNITY",
            ["costCategory"] = "VEHICLE_REPAIR",
            ["eroding"] = method != "CLEARING",
        });
        payload["amount"] = Eur3(amount);
        payload["payeePartyId"] = (payee ?? Guid.CreateVersion7()).ToString();
        payload["method"] = method;
        payload["disbursementId"] = disbursement?.ToString();
        payload["exGratia"] = false;
        payload["complaintRef"] = null;
        payload["accountingDate"] = Day;
        payload["fsStatementId"] = statement?.ToString();
        edit?.Invoke(payload);
        var keys = BusinessKeys.Empty.With("claimId", claim.ToString()).With("paymentId", payment.ToString());
        keys = disbursement is { } d ? keys.With("disbursementId", d.ToString()) : keys.With("fsStatementId", (statement ?? Guid.CreateVersion7()).ToString());
        return _slice.PublishAsync(PaymentIssuedV1.Descriptor, "Claim", claim.ToString(), payload, keys);
    }

    private Task<EventEnvelope> VoidAsync(Guid claim, Guid payment, string? amount = null)
    {
        var payload = new JsonObject
        {
            ["paymentId"] = payment.ToString(),
            ["reversalTransactionIds"] = new JsonArray(),
            ["reason"] = "VOIDED",
            ["claimId"] = claim.ToString(),
            ["amount"] = amount is null ? null : Eur3(amount),
        };
        return _slice.PublishAsync(PaymentVoidedV1.Descriptor, "Claim", claim.ToString(), payload,
            BusinessKeys.Empty.With("claimId", claim.ToString()).With("paymentId", payment.ToString()));
    }

    private Task<EventEnvelope> RiAsync(Guid contract, Guid participant, string incurred, string paid, string outstanding, int? year = 2026, Action<JsonObject>? edit = null)
    {
        var batch = Guid.CreateVersion7();
        var claim = Guid.CreateVersion7();
        var payload = new JsonObject
        {
            ["batchId"] = batch.ToString(),
            ["recoveryIds"] = new JsonArray(JsonValue.Create(Guid.CreateVersion7().ToString())),
            ["occurrence"] = "OCC-1",
            ["claimIds"] = new JsonArray(JsonValue.Create(claim.ToString())),
            ["riContractId"] = contract.ToString(),
            ["layerId"] = "L1",
            ["participantPartyId"] = participant.ToString(),
            ["incurredDelta"] = Eur4(incurred),
            ["paidDelta"] = Eur4(paid),
            ["outstandingDelta"] = Eur4(outstanding),
            ["claimLevelTotals"] = new JsonArray(),
            ["postingKey"] = Guid.CreateVersion7().ToString(),
            ["siiLob"] = "UNMAPPED",
            ["ifrs17Refs"] = new JsonArray(),
            ["contractYear"] = year,
        };
        edit?.Invoke(payload);
        return _slice.PublishAsync(RecoveryCalculatedV1.Descriptor, "RiContract", contract.ToString(), payload,
            BusinessKeys.Empty.With("riContractId", contract.ToString()).With("batchId", batch.ToString()).With("participantPartyId", participant.ToString()));
    }

    private static JsonObject Leg(string account, string side, string amount, Action<JsonObject> dimensions)
    {
        var dims = new JsonObject { ["legalEntity"] = "GR-TEST", ["jurisdiction"] = "GR", ["ruleId"] = "TEST-RULE" };
        dimensions(dims);
        return new JsonObject
        {
            ["account"] = account,
            ["side"] = side,
            ["amount"] = new JsonObject { ["amount"] = amount, ["currency"] = "EUR" },
            ["dimensions"] = dims,
        };
    }

    /// <summary>A BIL entry of two legs (debit account, credit account) carrying the same dimensions.</summary>
    private Task<EventEnvelope> BilAsync(string entryType, string debit, string credit, string amount, Action<JsonObject> dimensions) =>
        _slice.EntryAsync(Guid.CreateVersion7(), entryType, Day, Leg(debit, "DEBIT", amount, dimensions), Leg(credit, "CREDIT", amount, dimensions));

    private Task<EventEnvelope> ReceivableCollectedAsync(Guid claim, Guid recovery, string amount, Guid? counterparty = null, bool withRecovery = true) =>
        BilAsync("RECEIVABLE_COLLECTED", "LA-11", "LA-28", amount, d =>
        {
            d["sourceType"] = "CLM_CLAIM_PAYMENT";
            d["sourceId"] = recovery.ToString();
            d["claimId"] = claim.ToString();
            d["recoveryId"] = withRecovery ? recovery.ToString() : null;
            d["counterpartyPartyId"] = counterparty?.ToString();
        });

    private Task<EventEnvelope> FsEntryAsync(string entryType, string debit, string credit, string amount, Guid statement, Guid counterparty, string sourceType = "FS_CLEARING", bool withStatement = true) =>
        BilAsync(entryType, debit, credit, amount, d =>
        {
            d["sourceType"] = sourceType;
            d["sourceId"] = statement.ToString();
            d["statementRef"] = withStatement ? statement.ToString() : null;
            d["counterpartyPartyId"] = counterparty.ToString();
        });

    // ---- queries --------------------------------------------------------------------------------------------------------------

    private Task<string> StatusAsync(EventEnvelope envelope) =>
        ScalarAsync<string>(_db, $"SELECT status || coalesce('/' || exception_reason, '') FROM fin.business_event WHERE source_event_id = '{envelope.EventId.Value}'");

    private Task<decimal> NetAsync(string account, string where) =>
        ScalarAsync<decimal>(_db, $"""
            SELECT coalesce(sum(CASE side WHEN 'DEBIT' THEN amount ELSE -amount END), 0) FROM fin.journal_line
             WHERE account_code = '{account}' AND ({where})
            """);

    /// <summary>Every journal touching the filter is balanced in transaction and functional currency, and every one was posted by rule set v4.</summary>
    private async Task AssertJournalsBalancedAsync(string lineFilter)
    {
        (await ScalarAsync<long>(_db, $"""
            SELECT count(*) FROM (SELECT journal_id FROM fin.journal_line WHERE journal_id IN (SELECT journal_id FROM fin.journal_line WHERE {lineFilter})
                                  GROUP BY journal_id HAVING sum(CASE side WHEN 'DEBIT' THEN amount ELSE -amount END) <> 0
                                      OR sum(CASE side WHEN 'DEBIT' THEN amount_functional ELSE -amount_functional END) <> 0) t
            """)).ShouldBe(0);
        (await ScalarAsync<long>(_db, $"""
            SELECT count(*) FROM fin.journal_entry WHERE journal_id IN (SELECT journal_id FROM fin.journal_line WHERE {lineFilter})
               AND (book <> 'IFRS17' OR rule_set_version <> 4)
            """)).ShouldBe(0);
    }

    private async Task<IReadOnlyList<Modules.Finance.Queries.ClearingBalance>> InvariantAsync(
        Func<Modules.Finance.Queries.ClearingInvariants, LegalEntityId, CancellationToken, Task<IReadOnlyList<Modules.Finance.Queries.ClearingBalance>>> query)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<CoreIns.Platform.Context.RequestContext>();
        context.Actor = ActorRef.Service("fin-test-reader");
        context.LegalEntity = LegalEntityCode.Parse("GR-TEST");
        context.Jurisdiction = Jurisdiction.Parse("GR");
        context.ConfigurationHash = ConfigurationHash.Parse(new string('c', 64));
        return await query(scope.ServiceProvider.GetRequiredService<Modules.Finance.Queries.ClearingInvariants>(), new LegalEntityId(Guid.Parse(ApiHostFactory.LegalEntityId)), TestContext.Current.CancellationToken);
    }

    // ---- recoveries -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task REQ_FIN_160_subrogation_200000_reserve_recovery_and_BIL_cash_net_the_recovery_clearing_to_zero_per_recovery()
    {
        var (claim, recovery, insurer) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var reserve = await RecoveryReserveAsync(claim, recovery, "200000.00", "200000.00");
        var recorded = await RecoveryAsync(claim, recovery, "SUBROGATION", insurer, "200000.00");
        var release = await RecoveryReserveAsync(claim, recovery, "-200000.00", "0.00");
        await _slice.DrainAsync();

        foreach (var e in new[] { reserve, recorded, release })
        {
            (await StatusAsync(e)).ShouldBe("POSTED");
        }

        // Recorded but not yet collected: the clearing is open for this recovery only (cash comes from BIL, REQ-FIN-037).
        var filter = $"recovery_id = '{recovery}'";
        (await NetAsync("GL-1340", filter)).ShouldBe(200000.00m);
        (await InvariantAsync((q, le, ct) => q.RecoveryClearingAsync(le, "IFRS17", ct))).Where(b => b.RecoveryId == recovery).Select(b => b.Balance).ShouldBe([200000.00m]);

        var collected = await ReceivableCollectedAsync(claim, recovery, "200000.00", insurer);
        var registered = await BilAsync("RECEIVABLE_REGISTERED", "LA-28", "LA-17", "200000.00", d => d["recoveryId"] = recovery.ToString());
        await _slice.DrainAsync();
        (await StatusAsync(collected)).ShouldBe("POSTED");
        (await StatusAsync(registered)).ShouldBe("NO_POSTING", "RECEIVABLE_REGISTERED is a memo entry: the CLM recovery fact is the accounting fact");

        (await NetAsync("GL-1340", filter)).ShouldBe(0m);
        (await InvariantAsync((q, le, ct) => q.RecoveryClearingAsync(le, "IFRS17", ct))).Where(b => b.RecoveryId == recovery).ShouldBeEmpty();
        (await NetAsync("GL-4310", filter)).ShouldBe(-200000.00m, "subrogation income, credit");
        (await NetAsync("GL-1330", $"claim_id = '{claim}'")).ShouldBe(0m, "recovery reserve built and released by CLM's own ReserveChanged events");
        (await NetAsync("GL-5110", $"claim_id = '{claim}'")).ShouldBe(0m);
        (await NetAsync("GL-2540", filter)).ShouldBe(200000.00m);
        (await ScalarAsync<string>(_db, $"SELECT string_agg(DISTINCT counterparty_party_id::text, ',') FROM fin.journal_line WHERE {filter} AND account_code IN ('GL-1340', 'GL-4310')"))
            .ShouldBe(insurer.ToString());
        await AssertJournalsBalancedAsync($"claim_id = '{claim}' OR {filter}");
    }

    [Fact]
    public async Task REQ_FIN_160_salvage_reserve_1500_recovery_1700_posts_income_per_type_and_nets_the_clearing()
    {
        var (claim, recovery, buyer) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var envelopes = new[]
        {
            await RecoveryReserveAsync(claim, recovery, "1500.00", "1500.00"),
            await RecoveryAsync(claim, recovery, "SALVAGE", buyer, "1700.00"),
            await RecoveryReserveAsync(claim, recovery, "-1500.00", "0.00"),
            await ReceivableCollectedAsync(claim, recovery, "1700.00"),
        };
        await _slice.DrainAsync();
        foreach (var e in envelopes)
        {
            (await StatusAsync(e)).ShouldBe("POSTED");
        }

        var filter = $"recovery_id = '{recovery}'";
        (await NetAsync("GL-4320", filter)).ShouldBe(-1700.00m);
        (await NetAsync("GL-4310", filter)).ShouldBe(0m);
        (await NetAsync("GL-1340", filter)).ShouldBe(0m);
        (await NetAsync("GL-2540", filter)).ShouldBe(1700.00m);
        (await NetAsync("GL-1330", $"claim_id = '{claim}'")).ShouldBe(0m);
        await AssertJournalsBalancedAsync($"claim_id = '{claim}' OR {filter}");
    }

    // ---- Friendly Settlement --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task REQ_FIN_299_BR_FIN_097_FS_payables_3000_and_recovery_1200_net_to_1800_and_BIL_clears_GL2515_to_zero_per_counterparty_and_statement()
    {
        var (claim, statement, insurerY, other) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var payable = await PaymentAsync(claim, Guid.CreateVersion7(), "3000.00", "CLEARING", statement, insurerY);
        var recovery = await RecoveryAsync(claim, Guid.CreateVersion7(), "FRIENDLY_SETTLEMENT", insurerY, "1200.00", statement);
        await _slice.DrainAsync();
        (await StatusAsync(payable)).ShouldBe("POSTED");
        (await StatusAsync(recovery)).ShouldBe("POSTED");

        var key = $"counterparty_party_id = '{insurerY}' AND fs_statement_ref = '{statement}'";
        (await NetAsync("GL-2515", key)).ShouldBe(-1800.00m, "net payable 1,800 open until BIL moves the cash");
        (await NetAsync("GL-5110", $"claim_id = '{claim}'")).ShouldBe(3000.00m);
        (await NetAsync("GL-4330", $"claim_id = '{claim}'")).ShouldBe(-1200.00m);
        (await InvariantAsync((q, le, ct) => q.FsClearingAsync(le, "IFRS17", ct))).Where(b => b.CounterpartyPartyId == insurerY)
            .Select(b => (b.StatementRef, b.Balance)).ShouldBe([(statement.ToString(), -1800.00m)]);

        var released = await FsEntryAsync("FS_CLEARING_RELEASED", "LA-24", "LA-13", "1800.00", statement, insurerY);
        var cleared = await FsEntryAsync("FS_CLEARING_CLEARED", "LA-13", "LA-10", "1800.00", statement, insurerY);
        await _slice.DrainAsync();
        (await StatusAsync(released)).ShouldBe("POSTED");
        (await StatusAsync(cleared)).ShouldBe("POSTED");

        (await NetAsync("GL-2515", key)).ShouldBe(0m);
        (await InvariantAsync((q, le, ct) => q.FsClearingAsync(le, "IFRS17", ct))).Where(b => b.CounterpartyPartyId == insurerY).ShouldBeEmpty();
        (await NetAsync("GL-2530", $"counterparty_party_id = '{insurerY}'")).ShouldBe(0m);
        (await NetAsync("GL-1110", $"counterparty_party_id = '{insurerY}'")).ShouldBe(-1800.00m);

        // No per-payment GL-2510 break: a CLEARING payable never touches it (excluded by construction, REQ-FIN-299).
        (await NetAsync("GL-2510", $"claim_id = '{claim}'")).ShouldBe(0m);
        (await InvariantAsync((q, le, ct) => q.ClaimPaymentClearingAsync(le, "IFRS17", ct))).Where(b => b.ClaimPaymentId is not null).ShouldBeEmpty();
        (await NetAsync("GL-2515", $"counterparty_party_id = '{other}'")).ShouldBe(0m);
        await AssertJournalsBalancedAsync($"claim_id = '{claim}' OR {key}");
    }

    [Fact]
    public async Task REQ_FIN_299_the_FS_net_receivable_variant_is_collected_by_BIL_and_clears_GL2515()
    {
        var (claim, statement, insurerY) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        await PaymentAsync(claim, Guid.CreateVersion7(), "500.00", "CLEARING", statement, insurerY);
        await RecoveryAsync(claim, Guid.CreateVersion7(), "FRIENDLY_SETTLEMENT", insurerY, "2000.00", statement);
        await _slice.DrainAsync();
        var key = $"counterparty_party_id = '{insurerY}' AND fs_statement_ref = '{statement}'";
        (await NetAsync("GL-2515", key)).ShouldBe(1500.00m, "net receivable 1,500: a debit balance");

        var registered = await FsEntryAsync("RECEIVABLE_REGISTERED", "LA-28", "LA-24", "1500.00", statement, insurerY);
        var collected = await FsEntryAsync("RECEIVABLE_COLLECTED", "LA-11", "LA-28", "1500.00", statement, insurerY);
        await _slice.DrainAsync();
        (await StatusAsync(registered)).ShouldBe("NO_POSTING");
        (await StatusAsync(collected)).ShouldBe("POSTED");
        (await NetAsync("GL-2515", key)).ShouldBe(0m);
        (await NetAsync("GL-2540", key)).ShouldBe(1500.00m);
        await AssertJournalsBalancedAsync($"claim_id = '{claim}' OR {key}");
    }

    // ---- RI recoverables ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task REQ_FIN_164_RI_recoverables_post_paid_and_outstanding_separately_and_GL1320_by_basis_equals_the_RI_totals()
    {
        var (contract, reinsurer) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        var e1 = await RiAsync(contract, reinsurer, "50000.00", "0.00", "50000.00");
        var e2 = await RiAsync(contract, reinsurer, "450000.00", "0.00", "450000.00");
        var e3 = await RiAsync(contract, reinsurer, "0.00", "150000.00", "-150000.00"); // the payment split: 150,000 paid, 350,000 outstanding
        var e4 = await RiAsync(contract, reinsurer, "-50000.00", "0.00", "-50000.00");
        await _slice.DrainAsync();
        foreach (var e in new[] { e1, e2, e3, e4 })
        {
            (await StatusAsync(e)).ShouldBe("POSTED");
        }

        var filter = $"ri_contract_id = '{contract}'";
        (await NetAsync("GL-1320", $"{filter} AND recoverable_basis = 'PAID'")).ShouldBe(150000.00m);
        (await NetAsync("GL-1320", $"{filter} AND recoverable_basis = 'OUTSTANDING'")).ShouldBe(300000.00m);
        (await NetAsync("GL-4210", filter)).ShouldBe(-450000.00m);
        (await ScalarAsync<long>(_db, $"""
            SELECT count(*) FROM fin.journal_line WHERE {filter} AND (ri_held_group <> 'UNASSIGNED' OR counterparty_party_id <> '{reinsurer}' OR ri_contract_year <> 2026)
            """)).ShouldBe(0);
        (await ScalarAsync<long>(_db, $"SELECT count(DISTINCT journal_id) FROM fin.journal_line WHERE {filter}")).ShouldBe(4, "one journal per source event");
        await AssertJournalsBalancedAsync(filter);

        // The negative outstanding delta of the split posts on reversed sides: Dr GL-4210 / Cr GL-1320.
        (await ScalarAsync<string>(_db, $"""
            SELECT string_agg(l.account_code || ':' || l.side || ':' || l.recoverable_basis || ':' || l.amount::text, ',' ORDER BY l.line_no) FROM fin.journal_line l
              JOIN fin.journal_entry j USING (journal_id) WHERE j.source_event_ids[1] = '{e3.EventId.Value}'
            """)).ShouldBe("GL-1320:DEBIT:PAID:150000.0000,GL-4210:CREDIT:PAID:150000.0000,GL-1320:CREDIT:OUTSTANDING:150000.0000,GL-4210:DEBIT:OUTSTANDING:150000.0000");
    }

    // ---- payment voids --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task REQ_FIN_159_a_payment_void_reverses_the_payment_journal_and_BIL_reverses_the_cash_side_so_GL2510_returns_to_zero()
    {
        var (claim, payment, disbursement) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        await PaymentAsync(claim, payment, "800.00", "SEPA_CT", disbursement: disbursement);
        await _slice.DisbursementEntryAsync("DISBURSEMENT_RELEASED", Day, disbursement, payment, claim, "800.00", "LA-17", "LA-13");
        await _slice.DrainAsync();
        (await NetAsync("GL-2210", $"claim_id = '{claim}'")).ShouldBe(800.00m);
        (await NetAsync("GL-2530", $"disbursement_id = '{disbursement}'")).ShouldBe(-800.00m);

        var voided = await VoidAsync(claim, payment, "800.00");
        var reversed = await _slice.DisbursementEntryAsync("DISBURSEMENT_REVERSED", Day, disbursement, payment, claim, "800.00", "LA-13", "LA-17");
        await _slice.DrainAsync();
        (await StatusAsync(voided)).ShouldBe("POSTED");
        (await StatusAsync(reversed)).ShouldBe("POSTED");

        (await NetAsync("GL-2210", $"claim_id = '{claim}'")).ShouldBe(0m, "the case reserve consumed by the payment is restored");
        (await NetAsync("GL-2510", $"claim_payment_id = '{payment}'")).ShouldBe(0m);
        (await NetAsync("GL-2530", $"disbursement_id = '{disbursement}'")).ShouldBe(0m);
        (await InvariantAsync((q, le, ct) => q.ClaimPaymentClearingAsync(le, "IFRS17", ct))).Where(b => b.ClaimPaymentId == payment).ShouldBeEmpty();

        // The original journal is untouched and linked to its reversal (REQ-FIN-072, -073).
        (await ScalarAsync<string>(_db, $"""
            SELECT o.source_event_type || '>' || r.source_type || '>' || r.reason FROM fin.journal_entry o JOIN fin.journal_entry r ON r.reverses_journal_id = o.journal_id
             WHERE o.source_ref = '{payment}' AND o.source_event_type = 'PaymentIssued'
            """)).ShouldBe("PaymentIssued>REVERSAL>PaymentVoided: Voided");
        await AssertJournalsBalancedAsync($"claim_id = '{claim}'");

        // A second void of the same payment is refused (a journal is reversed at most once); a void of an unknown payment finds nothing.
        var again = await VoidAsync(claim, payment);
        var unknown = await VoidAsync(claim, Guid.CreateVersion7());
        var partial = await VoidAsync(claim, payment, "100.00");
        await _slice.DrainAsync();
        (await StatusAsync(again)).ShouldBe("SUSPENDED/VOID_REFUSED");
        (await StatusAsync(unknown)).ShouldBe("SUSPENDED/NO_ORIGINAL_JOURNAL");
        (await StatusAsync(partial)).ShouldBe("SUSPENDED/VOID_REFUSED");
    }

    [Fact]
    public async Task NX_HD_06_partial_voids_post_scaled_compensating_journals_never_exceed_the_payment_and_a_full_reversal_cannot_follow()
    {
        var (claim, payment, disbursement) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        await PaymentAsync(claim, payment, "800.00", "SEPA_CT", disbursement: disbursement);
        await _slice.DrainAsync();

        var first = await VoidAsync(claim, payment, "300.00");
        var tooMuch = await VoidAsync(claim, payment, "600.00");
        await _slice.DrainAsync();
        (await StatusAsync(first)).ShouldBe("POSTED");
        (await StatusAsync(tooMuch)).ShouldBe("SUSPENDED/VOID_REFUSED");
        (await NetAsync("GL-2210", $"claim_id = '{claim}'")).ShouldBe(500.00m, "300.00 of the 800.00 reserve consumption is restored");
        (await NetAsync("GL-2510", $"claim_payment_id = '{payment}'")).ShouldBe(-500.00m);
        (await ScalarAsync<long>(_db, $"SELECT count(*) FROM fin.journal_entry WHERE reverses_journal_id IS NOT NULL AND source_ref = '{payment}'")).ShouldBe(0, "a partial void is not the linked reversal");

        // A void without an amount voids what is left; the payment is then fully voided and nothing more can be.
        var rest = await VoidAsync(claim, payment);
        await _slice.DrainAsync();
        (await StatusAsync(rest)).ShouldBe("POSTED");
        (await NetAsync("GL-2210", $"claim_id = '{claim}'")).ShouldBe(0m);
        (await NetAsync("GL-2510", $"claim_payment_id = '{payment}'")).ShouldBe(0m);
        var again = await VoidAsync(claim, payment, "0.01");
        await _slice.DrainAsync();
        (await StatusAsync(again)).ShouldBe("SUSPENDED/VOID_REFUSED");
        await AssertJournalsBalancedAsync($"claim_id = '{claim}'");
    }

    // ---- fail closed ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task PITFALLS_10_every_new_path_fails_closed_with_a_reason_and_posts_nothing()
    {
        var (claim, recovery, party, statement) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var unknownType = await RecoveryAsync(claim, recovery, "WINDFALL", party, "100.00");
        var noStatement = await RecoveryAsync(claim, recovery, "FRIENDLY_SETTLEMENT", party, "100.00");
        var foreign = await RecoveryAsync(claim, recovery, "SUBROGATION", party, "100.00", edit: p => p["amount"]!["group"] = new JsonObject { ["amount"] = "110.00", ["currency"] = "USD" });
        var clearingNoStatement = await PaymentAsync(claim, Guid.CreateVersion7(), "100.00", "CLEARING");
        var reserveNoRecovery = await RecoveryReserveAsync(claim, null, "100.00", "100.00");
        var riSplit = await RiAsync(Guid.CreateVersion7(), party, "100.00", "30.00", "60.00");
        var riFx = await RiAsync(Guid.CreateVersion7(), party, "100.00", "0.00", "100.00", edit: p => p["outstandingDelta"]!["functional"] = new JsonObject { ["amount"] = "99.00", ["currency"] = "EUR" });
        var collectedNoRecovery = await ReceivableCollectedAsync(claim, recovery, "100.00", withRecovery: false);
        var fsNoStatement = await FsEntryAsync("FS_CLEARING_RELEASED", "LA-24", "LA-13", "100.00", statement, party, withStatement: false);
        var collectedUnknownSource = await BilAsync("RECEIVABLE_COLLECTED", "LA-11", "LA-28", "100.00", d => d["sourceType"] = "OTHER");
        await _slice.DrainAsync();

        (await StatusAsync(unknownType)).ShouldBe("SUSPENDED/NO_RULE");
        (await StatusAsync(noStatement)).ShouldBe("SUSPENDED/INVALID_ENVELOPE");
        (await StatusAsync(foreign)).ShouldBe("SUSPENDED/RATE_MISSING");
        (await StatusAsync(clearingNoStatement)).ShouldBe("SUSPENDED/INVALID_ENVELOPE");
        (await StatusAsync(reserveNoRecovery)).ShouldBe("SUSPENDED/INVALID_ENVELOPE");
        (await StatusAsync(riSplit)).ShouldBe("SUSPENDED/UNBALANCED");
        (await StatusAsync(riFx)).ShouldBe("SUSPENDED/RATE_MISSING");
        (await StatusAsync(collectedNoRecovery)).ShouldBe("SUSPENDED/INVALID_ENVELOPE");
        (await StatusAsync(fsNoStatement)).ShouldBe("SUSPENDED/INVALID_ENVELOPE");
        (await StatusAsync(collectedUnknownSource)).ShouldBe("SUSPENDED/NO_RULE");
        (await ScalarAsync<long>(_db, $"SELECT count(*) FROM fin.journal_line WHERE claim_id = '{claim}' OR recovery_id = '{recovery}' OR fs_statement_ref = '{statement}'")).ShouldBe(0);
    }

    // ---- idempotency, sealing, rule-set versions ------------------------------------------------------------------------------

    [Fact]
    public async Task REQ_FIN_031_a_redelivered_recovery_or_RI_event_posts_one_journal_and_the_journals_are_sealed()
    {
        var (claim, recovery, party, contract) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        await RecoveryAsync(claim, recovery, "SUBROGATION", party, "250.00");
        await RiAsync(contract, party, "100.00", "40.00", "60.00");
        await _slice.DrainAsync();
        var before = await ScalarAsync<long>(_db, "SELECT count(*) FROM fin.journal_entry");

        await database.ExecuteAsSuperuserAsync(
            "UPDATE plt.outbox_message SET status = 'Pending', dispatched_at = NULL; DELETE FROM plt.processed_event WHERE handler LIKE 'FIN.%'",
            TestContext.Current.CancellationToken);
        await _slice.DrainAsync();
        (await ScalarAsync<long>(_db, "SELECT count(*) FROM fin.journal_entry")).ShouldBe(before);
        (await ScalarAsync<long>(_db, $"SELECT count(DISTINCT journal_id) FROM fin.journal_line WHERE recovery_id = '{recovery}'")).ShouldBe(1);

        var journal = await ScalarAsync<Guid>(_db, $"SELECT journal_id FROM fin.journal_line WHERE recovery_id = '{recovery}' LIMIT 1");
        foreach (var sql in new[]
                 {
                     $"UPDATE fin.journal_line SET amount = 1 WHERE journal_id = '{journal}'",
                     $"DELETE FROM fin.journal_entry WHERE journal_id = '{journal}'",
                 })
        {
            await using var connection = await _app.OpenConnectionAsync(TestContext.Current.CancellationToken);
            await using var command = new NpgsqlCommand(sql, connection);
            var error = await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
            error.SqlState.ShouldBeOneOf(PostgresErrorCodes.ObjectNotInPrerequisiteState, PostgresErrorCodes.InsufficientPrivilege);
        }
    }

    [Fact]
    public void D_SL4_10_rule_set_v4_keeps_every_v3_rule_and_account_compiles_and_flags_the_placeholders()
    {
        var v3 = FinanceSeed.Rules(FinanceSeed.GrTestV3);
        var v4 = FinanceSeed.Rules(FinanceSeed.GrTestV4);
        v3.Where(r => !v4.Contains(r)).ShouldBeEmpty();
        FinanceSeed.ChartCodes(FinanceSeed.GrTestV3).Where(c => !FinanceSeed.ChartCodes(FinanceSeed.GrTestV4).Contains(c)).ShouldBeEmpty();
        PostingRules.Compile(v4, FinanceSeed.ChartCodes(FinanceSeed.GrTestV4), FinanceSeed.Derivations(FinanceSeed.GrTestV4)).ShouldBeEmpty();
        FinanceSeed.RuleSetHash(FinanceSeed.GrTestV4).ShouldNotBe(FinanceSeed.RuleSetHash(FinanceSeed.GrTestV3));

        var chart = System.Text.Json.Nodes.JsonNode.Parse(FinanceSeed.Json(FinanceSeed.GrTestV4))!["chart"]!["accounts"]!.AsArray();
        foreach (var code in new[] { "GL-1330", "GL-1340", "GL-4310", "GL-4320", "GL-4330" })
        {
            var account = chart.Single(a => a!["code"]!.GetValue<string>() == code)!;
            account["origin"]!.GetValue<string>().ShouldBe("TECHNICAL_PLACEHOLDER");
            account["en"]!.GetValue<string>().ShouldContain("PLACEHOLDER");
        }

        foreach (var code in new[] { "GL-1320", "GL-2515", "GL-4210" })
        {
            chart.Single(a => a!["code"]!.GetValue<string>() == code)!["origin"]!.GetValue<string>().ShouldBe("PRD09_ILLUSTRATIVE");
        }
    }

    [Fact]
    public async Task D_SL4_10_v4_activates_without_changing_journals_posted_by_v2()
    {
        // An earlier accounting date is still governed by v2 (v3 and v4 are effective from 2026-10-01); nothing is rewritten.
        var claim = Guid.CreateVersion7();
        var payload = Sample("clm", "ReserveChanged");
        var setId = Guid.CreateVersion7();
        var term = Guid.CreateVersion7();
        payload["claimId"] = claim.ToString();
        payload["exposureId"] = Guid.CreateVersion7().ToString();
        payload["reserveLineId"] = Guid.CreateVersion7().ToString();
        payload["reserveLine"] = new JsonObject { ["costType"] = "INDEMNITY", ["category"] = "VEHICLE_REPAIR" };
        payload["kind"] = "RESERVE";
        payload["delta"] = Eur3("100.00");
        payload["newOpenAmount"] = Eur3("100.00");
        payload["setId"] = setId.ToString();
        payload["accidentDate"] = "2026-08-01";
        payload["policyTermId"] = term.ToString();
        payload["productCode"] = "MOTOR-GR";
        payload["siiLob"] = "UNMAPPED";
        payload["handlingSegment"] = "STANDARD";
        payload["accountingDate"] = "2026-09-15";
        payload["recoveryId"] = null;
        var envelope = await _slice.PublishAsync(ReserveChangedV1.Descriptor, "Claim", claim.ToString(), payload,
            BusinessKeys.Empty.With("claimId", claim.ToString()).With("exposureId", payload["exposureId"]!.GetValue<string>()).With("setId", setId.ToString()).With("policyTermId", term.ToString()));
        await _slice.DrainAsync();
        (await StatusAsync(envelope)).ShouldBe("POSTED");
        (await ScalarAsync<int>(_db, $"SELECT rule_set_version FROM fin.journal_entry WHERE journal_id IN (SELECT journal_id FROM fin.journal_line WHERE claim_id = '{claim}')")).ShouldBe(2);
        (await ScalarAsync<string>(_db, "SELECT string_agg(version_no::text || ':' || status, ',' ORDER BY version_no) FROM fin.posting_rule_set")).ShouldBe("1:SUPERSEDED,2:ACTIVE,3:ACTIVE,4:ACTIVE");
    }
}
