using System.Net;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Market.State;

/// <summary>
/// <c>mkt.Pack.list</c> and <c>mkt.Pack.get</c> over HTTP (REQ-MKT-003 registry subset): the registered packs, their versions with content digests and
/// the activations, for the release manager, the design authority and the platform admin only (illustrative grants, SL5-MKT-STATE).
/// </summary>
public sealed class PackRegistryApiTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
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

    [Theory]
    [InlineData("Platform.ReleaseManager")]
    [InlineData("Platform.DesignAuthority")]
    [InlineData("Platform.Admin")]
    public async Task REQ_MKT_003_list_shows_the_gr_pack_with_both_versions_their_digests_and_the_genesis_activation(string role)
    {
        var (response, body) = await SendAsync(_client, HttpMethod.Get, "/api/mkt/v1/packs", roles: role);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body!["items"]!.AsArray().Count.ShouldBe(1);
        var pack = body["items"]![0]!;
        pack["pack"]!.GetValue<string>().ShouldBe("gr");
        pack["scope"]!.GetValue<string>().ShouldBe("COUNTRY");
        pack["packId"]!.GetValue<string>().Length.ShouldBe(36);
        pack["versions"]!.AsArray().Select(v => v!["version"]!.GetValue<string>()).ShouldBe(["0.1.0", "0.2.0"]);
        pack["versions"]!.AsArray().ShouldAllBe(v => v!["contentDigest"]!.GetValue<string>().Length == 64 && v["status"]!.GetValue<string>() == "PUBLISHED");
        var active = pack["activeVersions"]!.AsArray().Single()!;
        active["legalEntity"]!.GetValue<string>().ShouldBe("GR-TEST");
        active["version"]!.GetValue<string>().ShouldBe("0.2.0");
        active["configurationHash"]!.GetValue<string>().Length.ShouldBe(64);
        body["nextCursor"].ShouldBeNull();
        body.ToJsonString().ShouldNotContain("\"pack\":\"core\"", Case.Sensitive, "the core defaults are not a pack");
    }

    [Fact]
    public async Task REQ_MKT_003_get_returns_one_pack_and_an_unknown_pack_or_the_core_defaults_are_404()
    {
        var (found, body) = await SendAsync(_client, HttpMethod.Get, "/api/mkt/v1/packs/gr", roles: "Platform.ReleaseManager");
        var (missing, problem) = await SendAsync(_client, HttpMethod.Get, "/api/mkt/v1/packs/nope", roles: "Platform.ReleaseManager");
        var (core, _) = await SendAsync(_client, HttpMethod.Get, "/api/mkt/v1/packs/core", roles: "Platform.ReleaseManager");

        found.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body!["pack"]!.GetValue<string>().ShouldBe("gr");
        var history = body["activationHistory"]!.AsArray().Single()!;
        history["kind"]!.GetValue<string>().ShouldBe("ACTIVATE");
        history["status"]!.GetValue<string>().ShouldBe("ACTIVE");
        history["to"]!.GetValue<string>().ShouldBe("0.2.0");
        history["requestedBy"]!.GetValue<string>().ShouldBe("system:genesis");
        var (byId, byIdBody) = await SendAsync(_client, HttpMethod.Get, "/api/mkt/v1/packs/" + body["packId"]!.GetValue<string>(), roles: "Platform.DesignAuthority");
        byId.StatusCode.ShouldBe(HttpStatusCode.OK);
        byIdBody!["pack"]!.GetValue<string>().ShouldBe("gr");
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        problem.Text("code").ShouldBe("MKT-ERR-PACK-NOT-FOUND");
        core.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("Staff.Underwriter")]
    [InlineData("Staff.Billing")]
    [InlineData("Staff.Claims")]
    public async Task The_pack_registry_is_closed_to_every_other_role(string role)
    {
        var (list, _) = await SendAsync(_client, HttpMethod.Get, "/api/mkt/v1/packs", roles: role);
        var (get, _) = await SendAsync(_client, HttpMethod.Get, "/api/mkt/v1/packs/gr", roles: role);
        var (anonymous, _) = await SendAsync(_client, HttpMethod.Get, "/api/mkt/v1/packs", roles: "");

        list.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        get.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        anonymous.StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }
}
