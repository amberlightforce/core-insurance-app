using System.Net;
using static CoreIns.IntegrationTests.Claims.ClaimsMoney;

namespace CoreIns.IntegrationTests.Claims;

/// <summary>NX-EC-08: two-session races on the claim financial engine; each must have exactly one effect and the loser a clean 409.</summary>
public sealed class ClaimsRaceTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
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

    [Fact]
    public async Task Two_reserve_changes_built_on_the_same_state_and_submitted_together_apply_once()
    {
        var claim = await _money.OpenClaimAsync(ClaimsSlice.Fnol(_slice.Policy()));
        var (builtA, a) = await _money.BuildAsync(claim, [Reserve(claim, 1200m)]);
        var (builtB, b) = await _money.BuildAsync(claim, [Reserve(claim, 1500m)]);
        builtA.StatusCode.ShouldBe(HttpStatusCode.OK, a?.ToJsonString());
        builtB.StatusCode.ShouldBe(HttpStatusCode.OK, b?.ToJsonString());

        var results = await Task.WhenAll(
            Task.Run(() => _money.SubmitAsync(a.Text("setId"))),
            Task.Run(() => _money.SubmitAsync(b.Text("setId"))));

        var summary = string.Join(" | ", results.Select(r => r.Body?.ToJsonString()));
        results.Count(r => r.Response.StatusCode == HttpStatusCode.OK).ShouldBe(1, summary);
        var loser = results.Single(r => r.Response.StatusCode != HttpStatusCode.OK);
        loser.Response.StatusCode.ShouldBe(HttpStatusCode.Conflict, summary);
        loser.Body.Text("code").ShouldBe("CLM-ERR-SET-STALE");
        await _money.DrainAsync();
        (await _money.ScalarAsync<long>($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'ReserveChanged' AND aggregate_id = '{claim.ClaimId}'")).ShouldBe(1);
        Amount((await _money.FinancialsAsync(claim))["totals"]!["openReserve"]).ShouldBeOneOf(1200m, 1500m);
    }

    [Fact]
    public async Task A_referred_payment_approved_by_two_checkers_at_once_is_disbursed_once()
    {
        var claim = await _money.OpenClaimAsync(ClaimsSlice.Fnol(_slice.Policy()));
        await _money.BuildAndSubmitAsync(claim, [Reserve(claim, 1200m)]);
        await _money.BuildAndSubmitAsync(claim, [Reserve(claim, 5000m)], Manager, ManagerUser);
        var (account, _) = await _money.CaptureAsync(claim);
        var pending = await _money.BuildAndSubmitAsync(claim, [Payment(claim, 6200m, account)]);
        pending.Text("status").ShouldBe("PENDING_APPROVAL");
        var requestId = pending.Text("approvalRequestId");

        var results = await Task.WhenAll(_money.DecideAsync(requestId), _money.DecideAsync(requestId, user: "manager-maria"));
        var summary = string.Join(" | ", results.Select(r => r.Body?.ToJsonString()));
        results.Count(r => r.Response.StatusCode == HttpStatusCode.OK).ShouldBe(1, summary);
        results.Single(r => r.Response.StatusCode != HttpStatusCode.OK).Response.StatusCode.ShouldBe(HttpStatusCode.Conflict, summary);
        await _money.DrainAsync();

        (await _money.ScalarAsync<long>($"SELECT count(*) FROM bil.disbursement WHERE claim_id = '{claim.ClaimId}'")).ShouldBe(1);
    }
}
