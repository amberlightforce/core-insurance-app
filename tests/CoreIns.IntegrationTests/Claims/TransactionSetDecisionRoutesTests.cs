using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using static CoreIns.IntegrationTests.Claims.ClaimsMoney;

namespace CoreIns.IntegrationTests.Claims;

/// <summary>
/// BO-CLM-04: the HTTP routes clm.TransactionSet.approve / reject / return share one decision path with the PLT approvals
/// API (maker-checker is PLT's rule), and clm.Financials.dailyTotals equals the ledger lines.
/// </summary>
public sealed class TransactionSetDecisionRoutesTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private ClaimsSlice _slice = null!;
    private ClaimsMoney _money = null!;

    public ValueTask InitializeAsync()
    {
        _slice = new ClaimsSlice(database.AppConnectionString);
        _money = new ClaimsMoney(_slice.Factory, _slice.Client, database.SuperuserConnectionString);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _slice.DisposeAsync();

    private async Task<(MoneyClaim Claim, JsonNode Submit)> ReferredAsync(decimal amount = 6500m)
    {
        var claim = await _money.OpenClaimAsync(ClaimsSlice.Fnol(_slice.Policy()));
        var submit = await _money.BuildAndSubmitAsync(claim, [Reserve(claim, amount)]);
        submit.Text("status").ShouldBe("PENDING_APPROVAL");
        return (claim, submit);
    }

    private Task<(HttpResponseMessage Response, JsonNode? Body)> RouteAsync(string route, object body, string roles = Manager, string user = ManagerUser) =>
        _money.SendAsync(HttpMethod.Post, "/api/clm/v1/transaction-sets/" + route, body, roles, user);

    [Fact]
    public async Task Approve_route_decides_in_PLT_and_applies_the_set()
    {
        var (claim, submit) = await ReferredAsync();
        var set = await _money.SetAsync(submit.Text("setId"));
        var (response, body) = await RouteAsync("approve", new { setId = submit.Text("setId"), contentHash = set.Text("contentHash") });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("status").ShouldBe("APPROVED");
        Amount((await _money.FinancialsAsync(claim))["totals"]!["openReserve"]).ShouldBe(6500m);

        // The later ApprovalDecided event is a no-op (one path, idempotent).
        await _money.DrainAsync();
        (await _money.SetAsync(submit.Text("setId"))).Text("status").ShouldBe("APPROVED");

        // A decided set is stale on a second call.
        var (again, problem) = await RouteAsync("approve", new { setId = submit.Text("setId"), contentHash = set.Text("contentHash") });
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        problem.Text("code").ShouldBe("CLM-ERR-SET-STALE");
    }

    [Fact]
    public async Task Maker_is_refused_on_approve_reject_and_return_like_in_the_inbox()
    {
        var (_, submit) = await ReferredAsync();
        var setId = submit.Text("setId");
        var set = await _money.SetAsync(setId);
        var both = $"{Handler},{Manager}";
        (await _money.DecideAsync(submit.Text("approvalRequestId"), roles: both, user: HandlerUser)).Body.Text("code").ShouldBe("PLT-ERR-SELF-APPROVAL");
        (await RouteAsync("approve", new { setId, contentHash = set.Text("contentHash") }, both, HandlerUser)).Body.Text("code").ShouldBe("PLT-ERR-SELF-APPROVAL");
        (await RouteAsync("reject", new { setId, reason = "TOO_HIGH" }, both, HandlerUser)).Body.Text("code").ShouldBe("PLT-ERR-SELF-APPROVAL");
        (await RouteAsync("return", new { setId }, both, HandlerUser)).Body.Text("code").ShouldBe("PLT-ERR-SELF-APPROVAL");
        (await _money.SetAsync(setId)).Text("status").ShouldBe("PENDING_APPROVAL");
    }

    [Fact]
    public async Task Stale_set_is_CLM_ERR_SET_STALE_on_the_routes_and_a_wrong_hash_is_refused()
    {
        var (_, submit) = await ReferredAsync();
        var setId = submit.Text("setId");
        var (wrong, problem) = await RouteAsync("approve", new { setId, contentHash = new string('0', 64) });
        wrong.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        problem.Text("code").ShouldBe("CLM-ERR-SET-STALE");

        var (rejected, body) = await RouteAsync("reject", new { setId, reason = "TOO_HIGH", comment = "no" });
        rejected.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("status").ShouldBe("REJECTED");
        (await _money.SetAsync(setId)).Text("rejectionReason").ShouldBe("APPROVER_REJECTED");
        var (late, stale) = await RouteAsync("return", new { setId });
        late.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        stale.Text("code").ShouldBe("CLM-ERR-SET-STALE");
    }

    [Fact]
    public async Task Return_route_sends_the_set_back_without_changing_balances()
    {
        var (claim, submit) = await ReferredAsync();
        var (response, body) = await RouteAsync("return", new { setId = submit.Text("setId"), reason = "RETURNED", comment = "fix the amount" });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("status").ShouldBe("REJECTED");
        Amount((await _money.FinancialsAsync(claim))["totals"]!["openReserve"]).ShouldBe(0m);
    }

    [Fact]
    public async Task Daily_totals_equal_the_ledger_lines_of_the_business_date()
    {
        var claim = await _money.OpenClaimAsync(ClaimsSlice.Fnol(_slice.Policy()));
        (await _money.BuildAndSubmitAsync(claim, [Reserve(claim, 1200m)])).Text("status").ShouldBe("APPROVED");
        (await _money.BuildAndSubmitAsync(claim, [Reserve(claim, 9000m)])).Text("status").ShouldBe("PENDING_APPROVAL");

        var date = await _money.ScalarAsync<string>($"SELECT to_char(max(transaction_date), 'YYYY-MM-DD') FROM clm.financial_transaction WHERE claim_id = '{claim.ClaimId}'");
        var (response, body) = await _money.SendAsync(HttpMethod.Get, $"/api/clm/v1/financials/daily-totals?accountingDate={date}", roles: Manager, user: ManagerUser);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        var totals = body!["totalsByCurrencyAndCostType"]!["totals"]!.AsArray();
        var group = totals.Single(t => t!["kind"]!.GetValue<string>() == "RESERVE" && t["costType"]!.GetValue<string>() == "INDEMNITY" && t["currency"]!.GetValue<string>() == "EUR")!;

        // Only approved sets are ledger lines (the pending 9,000.00 is not); the total equals the stored reserve rows of the day.
        var ledger = await _money.ScalarAsync<string>(
            $"SELECT sum(t.amount)::text FROM clm.financial_transaction t JOIN clm.transaction_set s ON s.set_id = t.set_id WHERE s.status IN ('APPROVED','POSTED') AND t.kind = 'RESERVE' AND t.currency = 'EUR' AND t.transaction_date = '{date}'");
        decimal.Parse(group.Text("amount"), CultureInfo.InvariantCulture).ShouldBe(decimal.Parse(ledger, CultureInfo.InvariantCulture));
        group["transactionIds"]!.AsArray().Select(i => i!.GetValue<string>()).ShouldContain((await _money.ScalarAsync<string>($"SELECT txn_id::text FROM clm.financial_transaction WHERE claim_id = '{claim.ClaimId}' ORDER BY sequence LIMIT 1")));

        (await _money.SendAsync(HttpMethod.Get, "/api/clm/v1/financials/daily-totals?accountingDate=bad", roles: Manager, user: ManagerUser)).Response.StatusCode
            .ShouldBe(HttpStatusCode.BadRequest);
    }
}
