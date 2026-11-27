using System.Net;
using System.Text.Json.Nodes;
using static CoreIns.IntegrationTests.Party.PartyApi;
using static CoreIns.IntegrationTests.Reinsurance.Registry.RegistrySlice;

namespace CoreIns.IntegrationTests.Reinsurance.Registry;

/// <summary>BO-RI-01: the programme aggregate (create, update, renew), its validation and its versioning. Synthetic data.</summary>
public sealed class ProgrammeLifecycleTests(PostgresFixture database) : IClassFixture<PostgresFixture>
{
    private static string Url => "/api/ri/v1/programmes";

    /// <summary>Two Draft treaties of 2026 in one legal entity: A carries 500k xs 500k, B carries 1m xs 1m.</summary>
    private static async Task<(string A, string B)> TreatiesAsync(RegistrySlice slice, string product, string from = "2026-01-01", string to = "2027-01-01")
    {
        var lead = await slice.OrganisationAsync("Synthetic Lead Re " + Guid.NewGuid().ToString("N")[..6]);
        var follow = await slice.OrganisationAsync("Synthetic Follow Re " + Guid.NewGuid().ToString("N")[..6]);
        var (a, _, _) = await slice.CreateAsync(Body(product, "OD", lead, follow, from, to));
        var (b, _, _) = await slice.CreateAsync(Body(product, "OD", lead, follow, from, to, body =>
            body["layers"] = new JsonArray(new JsonObject
            {
                ["layerNo"] = 1, ["attachment"] = Money("1000000.00"), ["limit"] = Money("1000000.00"), ["aad"] = Money("0.00"), ["aal"] = null,
            })));
        return (a, b);
    }

    private static JsonObject Layer(int no, string attachment, string limit, string contractId) =>
        new() { ["layerNo"] = no, ["attachment"] = Money(attachment), ["limit"] = Money(limit), ["contractId"] = contractId };

    private static JsonObject ProgrammeBody(string line, string a, string b, string? code = null, string from = "2026-01-01", string to = "2027-01-01") => new()
    {
        ["code"] = code ?? "PRG-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
        ["name"] = "Synthetic motor XoL programme",
        ["lineCode"] = line,
        ["period"] = new JsonObject { ["from"] = from, ["to"] = to },
        ["layers"] = new JsonArray(Layer(1, "500000.00", "500000.00", a), Layer(2, "1000000.00", "1000000.00", b)),
    };

