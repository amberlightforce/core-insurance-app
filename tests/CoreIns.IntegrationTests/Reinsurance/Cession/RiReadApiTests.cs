using System.Net;
using System.Text.Json.Nodes;
using CoreIns.IntegrationTests.Reinsurance.Registry;

namespace CoreIns.IntegrationTests.Reinsurance.Cession;

/// <summary>NX-HD-08a: the cession, cession-exception, programme and participation reads (keyset paging, legal-entity scoping, permissions). Synthetic data.</summary>
public sealed class RiReadApiTests(PostgresFixture database) : IClassFixture<PostgresFixture>
{
    private static string Dt(int minutes) => $"timestamptz '2026-02-10 09:00:00+00' + interval '{minutes} minutes'";

    private static async Task<(string ContractId, string LegalEntity)> TreatyAsync(RegistrySlice slice)
    {
        var lead = await slice.OrganisationAsync("Synthetic Lead Re " + Guid.NewGuid().ToString("N")[..6]);
        var follow = await slice.OrganisationAsync("Synthetic Follow Re " + Guid.NewGuid().ToString("N")[..6]);
        var (id, _) = await slice.ApprovedAsync(RegistrySlice.Body(RegistrySlice.NewProduct(), "FIRE", lead, follow));
        var legalEntity = await slice.ScalarAsync<Guid>($"SELECT legal_entity_id FROM ri.contract WHERE contract_id = '{id}'");
        return (id, legalEntity.ToString());
    }

    private static string InsertCession(Guid id, string legalEntity, Guid term, decimal delta, int minute) =>
        $"INSERT INTO ri.cession (cession_id, legal_entity_id, treaty_id, contract_year, term_id, set_key, written_premium, target, booked_before, delta, mode, recorded_at) " +
        $"VALUES ('{id}', '{legalEntity}', 'QS-TEST', 2026, '{term}', '{Guid.NewGuid()}', 1000, {delta}, 0, {delta}, 'NET', {Dt(minute)});";

