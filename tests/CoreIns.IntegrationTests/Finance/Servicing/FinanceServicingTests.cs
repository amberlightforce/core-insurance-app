using System.Text.Json.Nodes;
using CoreIns.IntegrationTests.Party;
using CoreIns.IntegrationTests.Product;
using CoreIns.Modules.Market.Contracts.Spi;
using CoreIns.Platform.Events;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using static CoreIns.IntegrationTests.Finance.FinanceSlice;

namespace CoreIns.IntegrationTests.Finance.Servicing;

/// <summary>
/// SL3-FIN-RULES on a real PostgreSQL 17 through the real Host (D-SL3-05, REQ-FIN-036, -182, -183, GF-04 subset, E2E-03
/// step 9): BIL credit, refund and refund-disbursement entries post balanced, sealed journals; a tax or levy payable
/// movement that disagrees with <c>TaxCalculator.treatment</c> suspends the event as TAX_RULE_VIOLATION; the refund
/// payable and the disbursements in transit net to zero per refund. The entries are built the way SL3-BIL-CREDIT and
/// SL3-BIL-REFUND publish them (the contract samples plus the servicing dimensions); the treatment is the Greece rows of
/// REQ-MKT-331 in a double.
/// </summary>
public sealed class FinanceServicingTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Day = "2026-11-10";
    private const string Premium = "288.63";
    private WebApplicationFactory<Program> _factory = null!;
    private FakeTaxCalculator _treatment = null!;
    private FinanceSlice _slice = null!;
    private NpgsqlDataSource _db = null!;
    private NpgsqlDataSource _app = null!;
    private string _artefactHash = null!;

    public async ValueTask InitializeAsync()
    {
        _treatment = new FakeTaxCalculator();
        _factory = Host(_treatment);
        _slice = new FinanceSlice(_factory);
        _db = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        _app = NpgsqlDataSource.Create(database.AppConnectionString);
        using var client = _factory.CreateClient();
        var (response, body) = await ProductApi.ImportAsync(client, ProductApi.Seed("MOTOR-FIN"));
        response.IsSuccessStatusCode.ShouldBeTrue(body?.ToJsonString());
        _artefactHash = body.Text("artefactHash");
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        await _app.DisposeAsync();
        await _factory.DisposeAsync();
    }

    /// <summary>The real Host with <c>TaxCalculator.treatment</c> replaced by the double (or not bound at all).</summary>
    private WebApplicationFactory<Program> Host(ITaxCalculator? calculator) =>
        new ApiHostFactory(database.AppConnectionString).WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ITaxCalculator>();
            if (calculator is not null)
            {
                services.AddSingleton(calculator);
            }
        }));

    private Policy NewPolicy() => Policy.New($"POL{Random.Shared.Next(100_000_000, 999_999_999)}", "MOTOR-FIN", _artefactHash);

    /// <summary>A servicing line: the contract dimensions plus transactionKind, cancellationSource and treatmentRuleId.</summary>
    private static JsonObject Servicing(string account, string side, string amount, Policy policy, Guid billingAccount, string kind = "CANCELLATION",
        string? source = "Policyholder", string? chargeType = null, string? category = null, string? coverage = null, string? treatmentRule = null,
        string? sourceType = null, Guid? sourceId = null, Guid? invoiceId = null)
    {
        var line = Line(account, side, amount, policy, billingAccount, chargeType, category, coverage, invoiceId);
        var dimensions = line["dimensions"]!.AsObject();
        dimensions["transactionKind"] = kind;
        dimensions["cancellationSource"] = source;
        dimensions["treatmentRuleId"] = treatmentRule;
        dimensions["sourceType"] = sourceType;
        dimensions["sourceId"] = sourceId?.ToString();
        return line;
    }

    private static JsonObject Prem(string account, string side, string amount, Policy policy, Guid billingAccount, string kind = "CANCELLATION", string? source = "Policyholder") =>
        Servicing(account, side, amount, policy, billingAccount, kind, source, "PREM-MTPL", "PREMIUM", "MTPL");

    private static JsonObject Ipt(string side, string amount, Policy policy, Guid billingAccount, string kind = "CANCELLATION", string? source = "Policyholder",
        string rule = "GR-TRT-IPT-CANCEL-POLICYHOLDER", string account = "LA-06") =>
        Servicing(account, side, amount, policy, billingAccount, kind, source, "GR-IPT", "TAX", null, rule);

    private static JsonObject Plain(string account, string side, string amount, Policy policy, Guid billingAccount, string kind = "CANCELLATION", string? source = "Policyholder") =>
        Servicing(account, side, amount, policy, billingAccount, kind, source);

    private Task<decimal> NetAsync(string account, string where) =>
        ScalarAsync<decimal>(_db, $"""
            SELECT coalesce(sum(CASE side WHEN 'DEBIT' THEN amount ELSE -amount END), 0) FROM fin.journal_line
             WHERE account_code = '{account}' AND ({where})
            """);

    private Task<string> StatusAsync(EventEnvelope envelope) =>
        ScalarAsync<string>(_db, $"SELECT status || coalesce('/' || exception_reason, '') FROM fin.business_event WHERE source_event_id = '{envelope.EventId.Value}'");

    private Task<long> JournalsAsync(Policy policy) =>
        ScalarAsync<long>(_db, $"SELECT count(DISTINCT journal_id) FROM fin.journal_line WHERE policy_number = '{policy.Number}'");

    /// <summary>The E2E-03 credit as BIL posts it: CREDIT_WRITTEN and CREDIT_BILLED for the premium and an explicit 0.00 IPT line.</summary>
    private async Task<(EventEnvelope Written, EventEnvelope Billed)> CancellationCreditAsync(Policy policy, Guid account, Guid invoice)
    {
        var written = await _slice.EntryAsync(account, "CREDIT_WRITTEN", Day,
            Prem("LA-04", "DEBIT", Premium, policy, account), Prem("LA-01", "CREDIT", Premium, policy, account),
            Ipt("DEBIT", "0.00", policy, account), Plain("LA-01", "CREDIT", "0.00", policy, account));
        var billed = await _slice.EntryAsync(account, "CREDIT_BILLED", Day,
            Servicing("LA-01", "DEBIT", Premium, policy, account, invoiceId: invoice), Servicing("LA-02", "CREDIT", Premium, policy, account, invoiceId: invoice));
        return (written, billed);
    }

    [Fact]
    public async Task REQ_FIN_036_GF04_a_cancellation_credit_of_288_63_posts_Dr_GL_2110_Cr_GL_1210_and_leaves_GL_2410_untouched()
    {
        var policy = NewPolicy();
        var account = Guid.CreateVersion7();
        await _slice.PolicyBoundAsync(policy);
        var (written, billed) = await CancellationCreditAsync(policy, account, Guid.CreateVersion7());
        await _slice.DrainAsync();

        (await StatusAsync(written)).ShouldBe("POSTED");
        (await StatusAsync(billed)).ShouldBe("POSTED");
        (await JournalsAsync(policy)).ShouldBe(2);

        // Credit written: Dr GL-2110 / Cr GL-1215 (premium by the PFC GL key); credit billed: Dr GL-1215 / Cr GL-1210. Together
        // Dr GL-2110 288.63 / Cr GL-1210 288.63; the 0.00 IPT line posts nothing and GL-2410 has no line (PRD-09 section 4.11, 9.1).
        var lines = $"policy_number = '{policy.Number}'";
        (await NetAsync("GL-2110", lines)).ShouldBe(288.63m);
        (await NetAsync("GL-1210", lines)).ShouldBe(-288.63m);
        (await NetAsync("GL-1215", lines)).ShouldBe(0m);
        (await ScalarAsync<long>(_db, $"SELECT count(*) FROM fin.journal_line WHERE {lines} AND account_code IN ('GL-2410', 'GL-2411', 'GL-2420')")).ShouldBe(0);
        (await ScalarAsync<string>(_db, $"SELECT gl_key FROM fin.journal_line WHERE {lines} AND account_code = 'GL-2110'")).ShouldBe("PREM-MOTOR-MTPL");

        // Rule set v3 (effective 2026-10-01), balanced in transaction and functional currency, in the IFRS17 book.
        (await ScalarAsync<long>(_db, $"""
            SELECT count(*) FROM fin.journal_entry WHERE journal_id IN (SELECT journal_id FROM fin.journal_line WHERE {lines})
               AND (book <> 'IFRS17' OR rule_set_version <> 3 OR NOT (rule_codes <@ ARRAY['CW-UNBILLED', 'CW-PREMIUM', 'CB-BILLED', 'CB-UNBILLED']))
            """)).ShouldBe(0);
        (await ScalarAsync<long>(_db, $"""
            SELECT count(*) FROM (SELECT journal_id FROM fin.journal_line WHERE journal_id IN (SELECT journal_id FROM fin.journal_line WHERE {lines})
                                  GROUP BY journal_id HAVING sum(CASE side WHEN 'DEBIT' THEN amount ELSE -amount END) <> 0
                                      OR sum(CASE side WHEN 'DEBIT' THEN amount_functional ELSE -amount_functional END) <> 0) t
            """)).ShouldBe(0);

        // The 0.00 IPT line was never a treatment question: the calculator was not asked.
        _treatment.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task REQ_FIN_182_a_forged_entry_reducing_GL_2410_by_43_29_on_source_Policyholder_is_suspended_TAX_RULE_VIOLATION_with_no_journal()
    {
        var policy = NewPolicy();
        var account = Guid.CreateVersion7();
        await _slice.PolicyBoundAsync(policy);
        var forged = await _slice.EntryAsync(account, "CREDIT_WRITTEN", Day,
            Prem("LA-04", "DEBIT", Premium, policy, account), Prem("LA-01", "CREDIT", "245.34", policy, account),
            Ipt("DEBIT", "43.29", policy, account), Plain("LA-01", "CREDIT", "43.29", policy, account));
        await _slice.DrainAsync();

        (await StatusAsync(forged)).ShouldBe("SUSPENDED/TAX_RULE_VIOLATION");
        (await JournalsAsync(policy)).ShouldBe(0);
        (await ScalarAsync<string>(_db, $"SELECT exception_detail FROM fin.business_event WHERE source_event_id = '{forged.EventId.Value}'")).ShouldContain("GR-TRT-IPT-CANCEL-POLICYHOLDER");
        var request = _treatment.Requests.ShouldHaveSingleItem();
        (request.TransactionKind, request.CancellationSource, request.Category).ShouldBe((TaxTransactionKind.Cancellation, "Policyholder", TaxCategory.Tax));

        // Visible in the intake exception queue's event stream (REQ-FIN-080) and never on a suspense account (REQ-FIN-084).
        (await ScalarAsync<long>(_db, $"""
            SELECT count(*) FROM plt.event_archive WHERE event_type = 'BusinessEventSuspended'
               AND (business_keys->>'businessEventId')::uuid = (SELECT business_event_id FROM fin.business_event WHERE source_event_id = '{forged.EventId.Value}')
            """)).ShouldBe(1);
        (await ScalarAsync<long>(_db, $"SELECT count(*) FROM fin.journal_line WHERE billing_account_id = '{account}'")).ShouldBe(0);
    }

    [Fact]
    public async Task REQ_FIN_182_a_forged_IPT_reduction_is_suspended_at_once_even_before_the_policy_context_has_arrived()
    {
        var policy = NewPolicy();
        var account = Guid.CreateVersion7();
        var forged = await _slice.EntryAsync(account, "CREDIT_WRITTEN", Day,
            Ipt("DEBIT", "43.29", policy, account), Plain("LA-01", "CREDIT", "43.29", policy, account));
        await _slice.DrainAsync();
        (await StatusAsync(forged)).ShouldBe("SUSPENDED/TAX_RULE_VIOLATION");
    }

    [Fact]
    public async Task REQ_FIN_183_an_endorsement_credit_posts_premium_only_and_an_IPT_debit_on_it_is_suspended()
    {
        var policy = NewPolicy();
        var account = Guid.CreateVersion7();
        await _slice.PolicyBoundAsync(policy);
        var ok = await _slice.EntryAsync(account, "CREDIT_WRITTEN", Day,
            Prem("LA-04", "DEBIT", "12.00", policy, account, "ENDORSEMENT_CREDIT", null), Prem("LA-01", "CREDIT", "12.00", policy, account, "ENDORSEMENT_CREDIT", null));
        var forged = await _slice.EntryAsync(account, "CREDIT_WRITTEN", Day,
            Ipt("DEBIT", "1.80", policy, account, "ENDORSEMENT_CREDIT", null, "GR-TRT-IPT-ENDORSEMENT-CREDIT"), Plain("LA-01", "CREDIT", "1.80", policy, account, "ENDORSEMENT_CREDIT", null));
        await _slice.DrainAsync();

        (await StatusAsync(ok)).ShouldBe("POSTED");
        (await StatusAsync(forged)).ShouldBe("SUSPENDED/TAX_RULE_VIOLATION");
        (await NetAsync("GL-2110", $"policy_number = '{policy.Number}'")).ShouldBe(12.00m);
    }

    [Fact]
    public async Task REQ_FIN_183_an_endorsement_debit_is_written_as_new_business_with_its_IPT_increase_checked_APPLY()
    {
        var policy = NewPolicy();
        var account = Guid.CreateVersion7();
        await _slice.PolicyBoundAsync(policy);
        var debit = await _slice.EntryAsync(account, "WRITTEN", Day,
            Prem("LA-01", "DEBIT", "20.00", policy, account, "ENDORSEMENT_DEBIT", null), Prem("LA-04", "CREDIT", "20.00", policy, account, "ENDORSEMENT_DEBIT", null));
        var tax = await _slice.EntryAsync(account, "WRITTEN", Day,
            Servicing("LA-01", "DEBIT", "3.00", policy, account, "ENDORSEMENT_DEBIT", null, "GR-IPT", "TAX", null, "GR-TRT-IPT-ENDORSEMENT-DEBIT"),
            Ipt("CREDIT", "3.00", policy, account, "ENDORSEMENT_DEBIT", null, "GR-TRT-IPT-ENDORSEMENT-DEBIT", "LA-27"));
        await _slice.DrainAsync();

        (await StatusAsync(debit)).ShouldBe("POSTED");
        (await StatusAsync(tax)).ShouldBe("POSTED");
        var lines = $"policy_number = '{policy.Number}'";
        (await NetAsync("GL-2110", lines)).ShouldBe(-20.00m);
        (await NetAsync("GL-2411", lines)).ShouldBe(-3.00m);
        (await NetAsync("GL-1215", lines)).ShouldBe(23.00m);
        (await ScalarAsync<string>(_db, $"SELECT string_agg(DISTINCT rule_code, ',' ORDER BY rule_code) FROM fin.journal_line WHERE {lines}"))
            .ShouldBe("WR-PREMIUM,WR-TAX-NOT-DUE,WR-WRITTEN-UNBILLED");
        _treatment.Requests.ShouldHaveSingleItem().TransactionKind.ShouldBe(TaxTransactionKind.EndorsementDebit);
    }

    [Fact]
    public async Task REQ_FIN_182_RULE_MISSING_a_levy_line_a_source_without_a_row_a_missing_kind_and_a_failing_calculator_all_suspend()
    {
        var policy = NewPolicy();
        var account = Guid.CreateVersion7();
        await _slice.PolicyBoundAsync(policy);
        var levy = await _slice.EntryAsync(account, "CREDIT_WRITTEN", Day,
            Servicing("LA-07", "DEBIT", "3.00", policy, account, chargeType: "GR-AUXF", category: "LEVY"), Plain("LA-01", "CREDIT", "3.00", policy, account));
        var nonPayment = await _slice.EntryAsync(account, "CREDIT_WRITTEN", Day,
            Ipt("DEBIT", "43.29", policy, account, source: "NonPayment"), Plain("LA-01", "CREDIT", "43.29", policy, account, source: "NonPayment"));
        var premiumOnly = await _slice.EntryAsync(account, "CREDIT_WRITTEN", Day,
            Prem("LA-04", "DEBIT", "10.00", policy, account), Prem("LA-01", "CREDIT", "10.00", policy, account));
        await _slice.DrainAsync();
        (await StatusAsync(levy)).ShouldBe("SUSPENDED/TAX_RULE_VIOLATION");
        (await StatusAsync(nonPayment)).ShouldBe("SUSPENDED/TAX_RULE_VIOLATION");
        (await StatusAsync(premiumOnly)).ShouldBe("POSTED");

        var missing = await _slice.EntryAsync(account, "CREDIT_BILLED", Day,
            WithoutKind(Servicing("LA-01", "DEBIT", "10.00", policy, account)), WithoutKind(Servicing("LA-02", "CREDIT", "10.00", policy, account)));
        _treatment.Failure = new TimeoutException("MKT treatment timed out");
        var failing = await _slice.EntryAsync(account, "CREDIT_WRITTEN", Day,
            Ipt("DEBIT", "43.29", policy, account), Plain("LA-01", "CREDIT", "43.29", policy, account));
        await _slice.DrainAsync();
        (await StatusAsync(missing)).ShouldBe("SUSPENDED/TAX_RULE_VIOLATION");
        (await StatusAsync(failing)).ShouldBe("SUSPENDED/TAX_RULE_VIOLATION");
        (await JournalsAsync(policy)).ShouldBe(1, "only the premium-only credit posted");
    }

    /// <summary>
    /// BIL disbursement entry of a policy refund (SL3-BIL-REFUND, D-SL3-14): aggregate Disbursement, source type BIL_REFUND,
    /// sourceId = the refund id, no billing account and no claim.
    /// </summary>
    private Task<EventEnvelope> RefundDisbursementEntryAsync(string entryType, string date, Guid disbursementId, Guid refundId, string amount, string debitAccount, string creditAccount)
    {
        var entryId = Guid.CreateVersion7();
        var payload = Sample("bil", "BillingEntryPosted");
        payload["entryId"] = entryId.ToString();
        payload["eventType"] = entryType;
        payload["accountingDate"] = date;
        payload["businessDate"] = date;
        JsonObject Leg(string account, string side) => new()
        {
            ["account"] = account,
            ["side"] = side,
            ["amount"] = new JsonObject { ["amount"] = amount, ["currency"] = "EUR" },
            ["dimensions"] = new JsonObject
            {
                ["legalEntity"] = "GR-TEST",
                ["jurisdiction"] = "GR",
                ["ruleId"] = entryType == "DISBURSEMENT_RELEASED" ? "BLR-DISB-RELEASED-REFUND" : "BLR-DISB-CLEARED",
                ["billingAccountId"] = null,
                ["disbursementId"] = disbursementId.ToString(),
                ["sourceType"] = "BIL_REFUND",
                ["sourceId"] = refundId.ToString(),
            },
        };
        payload["lines"] = new JsonArray(Leg(debitAccount, "DEBIT"), Leg(creditAccount, "CREDIT"));
        return _slice.PublishAsync(CoreIns.Modules.Billing.Contracts.Events.BillingEntryPostedV1.Descriptor, "Disbursement", disbursementId.ToString(), payload,
            BusinessKeys.Empty.With("entryId", entryId.ToString()).With("disbursementId", disbursementId.ToString()).With("sourceId", refundId.ToString()));
    }

    private static JsonObject WithoutKind(JsonObject line)
    {
        line["dimensions"]!.AsObject()["transactionKind"] = null;
        return line;
    }

    [Fact]
    public async Task PITFALLS_10_a_calculator_that_is_not_bound_suspends_a_tax_movement_but_not_a_premium_only_credit()
    {
        await using var unbound = Host(null);
        var slice = new FinanceSlice(unbound);
        var policy = NewPolicy();
        var account = Guid.CreateVersion7();
        await slice.PolicyBoundAsync(policy);
        var tax = await slice.EntryAsync(account, "CREDIT_WRITTEN", Day,
            Ipt("DEBIT", "43.29", policy, account), Plain("LA-01", "CREDIT", "43.29", policy, account));
        var premiumOnly = await slice.EntryAsync(account, "CREDIT_WRITTEN", Day,
            Prem("LA-04", "DEBIT", "10.00", policy, account), Prem("LA-01", "CREDIT", "10.00", policy, account));
        await slice.DrainAsync();
        (await StatusAsync(tax)).ShouldBe("SUSPENDED/TAX_RULE_VIOLATION");
        (await StatusAsync(premiumOnly)).ShouldBe("POSTED");
    }

    [Fact]
    public async Task REQ_FIN_001_an_unknown_servicing_entry_type_is_NO_RULE_and_a_tax_line_of_a_credit_has_no_rule()
    {
        var policy = NewPolicy();
        var account = Guid.CreateVersion7();
        await _slice.PolicyBoundAsync(policy);
        var unknown = await _slice.EntryAsync(account, "CREDIT_SETTLED", Day,
            Prem("LA-04", "DEBIT", "10.00", policy, account), Prem("LA-01", "CREDIT", "10.00", policy, account));
        var unmapped = await _slice.EntryAsync(account, "REFUND_REJECTED", Day,
            Servicing("LA-12", "DEBIT", "10.00", policy, account, "REFUND", null), Servicing("LA-02", "CREDIT", "10.00", policy, account, "REFUND", null));
        await _slice.DrainAsync();
        (await StatusAsync(unknown)).ShouldBe("SUSPENDED/NO_RULE");
        (await StatusAsync(unmapped)).ShouldBe("SUSPENDED/NO_RULE");
        (await JournalsAsync(policy)).ShouldBe(0);
    }

    [Fact]
    public async Task REQ_FIN_297_GF04_refund_approved_released_cleared_nets_the_refund_payable_and_transit_to_zero_and_credits_cash_288_63()
    {
        var policy = NewPolicy();
        var account = Guid.CreateVersion7();
        var refund = Guid.CreateVersion7();
        var disbursement = Guid.CreateVersion7();
        await _slice.PolicyBoundAsync(policy);
        var (written, billed) = await CancellationCreditAsync(policy, account, Guid.CreateVersion7());

        var approved = await _slice.EntryAsync(account, "REFUND_APPROVED", Day,
            Servicing("LA-02", "DEBIT", Premium, policy, account, "REFUND", null, sourceType: "BIL_REFUND", sourceId: refund),
            Servicing("LA-12", "CREDIT", Premium, policy, account, "REFUND", null, sourceType: "BIL_REFUND", sourceId: refund));
        var released = await RefundDisbursementEntryAsync("DISBURSEMENT_RELEASED", Day, disbursement, refund, Premium, "LA-12", "LA-13");
        var cleared = await RefundDisbursementEntryAsync("DISBURSEMENT_CLEARED", Day, disbursement, refund, Premium, "LA-13", "LA-10");
        await _slice.DrainAsync();

        foreach (var envelope in new[] { written, billed, approved, released, cleared })
        {
            (await StatusAsync(envelope)).ShouldBe("POSTED");
        }

        // The refund payable nets to zero per refund (the id travels as sourceId of the BIL_REFUND entries), as does the transit account.
        var perRefund = $"refund_id = '{refund}'";
        (await NetAsync("GL-2535", perRefund)).ShouldBe(0m);
        (await ScalarAsync<long>(_db, $"SELECT count(*) FROM fin.journal_line WHERE {perRefund} AND account_code = 'GL-2535'")).ShouldBe(2);
        (await NetAsync("GL-2530", $"disbursement_id = '{disbursement}'")).ShouldBe(0m);
        (await NetAsync("GL-1110", $"disbursement_id = '{disbursement}'")).ShouldBe(-288.63m);

        // Credit balance cleared: the credit note's Cr GL-1210 is met by the approval's Dr GL-1210; premium stays reduced; IPT untouched.
        (await NetAsync("GL-1210", $"policy_number = '{policy.Number}'")).ShouldBe(0m);
        (await NetAsync("GL-2110", $"policy_number = '{policy.Number}'")).ShouldBe(288.63m);
        (await ScalarAsync<long>(_db, "SELECT count(*) FROM fin.journal_line WHERE account_code IN ('GL-2410', 'GL-2411')")).ShouldBe(0);

        // Every journal balances (REQ-FIN-068).
        (await ScalarAsync<long>(_db, """
            SELECT count(*) FROM (SELECT journal_id FROM fin.journal_line GROUP BY journal_id
                                  HAVING sum(CASE side WHEN 'DEBIT' THEN amount ELSE -amount END) <> 0) t
            """)).ShouldBe(0);
    }

    [Fact]
    public async Task REQ_FIN_031_a_duplicate_event_posts_one_journal()
    {
        var policy = NewPolicy();
        var account = Guid.CreateVersion7();
        await _slice.PolicyBoundAsync(policy);
        await CancellationCreditAsync(policy, account, Guid.CreateVersion7());
        await _slice.DrainAsync();
        (await JournalsAsync(policy)).ShouldBe(2);

        await database.ExecuteAsSuperuserAsync(
            "UPDATE plt.outbox_message SET status = 'Pending', dispatched_at = NULL; DELETE FROM plt.processed_event WHERE handler LIKE 'FIN.%'",
            TestContext.Current.CancellationToken);
        await _slice.DrainAsync();

        (await JournalsAsync(policy)).ShouldBe(2);
        (await NetAsync("GL-2110", $"policy_number = '{policy.Number}'")).ShouldBe(288.63m);
    }

    [Fact]
    public async Task REQ_FIN_052_dates_before_the_v3_effective_date_post_under_v2_and_have_no_credit_rules()
    {
        var policy = NewPolicy();
        var account = Guid.CreateVersion7();
        await _slice.PolicyBoundAsync(policy);
        var written = await _slice.EntryAsync(account, "WRITTEN", "2026-09-15",
            Line("LA-01", "DEBIT", "240.00", policy, account, "PREM-MTPL", "PREMIUM", "MTPL"), Line("LA-04", "CREDIT", "240.00", policy, account, "PREM-MTPL", "PREMIUM", "MTPL"));
        var credit = await _slice.EntryAsync(account, "CREDIT_WRITTEN", "2026-09-15",
            Prem("LA-04", "DEBIT", "10.00", policy, account), Prem("LA-01", "CREDIT", "10.00", policy, account));
        await _slice.DrainAsync();

        (await StatusAsync(written)).ShouldBe("POSTED");
        (await ScalarAsync<int>(_db, $"SELECT rule_set_version FROM fin.journal_entry WHERE journal_id IN (SELECT journal_id FROM fin.journal_line WHERE policy_number = '{policy.Number}')")).ShouldBe(2);
        (await StatusAsync(credit)).ShouldBe("SUSPENDED/NO_RULE");
        (await ScalarAsync<string>(_db, "SELECT string_agg(version_no::text || ':' || status || ':' || effective_from::text, ',' ORDER BY version_no) FROM fin.posting_rule_set"))
            .ShouldBe("1:SUPERSEDED:2026-01-01,2:ACTIVE:2026-01-01,3:ACTIVE:2026-10-01");
    }

    [Fact]
    public async Task REQ_FIN_002_070_a_line_cannot_be_appended_to_a_posted_credit_journal_by_the_app_role_or_any_other_role()
    {
        var policy = NewPolicy();
        var account = Guid.CreateVersion7();
        await _slice.PolicyBoundAsync(policy);
        await CancellationCreditAsync(policy, account, Guid.CreateVersion7());
        await _slice.DrainAsync();
        var journal = await ScalarAsync<Guid>(_db, $"SELECT DISTINCT journal_id FROM fin.journal_line WHERE policy_number = '{policy.Number}' AND account_code = 'GL-2110'");

        string Append(string side) => $"""
            INSERT INTO fin.journal_line (line_id, journal_id, line_no, legal_entity_id, book, account_code, side, amount, currency, amount_functional,
                functional_currency, rule_code, business_date)
            VALUES (gen_random_uuid(), '{journal}', (SELECT max(line_no) + 1 + floor(random() * 1000)::int FROM fin.journal_line WHERE journal_id = '{journal}'),
                '{ApiHostFactory.LegalEntityId}', 'IFRS17', 'GL-2410', '{side}', 43.29, 'EUR', 43.29, 'EUR', 'RAW', DATE '{Day}');
            """;
        var balancedAppend = "BEGIN;" + Append("DEBIT") + Append("CREDIT") + "COMMIT;";
        await using (var connection = await _app.OpenConnectionAsync(TestContext.Current.CancellationToken))
        await using (var command = new NpgsqlCommand(balancedAppend, connection))
        {
            (await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken))).SqlState
                .ShouldBe(PostgresErrorCodes.ObjectNotInPrerequisiteState);
        }

        (await Should.ThrowAsync<PostgresException>(() => database.ExecuteAsSuperuserAsync(balancedAppend, TestContext.Current.CancellationToken))).SqlState
            .ShouldBe(PostgresErrorCodes.ObjectNotInPrerequisiteState);
        (await ScalarAsync<long>(_db, $"SELECT count(*) FROM fin.journal_line WHERE journal_id = '{journal}'")).ShouldBe(2);
    }
}
