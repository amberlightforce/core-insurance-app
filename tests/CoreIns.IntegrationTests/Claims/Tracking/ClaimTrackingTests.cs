using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using static CoreIns.IntegrationTests.Claims.ClaimsMoney;

namespace CoreIns.IntegrationTests.Claims.Tracking;

/// <summary>
/// BO-CLM-08: the claim tracking watch list and the CAT event register (declare, tag by Claim.update with window validation, aggregate,
/// close) over HTTP on a real PostgreSQL.
/// </summary>
public sealed class ClaimTrackingTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly string[] Perils = ["STORM"];

    private ClaimsSlice _slice = null!;
    private ClaimsMoney _money = null!;

    public ValueTask InitializeAsync()
    {
        _slice = new ClaimsSlice(database.AppConnectionString);
        _money = new ClaimsMoney(_slice.Factory, _slice.Client, database.SuperuserConnectionString);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _slice.DisposeAsync();

    private async Task<int> VersionAsync(MoneyClaim claim) =>
        int.Parse((await _money.ClaimAsync(claim)).Text("summary.recordVersion"), CultureInfo.InvariantCulture);

    private async Task<(HttpResponseMessage Response, JsonNode? Body)> UpdateAsync(MoneyClaim claim, object changes) =>
        await _money.SendAsync(HttpMethod.Patch, $"/api/clm/v1/claims/{claim.ClaimId}", new { expectedRecordVersion = await VersionAsync(claim), changes, reason = "TRACKING" });

    private Task<(HttpResponseMessage Response, JsonNode? Body)> DeclareAsync(string code, string from, string? to = null, string roles = Manager) =>
        _money.SendAsync(HttpMethod.Post, "/api/clm/v1/cat-events",
            new { catCode = code, name = "Storm " + code, perils = Perils, region = "Attica", windowFrom = from, windowTo = to }, roles, ManagerUser);

    private Task<long> EventCountAsync(string aggregateId, string type) => _money.ScalarAsync<long>(
        $"SELECT count(*) FROM plt.outbox_message WHERE aggregate_id = '{aggregateId}' AND event_type = '{type}'");

    [Fact]
    public async Task Milestone_shows_in_tracking_and_ClaimUpdated_keeps_its_shape()
    {
        var claim = await _money.OpenClaimAsync(ClaimsSlice.Fnol(_slice.Policy()));
        var (updated, body) = await UpdateAsync(claim, new { milestone = "ASSESSOR_APPOINTED" });
        updated.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());

        var (status, tracking) = await _money.SendAsync(HttpMethod.Get, $"/api/clm/v1/claim-trackings/{claim.ClaimId}");
        status.StatusCode.ShouldBe(HttpStatusCode.OK, tracking?.ToJsonString());
        tracking.Text("trackingView.milestone").ShouldBe("ASSESSOR_APPOINTED");
        tracking.Text("trackingView.claimNumber").ShouldBe(claim.ClaimNumber);
        tracking.Text("trackingView.status").ShouldBe("OPEN");

        // By claim number too.
        (await _money.SendAsync(HttpMethod.Get, $"/api/clm/v1/claim-trackings/{claim.ClaimNumber}")).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _money.SendAsync(HttpMethod.Get, $"/api/clm/v1/claim-trackings/{Guid.NewGuid()}")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await _money.ScalarAsync<string>(
            $"SELECT payload->>'newMilestone' FROM plt.outbox_message WHERE event_type = 'ClaimUpdated' AND aggregate_id = '{claim.ClaimId}' ORDER BY aggregate_sequence DESC LIMIT 1"))
            .ShouldBe("ASSESSOR_APPOINTED");
        (await _money.ScalarAsync<long>(
            $"SELECT count(*) FROM plt.audit_event WHERE operation = 'clm.Claim.update' AND outcome = 'Succeeded' AND object_id = '{claim.ClaimId}' AND changes::text LIKE '%ASSESSOR_APPOINTED%'"))
            .ShouldBe(1);

        // An empty change is refused.
        (await UpdateAsync(claim, new { })).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Watch_list_pages_stably_oldest_first_and_filters()
    {
        var claims = new List<MoneyClaim>();
        for (var i = 0; i < 4; i++)
        {
            claims.Add(await _money.OpenClaimAsync(ClaimsSlice.Fnol(_slice.Policy())));
        }

        await _money.BuildAndSubmitAsync(claims[0], [Reserve(claims[0], 120m)]);
        var all = await ListAsync("limit=200");
        all.Count.ShouldBeGreaterThanOrEqualTo(4);
        all.Select(i => i.Text("trackingView.openedAt")).ShouldBe(all.Select(i => i.Text("trackingView.openedAt")).Order(StringComparer.Ordinal));

        var paged = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var (response, body) = await _money.SendAsync(HttpMethod.Get, "/api/clm/v1/claim-trackings?limit=2" + (cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor)));
            response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
            paged.AddRange(body!["items"]!.AsArray().Select(i => i.Text("trackingView.claimNumber")));
            cursor = body["nextCursor"]?.GetValue<string>();
            pages++;
        }
        while (cursor is not null && pages < 100);
        paged.ShouldBe(all.Select(i => i.Text("trackingView.claimNumber")));
        paged.Distinct().Count().ShouldBe(paged.Count);

        (await ListAsync($"claim={claims[1].ClaimNumber}")).Single().Text("trackingView.claimId").ShouldBe(claims[1].ClaimId);
        (await ListAsync("largeLoss=true")).ShouldBeEmpty();
        (await ListAsync("minAgeDays=30")).ShouldBeEmpty();
        (await ListAsync("status=CLOSED")).Select(i => i.Text("trackingView.claimId")).ShouldNotContain(claims[0].ClaimId);
        (await _money.SendAsync(HttpMethod.Get, "/api/clm/v1/claim-trackings?cursor=garbage")).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _money.SendAsync(HttpMethod.Get, "/api/clm/v1/claim-trackings", roles: "Staff.Nobody")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private async Task<List<JsonNode>> ListAsync(string query)
    {
        var (response, body) = await _money.SendAsync(HttpMethod.Get, "/api/clm/v1/claim-trackings?" + query);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return [.. body!["items"]!.AsArray().Select(i => i!)];
    }

    [Fact]
    public async Task Cat_event_tag_validates_window_and_state_and_aggregate_totals_equal_the_claim_financials()
    {
        var code = "STORM_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var from = ClaimsSlice.DaysAgo(10).ToString();
        (await DeclareAsync(code, from, roles: Handler)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var (declared, declaredBody) = await DeclareAsync(code, from);
        declared.StatusCode.ShouldBe(HttpStatusCode.Created, declaredBody?.ToJsonString());
        declaredBody.Text("catEvent.status").ShouldBe("OPEN");
        (await DeclareAsync(code, from)).Body.Text("code").ShouldBe("CLM-ERR-CAT-EVENT");
        (await DeclareAsync("BAD_WINDOW_" + code, from, ClaimsSlice.DaysAgo(20).ToString())).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await EventCountAsync(declaredBody.Text("catEvent.catEventId"), "CatEventDeclared")).ShouldBe(1);

        var inside1 = await _money.OpenClaimAsync(ClaimsSlice.Fnol(_slice.Policy(), ClaimsSlice.DaysAgo(3)));
        var inside2 = await _money.OpenClaimAsync(ClaimsSlice.Fnol(_slice.Policy(), ClaimsSlice.DaysAgo(2)));
        var outside = await _money.OpenClaimAsync(ClaimsSlice.Fnol(_slice.Policy(), ClaimsSlice.DaysAgo(30)));
        await _money.BuildAndSubmitAsync(inside1, [Reserve(inside1, 100m)]);
        await _money.BuildAndSubmitAsync(inside2, [Reserve(inside2, 250m)]);

        (await UpdateAsync(outside, new { catEventCode = code })).Body.Text("code").ShouldBe("CLM-ERR-CAT-EVENT");
        (await UpdateAsync(outside, new { catEventCode = "NO_SUCH_EVENT" })).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await UpdateAsync(inside1, new { catEventCode = code, untagCatEvent = true })).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await UpdateAsync(inside1, new { untagCatEvent = true })).Body.Text("code").ShouldBe("CLM-ERR-CAT-EVENT");
        (await UpdateAsync(inside1, new { catEventCode = code })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await UpdateAsync(inside2, new { catEventCode = code })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await EventCountAsync(inside1.ClaimId, "CatEventAssigned")).ShouldBe(1);

        // Claims by CAT event, and totals that equal the sum of the per-claim financials.
        var tagged = await ListAsync($"catCode={code}");
        tagged.Select(t => t.Text("trackingView.claimId")).Order().ShouldBe(new[] { inside1.ClaimId, inside2.ClaimId }.Order());
        var perClaim = 0m;
        foreach (var claim in new[] { inside1, inside2 })
        {
            perClaim += Amount((await _money.FinancialsAsync(claim))["totals"]!["reserved"]);
        }

        var (aggregate, body) = await _money.SendAsync(HttpMethod.Get, $"/api/clm/v1/cat-events/aggregate?catCode={code}");
        aggregate.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        var ev = body!["events"]!.AsArray().Single()!;
        ev.Text("claimCount").ShouldBe("2");
        var totals = ev["totalsByCurrency"]!.AsArray().Single()!;
        Amount(totals["reserved"]).ShouldBe(perClaim);
        Amount(totals["reserved"]).ShouldBe(350m);
        Amount(totals["openReserve"]).ShouldBe(350m);
        ev["totalsByLine"]!.AsArray().Sum(l => Amount(l!["totals"]!.AsArray().Single()!["reserved"])).ShouldBe(350m);

        // Untag, then close: no further tagging, existing tags stay.
        (await UpdateAsync(inside2, new { untagCatEvent = true })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _money.SendAsync(HttpMethod.Post, "/api/clm/v1/cat-events/close", new { catCode = code, reason = "EVENT_OVER" }, Handler, HandlerUser)).Response.StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);
        var (closed, closedBody) = await _money.SendAsync(HttpMethod.Post, "/api/clm/v1/cat-events/close", new { catCode = code, reason = "EVENT_OVER" }, Manager, ManagerUser);
        closed.StatusCode.ShouldBe(HttpStatusCode.OK, closedBody?.ToJsonString());
        closedBody.Text("catEvent.status").ShouldBe("CLOSED");
        closedBody.Text("catEvent.taggedClaimCount").ShouldBe("1");
        (await UpdateAsync(inside2, new { catEventCode = code })).Body.Text("code").ShouldBe("CLM-ERR-CAT-EVENT");
        (await _money.SendAsync(HttpMethod.Post, "/api/clm/v1/cat-events/close", new { catCode = code }, Manager, ManagerUser)).Body.Text("code").ShouldBe("CLM-ERR-CAT-EVENT");
        (await EventCountAsync(declaredBody.Text("catEvent.catEventId"), "CatEventChanged")).ShouldBe(1);

        var (listed, listBody) = await _money.SendAsync(HttpMethod.Get, "/api/clm/v1/cat-events?status=CLOSED");
        listed.StatusCode.ShouldBe(HttpStatusCode.OK, listBody?.ToJsonString());
        listBody!["events"]!.AsArray().Select(e => e.Text("catCode")).ShouldContain(code);
    }
}
