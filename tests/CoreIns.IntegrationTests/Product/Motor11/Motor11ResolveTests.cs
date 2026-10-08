using System.Net;
using System.Text.Json.Nodes;
using CoreIns.Modules.Product;
using static CoreIns.IntegrationTests.Party.PartyApi;
using static CoreIns.IntegrationTests.Product.ProductApi;

namespace CoreIns.IntegrationTests.Product.Motor11;

/// <summary>MOTOR-GR 1.0 and 1.1 imported side by side on a real PostgreSQL: resolution by date and by hash (D-SL3-04).</summary>
public sealed class Motor11ResolveTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private ApiHostFactory _factory = null!;
    private HttpClient _client = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new ApiHostFactory(database.AppConnectionString);
        _client = _factory.CreateClient();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private static JsonObject Definition(string json, string code)
    {
        var definition = JsonNode.Parse(json)!.AsObject();
        definition["product"]!["code"] = code;
        return definition;
    }

    [Fact]
    public async Task REQ_PFC_167_resolution_on_2026_10_08_picks_1_1_and_a_term_pinned_to_1_0_resolves_its_own_artefact_by_hash()
    {
        const string code = "MOTOR-M11-A";
        var (_, imported10) = await ImportAsync(_client, Definition(ProductSeeds.MotorPrivateCarJson(), code));
        var (created11, imported11) = await ImportAsync(_client, Definition(ProductSeeds.MotorPrivateCar11Json(), code));
        created11.StatusCode.ShouldBe(HttpStatusCode.Created, imported11?.ToJsonString());
        imported11.Text("version").ShouldBe("1.1");
        imported11.Text("status").ShouldBe("LOCKED");
        var hash10 = imported10.Text("artefactHash");
        var hash11 = imported11.Text("artefactHash");
        hash11.ShouldNotBe(hash10);

        // New business and renewal by term start date both pick 1.1.
        foreach (var type in new[] { "NewBusiness", "Renewal" })
        {
            var (ok, body) = await ResolveAsync(_client, code, "WEB_DIRECT", "2026-10-08", type);
            ok.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
            body.Text("version").ShouldBe("1.1");
            body.Text("artefactHash").ShouldBe(hash11);
        }

        // Before 1.1 opens (2026-10-01) a term still resolves 1.0; publishing 1.1 closed 1.0's new-business window there (REQ-PFC-033).
        var (before, beforeBody) = await ResolveAsync(_client, code, "WEB_DIRECT", "2026-09-30");
        before.StatusCode.ShouldBe(HttpStatusCode.OK, beforeBody?.ToJsonString());
        beforeBody.Text("version").ShouldBe("1.0");
        beforeBody.Text("artefactHash").ShouldBe(hash10);
        var (beforeRenewal, beforeRenewalBody) = await ResolveAsync(_client, code, "WEB_DIRECT", "2026-09-30", "Renewal");
        beforeRenewal.StatusCode.ShouldBe(HttpStatusCode.OK, beforeRenewalBody?.ToJsonString());
        beforeRenewalBody.Text("version").ShouldBe("1.0");

        // The term pinned to 1.0 keeps its own artefact: fetched by hash it is still ACT/365F with no refund methods.
        var (pinned, pinnedBody) = await GetAsync(_client, $"/api/pfc/v1/artifacts/{hash10}");
        pinned.StatusCode.ShouldBe(HttpStatusCode.OK, pinnedBody?.ToJsonString());
        var artefact10 = pinnedBody!["canonicalJsonArtefact"]!;
        artefact10["version"]!.GetValue<string>().ShouldBe("1.0");
        artefact10["dayCount"]!.GetValue<string>().ShouldBe("ACT/365F");
        artefact10["refundMethods"].ShouldBeNull();

        var (current, currentBody) = await GetAsync(_client, $"/api/pfc/v1/artifacts/{hash11}");
        current.StatusCode.ShouldBe(HttpStatusCode.OK, currentBody?.ToJsonString());
        currentBody!["canonicalJsonArtefact"]!["dayCount"]!.GetValue<string>().ShouldBe("TERM_RATIO");
        currentBody["canonicalJsonArtefact"]!["refundMethods"]!.AsArray().Count.ShouldBe(2);
    }

    [Fact]
    public async Task REQ_PFC_193_importing_1_1_again_changes_nothing_and_1_0_alone_still_resolves_to_1_0()
    {
        const string alone = "MOTOR-M11-B";
        await ImportAsync(_client, Definition(ProductSeeds.MotorPrivateCarJson(), alone));
        var (ok, body) = await ResolveAsync(_client, alone, "STAFF", "2026-10-08");
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("version").ShouldBe("1.0");

        const string both = "MOTOR-M11-C";
        var (first, _) = await ImportAsync(_client, Definition(ProductSeeds.MotorPrivateCar11Json(), both));
        var (again, againBody) = await ImportAsync(_client, Definition(ProductSeeds.MotorPrivateCar11Json(), both));
        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        again.StatusCode.ShouldBe(HttpStatusCode.OK, againBody?.ToJsonString());
        againBody.Text("created").ShouldBe("false");
    }
}