    private static async Task<(JsonNode Programme, JsonNode? Body)> CreateAsync(RegistrySlice slice, JsonObject body)
    {
        var (response, created) = await slice.SendAsync(HttpMethod.Post, Url, body);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, created?.ToJsonString());
        return (created!["programme"]!, created);
    }

    private static async Task ShouldRefuseAsync(RegistrySlice slice, JsonObject body, string code)
    {
        var (response, problem) = await slice.SendAsync(HttpMethod.Post, Url, body);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, problem?.ToJsonString());
        problem!.ToJsonString().ShouldContain(code);
    }

    [Fact]
    public async Task Create_stores_a_draft_programme_with_its_ladder_and_get_and_list_read_it()
    {
        await using var slice = new RegistrySlice(database);
        var line = NewProduct();
        var (a, b) = await TreatiesAsync(slice, line);
        var (programme, created) = await CreateAsync(slice, ProgrammeBody(line, a, b));

        programme["status"]!.GetValue<string>().ShouldBe("DRAFT");
        programme["versionNo"]!.GetValue<int>().ShouldBe(1);
        programme["programmeYear"]!.GetValue<int>().ShouldBe(2026);
        programme["retention"]!["amount"]!.GetValue<string>().ShouldBe("500000.00");
        programme["layers"]!.AsArray().Count.ShouldBe(2);
        programme["contracts"]!.AsArray().Count.ShouldBe(2);
        created!["diff"]!.AsArray().Any(d => d!["field"]!.GetValue<string>() == "layers[2]").ShouldBeTrue();

        var id = programme["programmeId"]!.GetValue<string>();
        var (get, one) = await slice.SendAsync(HttpMethod.Get, $"{Url}/{id}");
        get.StatusCode.ShouldBe(HttpStatusCode.OK, one?.ToJsonString());
        one!["programme"]!["code"]!.GetValue<string>().ShouldBe(programme["code"]!.GetValue<string>());

        var (_, list) = await slice.SendAsync(HttpMethod.Get, $"{Url}?limit=200");
        list!["items"]!.AsArray().Any(i => i!["programme"]!["programmeId"]!.GetValue<string>() == id).ShouldBeTrue();
        (await slice.SendAsync(HttpMethod.Get, $"{Url}/{Guid.NewGuid()}")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await slice.SendAsync(HttpMethod.Get, $"{Url}/abc")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await slice.SendAsync(HttpMethod.Get, $"{Url}?limit=0")).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await slice.SendAsync(HttpMethod.Get, $"{Url}?cursor=zzz")).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await slice.SendAsync(HttpMethod.Post, Url, ProgrammeBody(line, a, b), roles: Manager)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await slice.SendAsync(HttpMethod.Get, Url, roles: "Staff.Underwriter")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Layers_must_be_contiguous_carried_by_their_contract_and_in_force_for_the_period()
    {
        await using var slice = new RegistrySlice(database);
        var line = NewProduct();
        var (a, b) = await TreatiesAsync(slice, line);

        var gap = ProgrammeBody(line, a, b);
        gap["layers"] = new JsonArray(Layer(1, "500000.00", "500000.00", a), Layer(2, "1500000.00", "1000000.00", b));
        await ShouldRefuseAsync(slice, gap, "LAYERS_NOT_CONTIGUOUS");

        var overlap = ProgrammeBody(line, a, b);
        overlap["layers"] = new JsonArray(Layer(1, "500000.00", "500000.00", a), Layer(2, "900000.00", "1000000.00", b));
        await ShouldRefuseAsync(slice, overlap, "LAYERS_OVERLAP");

        var notCarried = ProgrammeBody(line, a, b);
        notCarried["layers"] = new JsonArray(Layer(1, "500000.00", "400000.00", a));
        await ShouldRefuseAsync(slice, notCarried, "LAYER_NOT_IN_CONTRACT");

        await ShouldRefuseAsync(slice, ProgrammeBody(line, a, Guid.NewGuid().ToString()), "CONTRACT_NOT_FOUND");
        await ShouldRefuseAsync(slice, ProgrammeBody(line, a, b, from: "2026-01-01", to: "2028-01-01"), "CONTRACT_PERIOD_DOES_NOT_COVER");

        var bad = ProgrammeBody(line, a, b, code: "bad code");
        var (response, problem) = await slice.SendAsync(HttpMethod.Post, Url, bad);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, problem?.ToJsonString());
        (await slice.ScalarAsync<long>($"SELECT count(*) FROM ri.programme WHERE line_code = '{line}'")).ShouldBe(0);
    }

    [Fact]
    public async Task A_second_programme_of_the_line_over_the_same_period_or_with_the_same_code_and_year_is_refused()
    {
        await using var slice = new RegistrySlice(database);
        var line = NewProduct();
        var (a, b) = await TreatiesAsync(slice, line);
        var first = ProgrammeBody(line, a, b);
        await CreateAsync(slice, first);

        await ShouldRefuseAsync(slice, ProgrammeBody(line, a, b), "PROGRAMME_PERIOD_OVERLAP");
        var sameCode = ProgrammeBody(NewProduct(), a, b, code: first["code"]!.GetValue<string>());
        await ShouldRefuseAsync(slice, sameCode, "PROGRAMME_YEAR_EXISTS");
    }

    [Fact]
    public async Task A_draft_is_edited_in_place_and_an_active_programme_gets_a_new_version()
    {
        await using var slice = new RegistrySlice(database);
        var line = NewProduct();
        var (a, b) = await TreatiesAsync(slice, line);
        var (programme, _) = await CreateAsync(slice, ProgrammeBody(line, a, b));
        var id = programme["programmeId"]!.GetValue<string>();
        var version = programme["recordVersion"]!.GetValue<int>();

        var (response, edited) = await slice.SendAsync(HttpMethod.Patch, $"{Url}/{id}", new { expectedRecordVersion = version, name = "Renamed programme" });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, edited?.ToJsonString());
        edited!["programme"]!["name"]!.GetValue<string>().ShouldBe("Renamed programme");
        edited["programme"]!["versionNo"]!.GetValue<int>().ShouldBe(1);
        edited["diff"]!.AsArray().Single()!["field"]!.GetValue<string>().ShouldBe("name");

        var stale = await slice.SendAsync(HttpMethod.Patch, $"{Url}/{id}", new { expectedRecordVersion = version, name = "Late" });
        stale.Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // Dropping a layer on a Draft replaces the layer rows in place.
        version = edited["programme"]!["recordVersion"]!.GetValue<int>();
        var (_, fewer) = await slice.SendAsync(HttpMethod.Patch, $"{Url}/{id}", new { expectedRecordVersion = version, layers = new[] { Layer(1, "500000.00", "500000.00", a) } });
        fewer!["programme"]!["layers"]!.AsArray().Count.ShouldBe(1);
        version = fewer["programme"]!["recordVersion"]!.GetValue<int>();

        // The approval (BO-RI-02) makes the version ACTIVE; its content is then sealed in the database, and an update starts version 2.
        await slice.ExecuteAsync($"UPDATE ri.programme_version SET status = 'PENDING_APPROVAL' WHERE programme_id = '{id}'; UPDATE ri.programme_version SET status = 'ACTIVE' WHERE programme_id = '{id}';");
        await Should.ThrowAsync<Exception>(() => slice.ExecuteAsync($"UPDATE ri.programme_version SET name = 'Tampered' WHERE programme_id = '{id}'"));
        await Should.ThrowAsync<Exception>(() => slice.ExecuteAsync($"DELETE FROM ri.programme_layer WHERE version_id IN (SELECT version_id FROM ri.programme_version WHERE programme_id = '{id}')"));
        await Should.ThrowAsync<Exception>(() => slice.ExecuteAsync($"DELETE FROM ri.programme_version WHERE programme_id = '{id}'"));

        var (next, second) = await slice.SendAsync(HttpMethod.Patch, $"{Url}/{id}", new { expectedRecordVersion = version, name = "Second version", layers = new[] { Layer(1, "500000.00", "500000.00", a), Layer(2, "1000000.00", "1000000.00", b) } });
        next.StatusCode.ShouldBe(HttpStatusCode.OK, second?.ToJsonString());
        second!["programme"]!["versionNo"]!.GetValue<int>().ShouldBe(2);
        second["programme"]!["status"]!.GetValue<string>().ShouldBe("DRAFT");
        second["programme"]!["versions"]!.AsArray().Select(v => v!["status"]!.GetValue<string>()).ShouldBe(["ACTIVE", "DRAFT"]);
        (await slice.ScalarAsync<string>($"SELECT name FROM ri.programme_version WHERE programme_id = '{id}' AND version_no = 1")).ShouldBe("Renamed programme");

        // A version that waits for its decision is not edited.
        await slice.ExecuteAsync($"UPDATE ri.programme_version SET status = 'PENDING_APPROVAL' WHERE programme_id = '{id}' AND version_no = 2");
        var waiting = await slice.SendAsync(HttpMethod.Patch, $"{Url}/{id}", new { expectedRecordVersion = second["programme"]!["recordVersion"]!.GetValue<int>(), name = "No" });
        waiting.Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Renew_copies_the_layers_and_the_treaties_as_drafts_of_the_next_year_and_refuses_a_second_renewal()
    {
        await using var slice = new RegistrySlice(database);
        var line = NewProduct();
        var (a, b) = await TreatiesAsync(slice, line);
        var (programme, _) = await CreateAsync(slice, ProgrammeBody(line, a, b));
        var id = programme["programmeId"]!.GetValue<string>();

        var (response, renewed) = await slice.SendAsync(HttpMethod.Post, $"{Url}/renew", new { programmeId = id });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, renewed?.ToJsonString());
        var next = renewed!["programme"]!;
        next["programmeYear"]!.GetValue<int>().ShouldBe(2027);
        next["code"]!.GetValue<string>().ShouldBe(programme["code"]!.GetValue<string>());
        next["renewedFromId"]!.GetValue<string>().ShouldBe(id);
        next["status"]!.GetValue<string>().ShouldBe("DRAFT");
        next["period"]!["from"]!.GetValue<string>().ShouldBe("2027-01-01");
        next["period"]!["to"]!.GetValue<string>().ShouldBe("2028-01-01");
        var layers = next["layers"]!.AsArray();
        layers.Select(l => l!["limit"]!["amount"]!.GetValue<string>()).ShouldBe(["500000.00", "1000000.00"]);
        layers.Select(l => l!["attachment"]!["amount"]!.GetValue<string>()).ShouldBe(["500000.00", "1000000.00"]);
        layers.All(l => l!["contractStatus"]!.GetValue<string>() == "DRAFT").ShouldBeTrue();
        var newContracts = next["contracts"]!.AsArray().Select(c => c!["contractId"]!.GetValue<string>()).ToArray();
        newContracts.ShouldNotContain(a);
        newContracts.ShouldNotContain(b);
        next["contracts"]!.AsArray().All(c => c!["contractYear"]!.GetValue<int>() == 2027).ShouldBeTrue();

        // The copied treaty keeps the stable treaty id, the period moves a year, the panel and layer are copied.
        var stableOld = await slice.ScalarAsync<string>($"SELECT stable_treaty_id FROM ri.contract WHERE contract_id = '{a}'");
        var stableNew = await slice.ScalarAsync<string>($"SELECT stable_treaty_id FROM ri.contract WHERE contract_id = '{newContracts[0]}'");
        stableNew.ShouldBe(stableOld);
        (await slice.ScalarAsync<long>($"SELECT count(*) FROM ri.participation p JOIN ri.contract_version v ON v.version_id = p.version_id WHERE v.contract_id = '{newContracts[0]}'")).ShouldBe(2);
        (await slice.ScalarAsync<long>($"SELECT count(*) FROM ri.layer l JOIN ri.contract_version v ON v.version_id = l.version_id WHERE v.contract_id = '{newContracts[1]}'")).ShouldBe(1);

        // The renewal is a Draft the renewer made: it is not approved by the same person, and the old programme is untouched.
        (await slice.SendAsync(HttpMethod.Get, $"{Url}/{id}")).Body!["programme"]!["versionNo"]!.GetValue<int>().ShouldBe(1);
        var again = await slice.SendAsync(HttpMethod.Post, $"{Url}/renew", new { programmeId = id });
        again.Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, again.Body?.ToJsonString());
        again.Body!.ToJsonString().ShouldContain("PROGRAMME_YEAR_EXISTS");
        (await slice.ScalarAsync<long>($"SELECT count(*) FROM ri.contract WHERE stable_treaty_id = '{stableOld}'")).ShouldBe(2);
        (await slice.SendAsync(HttpMethod.Post, $"{Url}/renew", new { programmeId = Guid.NewGuid() })).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Another_legal_entitys_programme_is_not_found_here()
    {
        await using var slice = new RegistrySlice(database);
        var line = NewProduct();
        var (a, b) = await TreatiesAsync(slice, line);
        var (programme, _) = await CreateAsync(slice, ProgrammeBody(line, a, b));
        var foreign = Guid.CreateVersion7();
        var foreignVersion = Guid.CreateVersion7();
        await slice.ExecuteAsync(
            $"INSERT INTO ri.programme (programme_id, legal_entity_id, jurisdiction, code, line_code, programme_year, currency, current_version_no, participants, created_at, created_by, updated_at, record_version) " +
            $"VALUES ('{foreign}', '{Guid.NewGuid()}', 'GR', 'FOREIGN-PRG', 'OTHER', 2026, 'EUR', 1, ARRAY['USER:x'], now(), 'x', now(), 1); " +
            $"INSERT INTO ri.programme_version (version_id, programme_id, version_no, status, name, valid_from, valid_to, created_at, created_by) " +
            $"VALUES ('{foreignVersion}', '{foreign}', 1, 'DRAFT', 'Foreign', '2026-01-01', '2027-01-01', now(), 'x');");

        (await slice.SendAsync(HttpMethod.Get, $"{Url}/{foreign}")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await slice.SendAsync(HttpMethod.Patch, $"{Url}/{foreign}", new { expectedRecordVersion = 1, name = "Hijack" })).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await slice.SendAsync(HttpMethod.Post, $"{Url}/renew", new { programmeId = foreign })).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var (_, page) = await slice.SendAsync(HttpMethod.Get, $"{Url}?limit=2" + (cursor is null ? string.Empty : $"&cursor={cursor}"));
            seen.AddRange(page!["items"]!.AsArray().Select(i => i!["programme"]!["programmeId"]!.GetValue<string>()));
            cursor = page["nextCursor"]?.GetValue<string>();
        }
        while (cursor is not null);

        seen.ShouldNotContain(foreign.ToString());
        seen.ShouldContain(programme["programmeId"]!.GetValue<string>());
        seen.Distinct().Count().ShouldBe(seen.Count);
    }
}
