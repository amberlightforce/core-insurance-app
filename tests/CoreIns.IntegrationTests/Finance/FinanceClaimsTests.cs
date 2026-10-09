using System.Net;
using System.Text.Json.Nodes;
using CoreIns.IntegrationTests.Party;
using CoreIns.Modules.Billing.Contracts.Events;
using CoreIns.Modules.Claims.Contracts.Events;
using CoreIns.Platform.Events;
using CoreIns.SharedKernel.Identifiers;
using Npgsql;
using static CoreIns.IntegrationTests.Finance.FinanceSlice;

namespace CoreIns.IntegrationTests.Finance;

/// <summary>
/// SL2-FIN-CLM on a real PostgreSQL 17 through the real Host (D-SL2-08, PRD-09 GF-05 subset, REQ-FIN-037, -158, -159):
/// CLM ReserveChanged and PaymentIssued and the BIL disbursement entries post balanced, sealed IFRS17 journals with
/// claim dimensions; the claim-payment clearing account GL-2510 nets to zero per payment in either arrival order;
/// replays post nothing; journals are listed by claim.
/// </summary>
public sealed class FinanceClaimsTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Day = "2026-11-05";
    private ApiHostFactory _factory = null!;
    private HttpClient _client = null!;
    private FinanceSlice _slice = null!;
    private NpgsqlDataSource _db = null!;
    private NpgsqlDataSource _app = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new ApiHostFactory(database.AppConnectionString);
        _client = _factory.CreateClient();
        _slice = new FinanceSlice(_factory);
        _db = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        _app = NpgsqlDataSource.Create(database.AppConnectionString);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        await _app.DisposeAsync();
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    /// <summary>A claim with one own-damage exposure and one indemnity reserve line (illustrative cost category, D-SL2-04).</summary>
    private sealed record ClaimCase(Guid ClaimId, Guid ExposureId, Guid ReserveLineId, Guid PolicyTermId)
    {
        public static ClaimCase New() => new(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
    }

    private static JsonObject Eur3(string amount) => new()
    {
        ["transaction"] = new JsonObject { ["amount"] = amount, ["currency"] = "EUR" },
        ["functional"] = new JsonObject { ["amount"] = amount, ["currency"] = "EUR" },
        ["group"] = new JsonObject { ["amount"] = amount, ["currency"] = "EUR" },
    };

    /// <summary>CLM ReserveChanged as SL2-CLM-MONEY publishes it (the typed v1 payload, see the SL2-FIN-CLM report).</summary>
    private Task<EventEnvelope> ReserveAsync(ClaimCase claim, string delta, string newOpen, string kind = "RESERVE", string? accountingDate = Day, Action<JsonObject>? edit = null)
    {
        var setId = Guid.CreateVersion7();
        var payload = Sample("clm", "ReserveChanged");
        payload["claimId"] = claim.ClaimId.ToString();
        payload["exposureId"] = claim.ExposureId.ToString();
        payload["reserveLineId"] = claim.ReserveLineId.ToString();
        payload["reserveLine"] = new JsonObject { ["costType"] = "INDEMNITY", ["category"] = "VEHICLE_REPAIR" };
        payload["kind"] = kind;
        payload["delta"] = Eur3(delta);
        payload["newOpenAmount"] = Eur3(newOpen);
        payload["setId"] = setId.ToString();
        payload["accidentDate"] = "2026-11-01";
        payload["policyTermId"] = claim.PolicyTermId.ToString();
        payload["productCode"] = "MOTOR-GR";
        payload["siiLob"] = "UNMAPPED";
        payload["ifrs17GroupRef"] = null;
        payload["catCode"] = null;
        payload["handlingSegment"] = "STANDARD";
        payload["accountingDate"] = accountingDate;
        edit?.Invoke(payload);
        return _slice.PublishAsync(ReserveChangedV1.Descriptor, "Claim", claim.ClaimId.ToString(), payload,
            BusinessKeys.Empty.With("claimId", claim.ClaimId.ToString()).With("exposureId", claim.ExposureId.ToString())
                .With("setId", setId.ToString()).With("policyTermId", claim.PolicyTermId.ToString()));
    }

    /// <summary>CLM PaymentIssued (published when BIL's DisbursementIssued arrives) for one eroding line.</summary>
    private Task<EventEnvelope> PaymentAsync(ClaimCase claim, Guid paymentId, Guid disbursementId, string amount, string? lineAmount = null, Action<JsonObject>? edit = null)
    {
        var transactionId = Guid.CreateVersion7();
        var payload = Sample("clm", "PaymentIssued");
        payload["claimId"] = claim.ClaimId.ToString();
        payload["paymentId"] = paymentId.ToString();
        payload["transactionIds"] = new JsonArray(JsonValue.Create(transactionId.ToString()));
        payload["lines"] = new JsonArray(new JsonObject
        {
            ["lineKey"] = transactionId.ToString(),
            ["amount"] = Eur3(lineAmount ?? amount),
            ["reserveLineId"] = claim.ReserveLineId.ToString(),
            ["exposureId"] = claim.ExposureId.ToString(),
            ["costType"] = "INDEMNITY",
            ["costCategory"] = "VEHICLE_REPAIR",
            ["eroding"] = true,
        });
        payload["amount"] = Eur3(amount);
        payload["payeePartyId"] = Guid.CreateVersion7().ToString();
        payload["method"] = "SEPA_CT";
        payload["disbursementId"] = disbursementId.ToString();
        payload["exGratia"] = false;
        payload["complaintRef"] = null;
        payload["accountingDate"] = Day;
        edit?.Invoke(payload);
        return _slice.PublishAsync(PaymentIssuedV1.Descriptor, "Claim", claim.ClaimId.ToString(), payload,
            BusinessKeys.Empty.With("claimId", claim.ClaimId.ToString()).With("paymentId", paymentId.ToString()).With("disbursementId", disbursementId.ToString()));
    }

    private Task<EventEnvelope> ReleasedAsync(ClaimCase claim, Guid paymentId, Guid disbursementId, string amount) =>
        _slice.DisbursementEntryAsync("DISBURSEMENT_RELEASED", Day, disbursementId, paymentId, claim.ClaimId, amount, "LA-17", "LA-13");

    private Task<EventEnvelope> ClearedAsync(ClaimCase claim, Guid paymentId, Guid disbursementId, string amount) =>
        _slice.DisbursementEntryAsync("DISBURSEMENT_CLEARED", Day, disbursementId, paymentId, claim.ClaimId, amount, "LA-13", "LA-10");

    /// <summary>Debits minus credits of an account over the lines matching <paramref name="where"/>.</summary>
    private Task<decimal> NetAsync(string account, string where) =>
        ScalarAsync<decimal>(_db, $"""
            SELECT coalesce(sum(CASE side WHEN 'DEBIT' THEN amount ELSE -amount END), 0) FROM fin.journal_line
             WHERE account_code = '{account}' AND ({where})
            """);

    private Task<string> StatusAsync(EventEnvelope envelope) =>
        ScalarAsync<string>(_db, $"SELECT status || coalesce('/' || exception_reason, '') FROM fin.business_event WHERE source_event_id = '{envelope.EventId.Value}'");

    /// <summary>The slice's claim money path (E2E-02a): reserve 1,200.00, increase 5,300.00, final payment 6,200.00, release −300.00, BIL release and clearing.</summary>
    private async Task<(ClaimCase Claim, Guid Payment, Guid Disbursement, EventEnvelope[] Events)> ClaimMoneyPathAsync()
    {
        var claim = ClaimCase.New();
        var payment = Guid.CreateVersion7();
        var disbursement = Guid.CreateVersion7();
        var events = new[]
        {
            await ReserveAsync(claim, "1200.00", "1200.00"),
            await ReserveAsync(claim, "5300.00", "6500.00"),
            await PaymentAsync(claim, payment, disbursement, "6200.00"),
            await ReserveAsync(claim, "-300.00", "0.00"),
            await ReleasedAsync(claim, payment, disbursement, "6200.00"),
            await ClearedAsync(claim, payment, disbursement, "6200.00"),
        };
        await _slice.DrainAsync();
        return (claim, payment, disbursement, events);
    }

    [Fact]
    public async Task REQ_FIN_037_158_159_DSL208_the_claim_money_path_posts_balanced_journals_and_every_clearing_account_nets_to_zero()
    {
        var (claim, payment, disbursement, events) = await ClaimMoneyPathAsync();
        foreach (var envelope in events)
        {
            (await StatusAsync(envelope)).ShouldBe("POSTED");
        }

        // One balanced IFRS17 journal per fact, from rule set v2 (REQ-FIN-001, -068).
        var claimLines = $"claim_id = '{claim.ClaimId}'";
        (await ScalarAsync<long>(_db, $"SELECT count(DISTINCT journal_id) FROM fin.journal_line WHERE {claimLines}")).ShouldBe(6);
        (await ScalarAsync<long>(_db, $"""
            SELECT count(*) FROM (SELECT journal_id FROM fin.journal_line WHERE journal_id IN (SELECT journal_id FROM fin.journal_line WHERE {claimLines})
                                  GROUP BY journal_id HAVING sum(CASE side WHEN 'DEBIT' THEN amount ELSE -amount END) <> 0
                                      OR sum(CASE side WHEN 'DEBIT' THEN amount_functional ELSE -amount_functional END) <> 0) t
            """)).ShouldBe(0);
        (await ScalarAsync<long>(_db, $"""
            SELECT count(*) FROM fin.journal_entry WHERE journal_id IN (SELECT journal_id FROM fin.journal_line WHERE {claimLines})
               AND (book <> 'IFRS17' OR rule_set_version <> 3 OR accounting_date <> DATE '{Day}')
            """)).ShouldBe(0);

        // Balances after the path (PRD-09 §4.11 / GF-05 subset): incurred 6,200.00; case reserve, clearing and in-transit
        // all zero; cash out 6,200.00.
        (await NetAsync("GL-5110", claimLines)).ShouldBe(6200.00m);
        (await NetAsync("GL-2210", claimLines)).ShouldBe(0m);
        (await NetAsync("GL-2510", $"claim_payment_id = '{payment}'")).ShouldBe(0m);
        (await NetAsync("GL-2530", $"disbursement_id = '{disbursement}'")).ShouldBe(0m);
        (await NetAsync("GL-1110", $"disbursement_id = '{disbursement}'")).ShouldBe(-6200.00m);

        // The rules that fired, per source (data-driven, REQ-FIN-048).
        (await ScalarAsync<string>(_db, $"""
            SELECT string_agg(DISTINCT j.source_event_type || ':' || l.rule_code || ':' || l.account_code, ',' ORDER BY j.source_event_type || ':' || l.rule_code || ':' || l.account_code)
              FROM fin.journal_line l JOIN fin.journal_entry j USING (journal_id) WHERE l.{claimLines}
            """)).ShouldBe(
            "BillingEntryPosted:DC-CASH:GL-1110,BillingEntryPosted:DC-IN-TRANSIT:GL-2530,BillingEntryPosted:DR-CLAIM-CLEARING:GL-2510,"
            + "BillingEntryPosted:DR-IN-TRANSIT:GL-2530,PaymentIssued:CP-CASE-RESERVE:GL-2210,PaymentIssued:CP-CLAIM-CLEARING:GL-2510,"
            + "ReserveChanged:CR-CASE-RESERVE:GL-2210,ReserveChanged:CR-INCURRED:GL-5110");

        // The release of −300.00 posts on the opposite sides (Dr GL-2210 / Cr GL-5110).
        (await ScalarAsync<string>(_db, $"""
            SELECT string_agg(l.account_code || ':' || l.side || ':' || l.amount::text, ',' ORDER BY l.line_no) FROM fin.journal_line l
              JOIN fin.journal_entry j USING (journal_id) WHERE j.source_event_ids[1] = '{events[3].EventId.Value}'
            """)).ShouldBe("GL-5110:CREDIT:300.0000,GL-2210:DEBIT:300.0000");

        // Claim dimensions on the lines (typed ids and codes; no personal data).
        (await ScalarAsync<long>(_db, $"""
            SELECT count(*) FROM fin.journal_line l JOIN fin.journal_entry j USING (journal_id)
             WHERE l.{claimLines} AND j.source_module = 'CLM'
               AND (l.exposure_id <> '{claim.ExposureId}' OR l.reserve_line_id <> '{claim.ReserveLineId}' OR l.cost_type <> 'INDEMNITY' OR l.cost_category <> 'VEHICLE_REPAIR')
            """)).ShouldBe(0);
        (await ScalarAsync<long>(_db, $"SELECT count(*) FROM fin.journal_line WHERE {claimLines} AND claim_payment_id = '{payment}' AND disbursement_id = '{disbursement}'")).ShouldBe(6);
        (await ScalarAsync<long>(_db, $"SELECT count(*) FROM fin.journal_line WHERE {claimLines} AND policy_term_id = '{claim.PolicyTermId}' AND product_code = 'MOTOR-GR'")).ShouldBe(6);
        (await ScalarAsync<string>(_db, $"SELECT DISTINCT source_ref FROM fin.journal_entry WHERE source_event_type = 'PaymentIssued' AND source_event_ids[1] = '{events[2].EventId.Value}'"))
            .ShouldBe(payment.ToString());

        // Gapless journal numbers from the PLT JOURNAL series (REQ-FIN-069) and one JournalPosted per journal (REQ-FIN-085).
        (await ScalarAsync<long>(_db, """
            SELECT count(*) - (max(substr(journal_number, 8)::bigint) - min(substr(journal_number, 8)::bigint) + 1)
              FROM fin.journal_entry WHERE substr(journal_number, 4, 4) = '2026'
            """)).ShouldBe(0);
        (await ScalarAsync<long>(_db, $"""
            SELECT count(*) FROM plt.event_archive e WHERE e.event_type = 'JournalPosted'
               AND (e.business_keys->>'journalId')::uuid IN (SELECT journal_id FROM fin.journal_line WHERE {claimLines})
            """)).ShouldBe(6);
    }

    [Fact]
    public async Task REQ_FIN_037_DARC26_claim_payment_clearing_nets_to_zero_whichever_of_PaymentIssued_and_the_BIL_release_arrives_first()
    {
        // BIL's release first (the BIL and CLM aggregates are ordered independently).
        var claim = ClaimCase.New();
        var payment = Guid.CreateVersion7();
        var disbursement = Guid.CreateVersion7();
        await ReserveAsync(claim, "800.00", "800.00");
        await ReleasedAsync(claim, payment, disbursement, "800.00");
        await _slice.DrainAsync();
        (await NetAsync("GL-2510", $"claim_payment_id = '{payment}'")).ShouldBe(800.00m, "only the BIL leg is posted: the payment is open on the clearing account");

        await PaymentAsync(claim, payment, disbursement, "800.00");
        await _slice.DrainAsync();
        (await NetAsync("GL-2510", $"claim_payment_id = '{payment}'")).ShouldBe(0m);
        (await NetAsync("GL-2210", $"claim_id = '{claim.ClaimId}'")).ShouldBe(0m);

        // CLM's PaymentIssued first.
        var second = ClaimCase.New();
        var payment2 = Guid.CreateVersion7();
        var disbursement2 = Guid.CreateVersion7();
        await ReserveAsync(second, "450.25", "450.25");
        await PaymentAsync(second, payment2, disbursement2, "450.25");
        await _slice.DrainAsync();
        (await NetAsync("GL-2510", $"claim_payment_id = '{payment2}'")).ShouldBe(-450.25m);

        await ReleasedAsync(second, payment2, disbursement2, "450.25");
        await _slice.DrainAsync();
        (await NetAsync("GL-2510", $"claim_payment_id = '{payment2}'")).ShouldBe(0m);
        (await NetAsync("GL-2530", $"disbursement_id = '{disbursement2}'")).ShouldBe(-450.25m, "in transit until the bank debit");

        await ClearedAsync(second, payment2, disbursement2, "450.25");
        await _slice.DrainAsync();
        (await NetAsync("GL-2530", $"disbursement_id = '{disbursement2}'")).ShouldBe(0m);
    }

    [Fact]
    public async Task REQ_FIN_031_replayed_claim_and_disbursement_events_post_nothing_new()
    {
        var (claim, payment, _, _) = await ClaimMoneyPathAsync();
        var before = await ScalarAsync<long>(_db, "SELECT count(*) FROM fin.journal_entry");

        // Replay everything: outbox back to Pending and the platform's processed markers gone, so only FIN's own
        // idempotency (unique source event id) stands between a redelivery and a second journal.
        await database.ExecuteAsSuperuserAsync(
            "UPDATE plt.outbox_message SET status = 'Pending', dispatched_at = NULL; DELETE FROM plt.processed_event WHERE handler LIKE 'FIN.%'",
            TestContext.Current.CancellationToken);
        await _slice.DrainAsync();

        (await ScalarAsync<long>(_db, "SELECT count(*) FROM fin.journal_entry")).ShouldBe(before);
        (await NetAsync("GL-5110", $"claim_id = '{claim.ClaimId}'")).ShouldBe(6200.00m);
        (await NetAsync("GL-2510", $"claim_payment_id = '{payment}'")).ShouldBe(0m);
    }

    [Fact]
    public async Task REQ_FIN_070_DARC34_the_app_role_can_neither_append_to_nor_change_a_posted_claim_journal()
    {
        var claim = ClaimCase.New();
        var reserve = await ReserveAsync(claim, "1200.00", "1200.00");
        await _slice.DrainAsync();
        var journal = await ScalarAsync<Guid>(_db, $"SELECT journal_ids[1] FROM fin.business_event WHERE source_event_id = '{reserve.EventId.Value}'");

        // A balanced pair appended later would pass a balance-only check: the line trigger seals the journal (D-ARC-34).
        string Append(string side, int lineNo) => $"""
            INSERT INTO fin.journal_line (line_id, journal_id, line_no, legal_entity_id, book, account_code, side, amount, currency, amount_functional,
                functional_currency, rule_code, business_date, claim_id)
            VALUES (gen_random_uuid(), '{journal}', {lineNo}, '{ApiHostFactory.LegalEntityId}', 'IFRS17', 'GL-2210', '{side}', 5, 'EUR', 5, 'EUR', 'RAW',
                DATE '{Day}', '{claim.ClaimId}');
            """;
        foreach (var sql in new[]
                 {
                     "BEGIN;" + Append("DEBIT", 50) + Append("CREDIT", 51) + "COMMIT;",
                     $"UPDATE fin.journal_line SET amount = 1 WHERE journal_id = '{journal}'",
                     $"DELETE FROM fin.journal_entry WHERE journal_id = '{journal}'",
                 })
        {
            await using var connection = await _app.OpenConnectionAsync(TestContext.Current.CancellationToken);
            await using var command = new NpgsqlCommand(sql, connection);
            var error = await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
            error.SqlState.ShouldBeOneOf(PostgresErrorCodes.ObjectNotInPrerequisiteState, PostgresErrorCodes.InsufficientPrivilege);
        }

        (await ScalarAsync<string>(_db, $"SELECT count(*) || '/' || sum(amount) FROM fin.journal_line WHERE journal_id = '{journal}'")).ShouldBe("2/2400.0000");
    }

    [Fact]
    public async Task REQ_FIN_078_DSL208_journals_are_listed_by_claim_with_their_claim_dimensions()
    {
        var (claim, payment, disbursement, _) = await ClaimMoneyPathAsync();
        var (other, _, _, _) = await ClaimMoneyPathAsync();

        var (response, page) = await PartyApi.SendAsync(_client, HttpMethod.Get, $"/api/fin/v1/journals/query?claimId={claim.ClaimId}&limit=50", roles: FinanceRole);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, page?.ToJsonString());
        var journals = page!["items"]!.AsArray().Select(i => i!["journal"]!).ToList();
        journals.Count.ShouldBe(6);
        journals.Select(j => j.Text("sourceEventType")).Order(StringComparer.Ordinal)
            .ShouldBe(["BillingEntryPosted", "BillingEntryPosted", "PaymentIssued", "ReserveChanged", "ReserveChanged", "ReserveChanged"]);
        journals.SelectMany(j => j["lines"]!.AsArray()).ShouldAllBe(l => l!.Text("dimensions.claimId") == claim.ClaimId.ToString());
        journals.SelectMany(j => j["lines"]!.AsArray()).ShouldNotContain(l => l!.Text("dimensions.claimId") == other.ClaimId.ToString());

        var paymentJournal = journals.Single(j => j.Text("sourceEventType") == "PaymentIssued");
        paymentJournal.Text("sourceModule").ShouldBe("CLM");
        paymentJournal.Text("sourceRef").ShouldBe(payment.ToString());
        var clearing = paymentJournal["lines"]!.AsArray().Single(l => l!.Text("account") == "GL-2510")!;
        clearing.Text("side").ShouldBe("CREDIT");
        clearing.Text("amount.amount").ShouldBe("6200.00");
        clearing.Text("accountOrigin").ShouldBe("PRD09_ILLUSTRATIVE");
        clearing.Text("accountName.en").ShouldBe("Claim payments clearing");
        clearing.Text("dimensions.claimPaymentId").ShouldBe(payment.ToString());
        clearing.Text("dimensions.disbursementId").ShouldBe(disbursement.ToString());
        clearing.Text("dimensions.reserveLineId").ShouldBe(claim.ReserveLineId.ToString());
        clearing.Text("dimensions.exposureId").ShouldBe(claim.ExposureId.ToString());
        clearing.Text("dimensions.costType").ShouldBe("INDEMNITY");
        var release = journals.First(j => j.Text("sourceEventType") == "BillingEntryPosted" && j["lines"]!.AsArray().Any(l => l!.Text("account") == "GL-2510"));
        release["lines"]!.AsArray().ShouldAllBe(l => l!.Text("dimensions.claimPaymentId") == payment.ToString());

        var (bad, problem) = await PartyApi.SendAsync(_client, HttpMethod.Get, "/api/fin/v1/journals/query?claimId=CLM-1", roles: FinanceRole);
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        problem.Text("code").ShouldBe("FIN-ERR-VALIDATION");
    }

    [Fact]
    public async Task REQ_FIN_030_DSL208_claim_and_disbursement_context_events_are_recorded_and_never_journalised()
    {
        var claim = ClaimCase.New();
        var disbursement = Guid.CreateVersion7();
        var claimKeys = BusinessKeys.Empty.With("claimId", claim.ClaimId.ToString());
        var published = new List<EventEnvelope>();
        foreach (var (contract, type, keys) in new (CoreIns.Platform.Contracts.Events.EventContract, string, BusinessKeys)[]
                 {
                     (ClaimReportedV1.Descriptor, "ClaimReported", claimKeys.With("policyId", Guid.CreateVersion7().ToString()).With("policyTermId", claim.PolicyTermId.ToString())),
                     (ExposureCreatedV1.Descriptor, "ExposureCreated", claimKeys.With("exposureId", claim.ExposureId.ToString())),
                     (TransactionSetApprovedV1.Descriptor, "TransactionSetApproved", claimKeys.With("setId", Guid.CreateVersion7().ToString())),
                     (ClaimClosedV1.Descriptor, "ClaimClosed", claimKeys),
                 })
        {
            published.Add(await _slice.PublishAsync(contract, "Claim", claim.ClaimId.ToString(), Sample("clm", type), RequiredKeys(contract, keys)));
        }

        foreach (var (contract, type) in new (CoreIns.Platform.Contracts.Events.EventContract, string)[]
                 {
                     (DisbursementIssuedV1.Descriptor, "DisbursementIssued"),
                     (DisbursementClearedV1.Descriptor, "DisbursementCleared"),
                     (DisbursementRejectedV1.Descriptor, "DisbursementRejected"),
                     (DisbursementStoppedV1.Descriptor, "DisbursementStopped"),
                     (DisbursementVoidedV1.Descriptor, "DisbursementVoided"),
                     (DisbursementReturnedV1.Descriptor, "DisbursementReturned"),
                 })
        {
            published.Add(await _slice.PublishAsync(contract, "Disbursement", disbursement.ToString(), Sample("bil", type),
                RequiredKeys(contract, BusinessKeys.Empty.With("disbursementId", disbursement.ToString()).With("claimId", claim.ClaimId.ToString()))));
        }

        await _slice.DrainAsync();

        foreach (var envelope in published)
        {
            (await ScalarAsync<string>(_db, $"SELECT relevance || ':' || status FROM fin.business_event WHERE source_event_id = '{envelope.EventId.Value}'"))
                .ShouldBe("CONTEXT:NO_POSTING", envelope.EventType.Value);
        }

        (await ScalarAsync<long>(_db, $"SELECT count(*) FROM fin.journal_line WHERE claim_id = '{claim.ClaimId}' OR disbursement_id = '{disbursement}'")).ShouldBe(0);
    }

    /// <summary>Adds any lineage key the contract requires that the test did not set (fresh ids: the values do not matter to FIN).</summary>
    private static BusinessKeys RequiredKeys(CoreIns.Platform.Contracts.Events.EventContract contract, BusinessKeys keys)
    {
        foreach (var key in contract.RequiredBusinessKeys.SelectMany(k => k.Split('|').Take(1)))
        {
            if (!keys.TryGetValue(key, out _))
            {
                keys = keys.With(key, Guid.CreateVersion7().ToString());
            }
        }

        return keys;
    }

    [Fact]
    public async Task DSL212a_FS_clearing_payments_missing_lines_unknown_eroding_and_FX_amounts_are_suspended_never_guessed()
    {
        var claim = ClaimCase.New();
        var fs = await PaymentAsync(claim, Guid.CreateVersion7(), Guid.CreateVersion7(), "100.00", edit: p => p["method"] = "CLEARING");
        var noLines = await PaymentAsync(claim, Guid.CreateVersion7(), Guid.CreateVersion7(), "100.00", edit: p => p["lines"] = new JsonArray());
        var unknownEroding = await PaymentAsync(claim, Guid.CreateVersion7(), Guid.CreateVersion7(), "100.00", edit: p => p["lines"]![0]!["eroding"] = null);
        var fxPayment = await PaymentAsync(claim, Guid.CreateVersion7(), Guid.CreateVersion7(), "100.00",
            edit: p => p["lines"]![0]!["amount"]!["group"] = new JsonObject { ["amount"] = "110.00", ["currency"] = "USD" });
        var fxReserve = await ReserveAsync(claim, "100.00", "100.00",
            edit: p => p["delta"]!["functional"] = new JsonObject { ["amount"] = "99.99", ["currency"] = "EUR" });
        await _slice.DrainAsync();

        (await StatusAsync(fs)).ShouldBe("SUSPENDED/NO_RULE", "Friendly Settlement settles per clearing statement, never through GL-2510 (REQ-FIN-299)");
        (await StatusAsync(noLines)).ShouldBe("SUSPENDED/INVALID_ENVELOPE");
        (await StatusAsync(unknownEroding)).ShouldBe("SUSPENDED/INVALID_ENVELOPE");
        (await StatusAsync(fxPayment)).ShouldBe("SUSPENDED/RATE_MISSING");
        (await StatusAsync(fxReserve)).ShouldBe("SUSPENDED/RATE_MISSING");
        (await ScalarAsync<long>(_db, $"SELECT count(*) FROM fin.journal_line WHERE claim_id = '{claim.ClaimId}'")).ShouldBe(0);
    }

    [Fact]
    public async Task REQ_FIN_080_unmapped_or_inconsistent_claim_facts_are_intake_exceptions_never_journals()
    {
        var claim = ClaimCase.New();
        var recovery = await ReserveAsync(claim, "800.00", "800.00", kind: "RECOVERY_RESERVE");
        var mismatch = await PaymentAsync(claim, Guid.CreateVersion7(), Guid.CreateVersion7(), "100.00", lineAmount: "90.00");
        await _slice.DrainAsync();

        // Recoveries are out of the slice (D-SL2-01): no rule for the recovery-reserve legs, so no journal and no default account.
        (await StatusAsync(recovery)).ShouldBe("SUSPENDED/NO_RULE");
        (await StatusAsync(mismatch)).ShouldBe("SUSPENDED/UNBALANCED");
        (await ScalarAsync<long>(_db, $"SELECT count(*) FROM fin.journal_line WHERE claim_id = '{claim.ClaimId}'")).ShouldBe(0);
    }

    [Fact]
    public async Task REQ_FIN_035_a_claim_fact_without_an_accounting_date_is_dated_by_its_business_date_in_the_entity_zone()
    {
        var claim = ClaimCase.New();
        var reserve = await ReserveAsync(claim, "100.00", "100.00", accountingDate: null);
        await _slice.DrainAsync();

        (await StatusAsync(reserve)).ShouldBe("POSTED");
        var occurred = await ScalarAsync<string>(_db, $"SELECT (occurred_at AT TIME ZONE 'Europe/Athens')::date::text FROM fin.business_event WHERE source_event_id = '{reserve.EventId.Value}'");
        (await ScalarAsync<string>(_db, $"SELECT DISTINCT j.accounting_date::text FROM fin.journal_entry j JOIN fin.journal_line l USING (journal_id) WHERE l.claim_id = '{claim.ClaimId}'"))
            .ShouldBe(occurred);
    }
}
