using System.Net;
using System.Text.Json.Nodes;
using CoreIns.IntegrationTests.Policy.Change;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Policy;

/// <summary>BO-POL-02: pol.Policy.getMany, pol.StatusModel.get, pol.EffectiveDate.limits and pol.Segment.changes over HTTP on PostgreSQL 17.</summary>
public sealed class PolicyReadsTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private ChangeHarness _h = null!;

    public async ValueTask InitializeAsync()
    {
        _h = new ChangeHarness(database);
        await _h.InitializeAsync();
    }

    public async ValueTask DisposeAsync() => await _h.DisposeAsync();

    [Fact]
    public async Task GetMany_returns_known_policies_lists_unknown_ids_and_refuses_bad_input()
    {
        var first = await _h.IssueAsync();
        var second = await _h.IssueAsync();
        var unknown = Guid.CreateVersion7();
        _h.SetDay(second, 1);

        var (ok, body) = await _h.SendAsync(HttpMethod.Get, $"/api/pol/v1/policies/get-many?ids={first.PolicyId},{second.PolicyId}&ids={unknown}");

        ok.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body!["policies"]!.AsArray().Select(p => p!["policy"]!["policyId"]!.GetValue<string>()).ShouldBe([first.PolicyId, second.PolicyId], ignoreOrder: true);
        body["notFound"]!.AsArray().Select(i => i!.GetValue<string>()).ShouldBe([unknown.ToString()]);

        (await _h.SendAsync(HttpMethod.Get, "/api/pol/v1/policies/get-many?ids=not-a-guid")).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await _h.SendAsync(HttpMethod.Get, "/api/pol/v1/policies/get-many")).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var tooMany = string.Join(',', Enumerable.Range(0, 51).Select(_ => Guid.NewGuid()));
        (await _h.SendAsync(HttpMethod.Get, $"/api/pol/v1/policies/get-many?ids={tooMany}")).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await _h.SendAsync(HttpMethod.Get, $"/api/pol/v1/policies/get-many?ids={first.PolicyId}", roles: ChangeHarness.NoChange)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task StatusModel_serves_the_job_quote_and_term_tables()
    {
        var (ok, body) = await _h.SendAsync(HttpMethod.Get, "/api/pol/v1/status-model/get");

        ok.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        var tables = body!["table"]!.AsArray().ToDictionary(t => t!["entity"]!.GetValue<string>(), t => t!);
        tables.Keys.ShouldBe(["Job", "Quote", "PolicyTerm"], ignoreOrder: true);
        tables["PolicyTerm"]["transitions"]!.AsArray().ShouldContain(
            t => t!["from"]!.GetValue<string>() == "Cancelled" && t["trigger"]!.GetValue<string>() == "Reinstate" && t["to"]!.GetValue<string>() == "InForce");
        tables["Job"]["terminalStates"]!.AsArray().Select(s => s!.GetValue<string>()).ShouldContain("Bound");
        (await _h.SendAsync(HttpMethod.Get, "/api/pol/v1/status-model/get", roles: ChangeHarness.NoChange)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task EffectiveDateLimits_follow_the_role_and_the_term()
    {
        var policy = await _h.IssueAsync();
        _h.SetDay(policy, 100);

        var (underwriter, uw) = await _h.SendAsync(HttpMethod.Get, $"/api/pol/v1/effective-date/limits?policyId={policy.PolicyId}");
        var (csr, csrBody) = await _h.SendAsync(HttpMethod.Get, $"/api/pol/v1/effective-date/limits?policyId={policy.PolicyId}&role={ChangeHarness.Csr}");

        underwriter.StatusCode.ShouldBe(HttpStatusCode.OK, uw?.ToJsonString());
        csr.StatusCode.ShouldBe(HttpStatusCode.OK, csrBody?.ToJsonString());
        uw!["backdateDays"]!.GetValue<int>().ShouldBe(30);
        csrBody!["backdateDays"]!.GetValue<int>().ShouldBe(0);
        DateTimeOffset.Parse(uw["earliest"]!.GetValue<string>()).ShouldBeLessThan(DateTimeOffset.Parse(csrBody["earliest"]!.GetValue<string>()));
        DateTimeOffset.Parse(uw["latest"]!.GetValue<string>()).UtcDateTime.ShouldBeLessThanOrEqualTo(policy.End);
        uw["overrideAvailable"]!.GetValue<bool>().ShouldBeFalse();

        (await _h.SendAsync(HttpMethod.Get, $"/api/pol/v1/effective-date/limits?policyId={Guid.NewGuid()}")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _h.SendAsync(HttpMethod.Get, "/api/pol/v1/effective-date/limits")).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await _h.SendAsync(HttpMethod.Get, $"/api/pol/v1/effective-date/limits?policyId={policy.PolicyId}", roles: ChangeHarness.NoChange)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task SegmentChanges_pages_with_a_stable_cursor_and_filters_since_a_transaction()
    {
        var first = await _h.IssueAsync();
        var second = await _h.IssueAsync();

        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var (ok, page) = await _h.SendAsync(HttpMethod.Get, "/api/pol/v1/segments/changes?limit=1" + (cursor is null ? string.Empty : $"&cursor={cursor}"));
            ok.StatusCode.ShouldBe(HttpStatusCode.OK, page?.ToJsonString());
            page!["items"]!.AsArray().Count.ShouldBeLessThanOrEqualTo(1);
            seen.AddRange(page["items"]!.AsArray().Select(i => i!["segmentId"]!.GetValue<string>()));
            cursor = page["nextCursor"]?.GetValue<string>();
            pages++;
        }
        while (cursor is not null && pages < 20);

        seen.Count.ShouldBeGreaterThanOrEqualTo(2);
        seen.Distinct().Count().ShouldBe(seen.Count);

        var (since, sinceBody) = await _h.SendAsync(HttpMethod.Get, $"/api/pol/v1/segments/changes?sinceTransactionId={second.IssuanceTransactionId}");
        since.StatusCode.ShouldBe(HttpStatusCode.OK, sinceBody?.ToJsonString());
        var items = sinceBody!["items"]!.AsArray();
        items.ShouldNotBeEmpty();
        items.ShouldAllBe(i => i!["policyId"]!.GetValue<string>() == second.PolicyId);

        (await _h.SendAsync(HttpMethod.Get, "/api/pol/v1/segments/changes?cursor=garbage")).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await _h.SendAsync(HttpMethod.Get, "/api/pol/v1/segments/changes?limit=0")).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await _h.SendAsync(HttpMethod.Get, $"/api/pol/v1/segments/changes?sinceTransactionId={Guid.NewGuid()}")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _h.SendAsync(HttpMethod.Get, "/api/pol/v1/segments/changes", roles: ChangeHarness.NoChange)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        first.PolicyId.ShouldNotBe(second.PolicyId);
    }
}
