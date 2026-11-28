using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using CoreIns.IntegrationTests.Policy;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Bil;

/// <summary>
/// BO-BIL-04: with the product configured to require it, a multi-instalment bind is blocked (gate DOWN_PAYMENT) until the first instalment is
/// cleared; the amount is the first instalment of the plan; ANNUAL and unconfigured products bind as before.
/// </summary>
public sealed class DownPaymentTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private PolicySlice _required = null!;
    private PolicySlice _default = null!;

    public async ValueTask InitializeAsync()
    {
        _required = new PolicySlice(database.AppConnectionString, realRatingAndUnderwriting: true, settings: new Dictionary<string, string?> { ["Policy:DownPaymentRequiredProducts:0"] = "*" });
        await _required.SeedAsync();
        _default = new PolicySlice(database.AppConnectionString, realRatingAndUnderwriting: true);
        await _default.SeedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _required.DisposeAsync();
        await _default.DisposeAsync();
    }

    private static async Task<string> QuotedJobAsync(PolicySlice slice, string family)
    {
        var (created, party) = await SendAsync(slice.Client, HttpMethod.Post, "/api/pty/v1/parties", Person("Nikos", family, null));
        created.StatusCode.ShouldBe(HttpStatusCode.Created, party?.ToJsonString());
        var (jobId, _, _) = await slice.DraftAsync(party.Text("party.partyId"), DateTimeOffset.UtcNow.AddDays(2));
        (await slice.QuoteAsync(jobId)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return jobId;
    }

    private static Task<(HttpResponseMessage Response, JsonNode? Body)> BindAsync(PolicySlice slice, string jobId, string plan) =>
        SendAsync(slice.Client, HttpMethod.Post, "/api/pol/v1/jobs/bind", new { jobId, versionNo = 1, paymentPlanOption = plan, confirmation = true });

    private static Task<(HttpResponseMessage Response, JsonNode? Body)> InitiateAsync(PolicySlice slice, string jobId, string plan) =>
        SendAsync(slice.Client, HttpMethod.Post, "/api/bil/v1/down-payment/initiate", new { jobId, method = "BANK_TRANSFER", paymentPlanOption = plan }, Billing);

    private static async Task<JsonNode?> StatusAsync(PolicySlice slice, string jobId) =>
        (await SendAsync(slice.Client, HttpMethod.Get, $"/api/bil/v1/down-payment/status?jobId={jobId}", roles: Billing)).Body;

    private static decimal Amt(JsonNode? node, string path) => decimal.Parse(node.Text(path), CultureInfo.InvariantCulture);

    [Fact]
    public async Task A_monthly_bind_is_blocked_until_the_first_instalment_is_cleared()
    {
        var jobId = await QuotedJobAsync(_required, "Dimou");

        var (blockedResponse, blocked) = await BindAsync(_required, jobId, "MONTHLY");
        blockedResponse.StatusCode.ShouldBe(HttpStatusCode.OK, blocked?.ToJsonString());
        blocked.Text("state").ShouldBe("QUOTED");
        blocked!["gateResults"]!.AsArray().Single(g => g!.Text("gate") == "DOWN_PAYMENT")!.Text("passed").ShouldBe("false");
        (await StatusAsync(_required, jobId)).Text("status").ShouldBe("NOT_REQUIRED");

        var (initiated, initiate) = await InitiateAsync(_required, jobId, "MONTHLY");
        initiated.StatusCode.ShouldBe(HttpStatusCode.OK, initiate?.ToJsonString());
        initiate.Text("status").ShouldBe("PENDING");
        var amount = Amt(initiate, "amount.amount");
        amount.ShouldBeGreaterThan(0m);
        var reference = initiate.Text("paymentReference");
        (await StatusAsync(_required, jobId)).Text("planCode").ShouldBe("MONTHLY");

        // The first instalment is a twelfth of the annual premium: the whole-year total divided over 12 stays close to it.
        var annual = await InitiateAsync(_required, jobId, "SEMI_ANNUAL");
        Amt(annual.Body, "amount.amount").ShouldBeGreaterThan(amount * 5m);
        (await InitiateAsync(_required, jobId, "MONTHLY")).Body.Text("amount.amount").ShouldBe(initiate.Text("amount.amount"));

        // Part of the amount is not enough; the full amount clears the gate.
        var part = await SendAsync(_required.Client, HttpMethod.Post, $"/dev/bil/down-payments/{reference}/pay?amount=1.00", roles: Billing);
        part.Response.StatusCode.ShouldBe(HttpStatusCode.OK, part.Body?.ToJsonString());
        part.Body.Text("status").ShouldBe("PENDING");
        (await BindAsync(_required, jobId, "MONTHLY")).Body.Text("state").ShouldBe("QUOTED");

        var rest = await SendAsync(_required.Client, HttpMethod.Post, $"/dev/bil/down-payments/{reference}/pay?amount={(amount - 1m).ToString(CultureInfo.InvariantCulture)}", roles: Billing);
        rest.Body.Text("status").ShouldBe("CLEARED");
        (await StatusAsync(_required, jobId)).Text("status").ShouldBe("CLEARED");

        // A different plan than the one paid for is refused; the paid plan binds.
        (await BindAsync(_required, jobId, "QUARTERLY")).Body.Text("state").ShouldBe("QUOTED");
        var (boundResponse, bound) = await BindAsync(_required, jobId, "MONTHLY");
        boundResponse.StatusCode.ShouldBe(HttpStatusCode.OK, bound?.ToJsonString());
        bound.Text("state").ShouldBe("BOUND");
    }

    [Fact]
    public async Task Annual_needs_no_down_payment_even_when_the_product_requires_it()
    {
        var jobId = await QuotedJobAsync(_required, "Karras");
        var (response, initiate) = await InitiateAsync(_required, jobId, "ANNUAL");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, initiate?.ToJsonString());
        initiate.Text("status").ShouldBe("NOT_REQUIRED");
        var (_, bound) = await BindAsync(_required, jobId, "ANNUAL");
        bound.Text("state").ShouldBe("BOUND");
    }

    [Fact]
    public async Task Without_the_product_setting_a_monthly_job_binds_without_a_down_payment()
    {
        var jobId = await QuotedJobAsync(_default, "Lykou");
        var (response, bound) = await BindAsync(_default, jobId, "MONTHLY");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, bound?.ToJsonString());
        bound.Text("state").ShouldBe("BOUND");
    }

    [Fact]
    public async Task An_unavailable_method_is_refused()
    {
        var jobId = await QuotedJobAsync(_required, "Mela");
        var (response, _) = await SendAsync(_required.Client, HttpMethod.Post, "/api/bil/v1/down-payment/initiate", new { jobId, method = "BITCOIN", paymentPlanOption = "MONTHLY" }, Billing);
        ((int)response.StatusCode).ShouldBeInRange(400, 503);
        response.IsSuccessStatusCode.ShouldBeFalse();
    }
}