    [Fact]
    public async Task Cessions_page_by_key_are_scoped_to_the_legal_entity_and_permissioned()
    {
        await using var slice = new RegistrySlice(database);
        var (_, legalEntity) = await TreatyAsync(slice);
        var policy = Guid.NewGuid();
        var term = Guid.NewGuid();
        var otherPolicy = Guid.NewGuid();
        var otherTerm = Guid.NewGuid();
        var ids = Enumerable.Range(0, 3).Select(_ => Guid.CreateVersion7()).Order().ToArray();
        var foreign = Guid.CreateVersion7();
        var foreignLe = Guid.NewGuid();
        await slice.ExecuteAsync(
            $"INSERT INTO ri.premium_delta (event_id, legal_entity_id, policy_id, term_id, coverage_code, charge_category, amount, valid_from, set_size, recorded_at) " +
            $"VALUES ('{Guid.NewGuid()}', '{legalEntity}', '{policy}', '{term}', 'FIRE', 'PREMIUM', 1000, '2026-03-01', 1, {Dt(0)}), " +
            $"('{Guid.NewGuid()}', '{foreignLe}', '{otherPolicy}', '{otherTerm}', 'FIRE', 'PREMIUM', 1000, '2026-03-01', 1, {Dt(0)});" +
            string.Concat(ids.Select((id, i) => InsertCession(id, legalEntity, term, 100 + i, i + 1))) + InsertCession(foreign, foreignLe.ToString(), otherTerm, 5, 9));

        var (first, firstBody) = await slice.SendAsync(HttpMethod.Get, $"/api/ri/v1/cessions/list-by-policy?policy={policy}&limit=2");
        first.StatusCode.ShouldBe(HttpStatusCode.OK, firstBody?.ToJsonString());
        firstBody!["items"]!.AsArray().Count.ShouldBe(2);
        var cursor = firstBody["nextCursor"]!.GetValue<string>();
        var (_, secondBody) = await slice.SendAsync(HttpMethod.Get, $"/api/ri/v1/cessions/list-by-policy?policy={policy}&limit=2&cursor={cursor}");
        secondBody!["items"]!.AsArray().Count.ShouldBe(1);
        secondBody["nextCursor"].ShouldBeNull();
        secondBody["items"]![0]!["intervals"]!["cessionId"]!.GetValue<string>().ShouldBe(ids[2].ToString());

        // The other legal entity's policy shows nothing, and its cession is "not found" here.
        var (_, none) = await slice.SendAsync(HttpMethod.Get, $"/api/ri/v1/cessions/list-by-policy?policy={otherPolicy}");
        none!["items"]!.AsArray().Count.ShouldBe(0);
        (await slice.SendAsync(HttpMethod.Get, $"/api/ri/v1/cessions/{foreign}")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await slice.SendAsync(HttpMethod.Get, $"/api/ri/v1/cessions/{Guid.NewGuid()}")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await slice.SendAsync(HttpMethod.Get, "/api/ri/v1/cessions/not-a-guid")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var (one, oneBody) = await slice.SendAsync(HttpMethod.Get, $"/api/ri/v1/cessions/{ids[0]}");
        one.StatusCode.ShouldBe(HttpStatusCode.OK, oneBody?.ToJsonString());
        oneBody!["shares"]!["treatyId"]!.GetValue<string>().ShouldBe("QS-TEST");
        oneBody["deltas"]!["delta"]!.GetValue<string>().ShouldBe("100.00");

        // Fail closed on bad input and missing permission.
        foreach (var bad in new[] { "limit=0", "limit=100000", "cursor=zzz", "knownAt=nope", "knownAt=2999-01-01T00:00:00Z" })
        {
            (await slice.SendAsync(HttpMethod.Get, $"/api/ri/v1/cessions/list-by-policy?policy={policy}&{bad}")).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, bad);
        }

        (await slice.SendAsync(HttpMethod.Get, "/api/ri/v1/cessions/list-by-policy")).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await slice.SendAsync(HttpMethod.Get, $"/api/ri/v1/cessions/list-by-policy?policy={policy}", roles: "Staff.Underwriter")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Cession_exceptions_page_and_stay_in_their_legal_entity()
    {
        await using var slice = new RegistrySlice(database);
        var (_, legalEntity) = await TreatyAsync(slice);
        var ids = Enumerable.Range(0, 3).Select(_ => Guid.CreateVersion7()).Order().ToArray();
        var foreign = Guid.CreateVersion7();
        static string Row(Guid id, string le) =>
            $"INSERT INTO ri.cession_exception (exception_id, legal_entity_id, term_id, source_event_id, code, detail, recorded_at) VALUES ('{id}', '{le}', '{Guid.NewGuid()}', '{Guid.NewGuid()}', 'UNKNOWN_COVERAGE', 'synthetic', {Dt(0)});";
        await slice.ExecuteAsync(string.Concat(ids.Select(id => Row(id, legalEntity))) + Row(foreign, Guid.NewGuid().ToString()));

        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var (response, page) = await slice.SendAsync(HttpMethod.Get, "/api/ri/v1/cession-exceptions?limit=2" + (cursor is null ? string.Empty : $"&cursor={cursor}"));
            response.StatusCode.ShouldBe(HttpStatusCode.OK, page?.ToJsonString());
            seen.AddRange(page!["items"]!.AsArray().Select(i => i!["exception"]!["exceptionId"]!.GetValue<string>()));
            cursor = page["nextCursor"]?.GetValue<string>();
        }
        while (cursor is not null);

        // Other tests share the database: assert only that ours are present in key order, once, and the foreign one is absent.
        seen.Where(s => ids.Any(i => i.ToString() == s)).ToArray().ShouldBe(ids.Select(i => i.ToString()).ToArray());
        seen.ShouldNotContain(foreign.ToString());
        seen.Distinct().Count().ShouldBe(seen.Count);
        (await slice.SendAsync(HttpMethod.Get, "/api/ri/v1/cession-exceptions?limit=999")).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await slice.SendAsync(HttpMethod.Get, "/api/ri/v1/cession-exceptions", roles: "Staff.Underwriter")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Participations_and_programmes_are_read_from_the_registry()
    {
        await using var slice = new RegistrySlice(database);
        var (contractId, _) = await TreatyAsync(slice);

        var (response, page) = await slice.SendAsync(HttpMethod.Get, $"/api/ri/v1/participations?contractId={contractId}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, page?.ToJsonString());
        var items = page!["items"]!.AsArray();
        items.Count.ShouldBe(2);
        items.Count(i => i!["participations"]!["lead"]!.GetValue<bool>()).ShouldBe(1);
        items[0]!["checks"]!["signedLinesMatchPlaced"]!.GetValue<bool>().ShouldBeTrue();
        items[0]!["checks"]!["signedLinesSumPct"]!.GetValue<string>().ShouldBe("100");

        var (_, paged) = await slice.SendAsync(HttpMethod.Get, $"/api/ri/v1/participations?contractId={contractId}&limit=1");
        paged!["items"]!.AsArray().Count.ShouldBe(1);
        paged["nextCursor"].ShouldNotBeNull();
        (await slice.SendAsync(HttpMethod.Get, "/api/ri/v1/participations?cursor=zzz")).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var (listResponse, programmes) = await slice.SendAsync(HttpMethod.Get, "/api/ri/v1/programmes?limit=50");
        listResponse.StatusCode.ShouldBe(HttpStatusCode.OK, programmes?.ToJsonString());
        var programme = programmes!["items"]!.AsArray().Single(p => p!["programmes"]!["contractYear"]!.GetValue<int>() == 2026);
        programme!["programmes"]!["contracts"]!.AsArray().Any(c => c!["contractId"]!.GetValue<string>() == contractId).ShouldBeTrue();

        var (getResponse, one) = await slice.SendAsync(HttpMethod.Get, "/api/ri/v1/programmes/2026");
        getResponse.StatusCode.ShouldBe(HttpStatusCode.OK, one?.ToJsonString());
        one!["programmes"]!["programmeId"]!.GetValue<string>().ShouldBe("2026");
        (await slice.SendAsync(HttpMethod.Get, "/api/ri/v1/programmes/1901")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await slice.SendAsync(HttpMethod.Get, "/api/ri/v1/programmes/abc")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await slice.SendAsync(HttpMethod.Get, "/api/ri/v1/programmes?limit=0")).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await slice.SendAsync(HttpMethod.Get, "/api/ri/v1/programmes", roles: "Staff.Underwriter")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
