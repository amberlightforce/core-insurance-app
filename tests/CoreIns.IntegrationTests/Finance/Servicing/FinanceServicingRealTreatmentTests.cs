using System.Text.Json.Nodes;
using CoreIns.IntegrationTests.Party;
using CoreIns.IntegrationTests.Product;
using static CoreIns.IntegrationTests.Finance.FinanceSlice;

namespace CoreIns.IntegrationTests.Finance.Servicing;

/// <summary>
/// The REQ-FIN-182 check against the real MKT <c>TaxCalculator.treatment</c> with the Greece pack rows (REQ-MKT-331,
/// all PendingOpinion, D-SL3-05) instead of a double: the host is untouched, so FIN calls what SL3-MKT-TREATMENT bound.
/// </summary>
public sealed class FinanceServicingRealTreatmentTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Day = "2026-11-10";
    private ApiHostFactory _factory = null!;
    private FinanceSlice _slice = null!;
    private System.Net.Http.HttpClient _client = null!;
    private Npgsql.NpgsqlDataSource _db = null!;
    private string _artefactHash = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = new ApiHostFactory(database.AppConnectionString);
        _client = _factory.CreateClient();
        _slice = new FinanceSlice(_factory);
        _db = Npgsql.NpgsqlDataSource.Create(database.SuperuserConnectionString);
        var (response, body) = await ProductApi.ImportAsync(_client, ProductApi.Seed("MOTOR-FIN"));
        response.IsSuccessStatusCode.ShouldBeTrue(body?.ToJsonString());
        _artefactHash = body.Text("artefactHash");
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private static JsonObject Servicing(string account, string side, string amount, Policy policy, Guid billingAccount, string kind, string? source,
        string? chargeType = null, string? category = null, string? coverage = null, string? treatmentRule = null)
    {
        var line = Line(account, side, amount, policy, billingAccount, chargeType, category, coverage);
        var dimensions = line["dimensions"]!.AsObject();
        dimensions["transactionKind"] = kind;
        dimensions["cancellationSource"] = source;
        dimensions["treatmentRuleId"] = treatmentRule;
        return line;
    }

    private Task<string> StatusAsync(CoreIns.Platform.Events.EventEnvelope envelope) =>
        ScalarAsync<string>(_db, $"SELECT status || coalesce('/' || exception_reason, '') FROM fin.business_event WHERE source_event_id = '{envelope.EventId.Value}'");

    [Fact]
    public async Task REQ_FIN_182_the_real_Greece_treatment_keeps_IPT_on_a_policyholder_cancellation_and_refuses_a_forged_reduction_or_a_levy_credit()
    {
        var policy = Policy.New($"POL{Random.Shared.Next(100_000_000, 999_999_999)}", "MOTOR-FIN", _artefactHash);
        var account = Guid.CreateVersion7();
        await _slice.PolicyBoundAsync(policy);

        var credit = await _slice.EntryAsync(account, "CREDIT_WRITTEN", Day,
            Servicing("LA-04", "DEBIT", "288.63", policy, account, "CANCELLATION", "Policyholder", "PREM-MTPL", "PREMIUM", "MTPL"),
            Servicing("LA-01", "CREDIT", "288.63", policy, account, "CANCELLATION", "Policyholder"),
            Servicing("LA-06", "DEBIT", "0.00", policy, account, "CANCELLATION", "Policyholder", "GR-IPT", "TAX", null, "GR-TRT-IPT-CANCEL-POLICYHOLDER"),
            Servicing("LA-01", "CREDIT", "0.00", policy, account, "CANCELLATION", "Policyholder"));
        var forged = await _slice.EntryAsync(account, "CREDIT_WRITTEN", Day,
            Servicing("LA-06", "DEBIT", "43.29", policy, account, "CANCELLATION", "Policyholder", "GR-IPT", "TAX", null, "GR-TRT-IPT-CANCEL-POLICYHOLDER"),
            Servicing("LA-01", "CREDIT", "43.29", policy, account, "CANCELLATION", "Policyholder"));
        var levy = await _slice.EntryAsync(account, "CREDIT_WRITTEN", Day,
            Servicing("LA-07", "DEBIT", "3.00", policy, account, "CANCELLATION", "Policyholder", "GR-AUXF", "LEVY"),
            Servicing("LA-01", "CREDIT", "3.00", policy, account, "CANCELLATION", "Policyholder"));
        var otherSource = await _slice.EntryAsync(account, "CREDIT_WRITTEN", Day,
            Servicing("LA-06", "DEBIT", "43.29", policy, account, "CANCELLATION", "NonPayment", "GR-IPT", "TAX", null, "GR-TRT-IPT-CANCEL-POLICYHOLDER"),
            Servicing("LA-01", "CREDIT", "43.29", policy, account, "CANCELLATION", "NonPayment"));
        await _slice.DrainAsync();

        (await StatusAsync(credit)).ShouldBe("POSTED");
        (await StatusAsync(forged)).ShouldBe("SUSPENDED/TAX_RULE_VIOLATION");
        (await StatusAsync(levy)).ShouldBe("SUSPENDED/TAX_RULE_VIOLATION", "RULE_MISSING: the pack holds no levy row (D-SL3-06)");
        (await StatusAsync(otherSource)).ShouldBe("SUSPENDED/TAX_RULE_VIOLATION", "RULE_MISSING: no IPT row for a source other than Policyholder");
        (await ScalarAsync<string>(_db, $"SELECT string_agg(account_code || ' ' || side || ' ' || amount::text, ',' ORDER BY account_code) FROM fin.journal_line WHERE policy_number = '{policy.Number}'"))
            .ShouldBe("GL-1215 CREDIT 288.6300,GL-2110 DEBIT 288.6300");
    }
}
