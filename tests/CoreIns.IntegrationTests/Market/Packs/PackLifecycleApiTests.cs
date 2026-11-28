using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CoreIns.Modules.Market.Contracts.Spi;
using CoreIns.Modules.Market.Domain;
using CoreIns.SharedKernel;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Market.Packs;

/// <summary>BO-MKT-04: pack certify, publish and deprecate (lifecycle order, Production fail-closed on unverified and synthetic packs, in-use refusal).</summary>
public sealed class PackLifecycleApiTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Reason = "Pack lifecycle step recorded by the integration test";
    private ApiHostFactory _factory = null!;
    private HttpClient _client = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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

    private async Task<(HttpStatusCode Status, JsonNode Body)> PostAsync(string path, string role, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative));
        request.Headers.Add(TestAuthHandler.UserHeader, "pack-manager");
        request.Headers.Add(TestAuthHandler.RolesHeader, role);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Content = JsonContent.Create(body);
        using var response = await _client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response.StatusCode, string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text)!);
    }

    private Task<(HttpStatusCode Status, JsonNode Body)> CertifyAsync(string pack, string version, string target, bool allowUnverified = false, int passed = 12) =>
        PostAsync("/api/mkt/v1/packs/certify", "Platform.DesignAuthority", new
        {
            pack, version, targetEnvironment = target, allowUnverified, reason = Reason,
            goldenSuite = new { suite = "gr-golden", total = 12, passed, failedCases = passed == 12 ? Array.Empty<string>() : ["case-7"] },
        });

    private Task<(HttpStatusCode Status, JsonNode Body)> PublishAsync(string pack, string version, string target) =>
        PostAsync("/api/mkt/v1/packs/publish", "Platform.ReleaseManager", new { pack, version, targetEnvironment = target, reason = Reason });

    private Task<(HttpStatusCode Status, JsonNode Body)> DeprecateAsync(string pack, string version) =>
        PostAsync("/api/mkt/v1/packs/deprecate", "Platform.ReleaseManager", new { pack, version, reason = Reason });

    /// <summary>Registers a Built version directly (shipped versions are born Published; BUILD is not an API step).</summary>
    private async Task RegisterAsync(string pack, string version, params (string Key, LegalStatus Status, string Source)[] rows)
    {
        var values = rows.Select(r => new PackConfigValue(r.Key, ConfigValueType.WholeNumber, "1", r.Status, r.Source, false)).ToList();
        await using var connection = new NpgsqlConnection(database.AppConnectionString);
        await connection.OpenAsync(Ct);
        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO mkt.pack_version (pack_id, version, country, content_digest, "values", status, registered_at)
            VALUES (@pack, @version, 'GR', @digest, @values::jsonb, 'Built', now())
            """, connection);
        insert.Parameters.AddWithValue("pack", pack);
        insert.Parameters.AddWithValue("version", version);
        insert.Parameters.AddWithValue("digest", PackVersionContent.DigestOf(values).ToString());
        insert.Parameters.AddWithValue("values", PackVersionContent.ToJson(values).ToJsonString());
        await insert.ExecuteNonQueryAsync(Ct);
    }

    private async Task<string> StatusOfAsync(string pack, string version)
    {
        var (_, body) = await SendAsync(_client, HttpMethod.Get, $"/api/mkt/v1/packs/{pack}", roles: "Platform.ReleaseManager");
        return body!["versions"]!.AsArray().Single(v => v!.Text("version") == version)!.Text("status");
    }

    [Fact]
    public async Task Lifecycle_runs_in_order_and_refuses_skipped_or_repeated_steps()
    {
        await RegisterAsync("gr", "7.0.0", ("mkt.test.alpha", LegalStatus.Settled, "Test"), ("mkt.test.beta", LegalStatus.NotRegulatory, "Test"));
        (await StatusOfAsync("gr", "7.0.0")).ShouldBe("BUILT");

        var (skipped, _) = await PublishAsync("gr", "7.0.0", "PRODUCTION");
        skipped.ShouldBe(HttpStatusCode.Conflict);
        var (earlyDeprecate, _) = await DeprecateAsync("gr", "7.0.0");
        earlyDeprecate.ShouldBe(HttpStatusCode.Conflict);

        var (role, _) = await PostAsync("/api/mkt/v1/packs/certify", "Platform.ReleaseManager", new { pack = "gr", version = "7.0.0" });
        role.ShouldBe(HttpStatusCode.Forbidden);

        var (certified, cert) = await CertifyAsync("gr", "7.0.0", "PRODUCTION");
        certified.ShouldBe(HttpStatusCode.OK, cert.ToJsonString());
        cert.Text("status").ShouldBe("CERTIFIED");
        cert["productionEligible"]!.GetValue<bool>().ShouldBeTrue();
        cert["unverifiedKeys"]!.AsArray().Count.ShouldBe(0);
        cert.Text("goldenSuite.suite").ShouldBe("gr-golden");
        (await StatusOfAsync("gr", "7.0.0")).ShouldBe("CERTIFIED");

        var (again, _) = await CertifyAsync("gr", "7.0.0", "PRODUCTION");
        again.ShouldBe(HttpStatusCode.Conflict);

        var (published, pub) = await PublishAsync("gr", "7.0.0", "PRODUCTION");
        published.ShouldBe(HttpStatusCode.OK, pub.ToJsonString());
        pub.Text("status").ShouldBe("PUBLISHED");
        pub.Text("publishedAt").ShouldNotBeNullOrEmpty();
        var (twice, _) = await PublishAsync("gr", "7.0.0", "PRODUCTION");
        twice.ShouldBe(HttpStatusCode.Conflict);

        var (deprecated, dep) = await DeprecateAsync("gr", "7.0.0");
        deprecated.ShouldBe(HttpStatusCode.OK, dep.ToJsonString());
        dep.Text("status").ShouldBe("DEPRECATED");
        (await StatusOfAsync("gr", "7.0.0")).ShouldBe("DEPRECATED");
        var (backwards, _) = await CertifyAsync("gr", "7.0.0", "PRODUCTION");
        backwards.ShouldBe(HttpStatusCode.Conflict);

        var (unknown, _) = await CertifyAsync("gr", "99.0.0", "PRODUCTION");
        unknown.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Unverified_values_never_certify_or_publish_for_production_without_the_opt_in_and_never_then()
    {
        await RegisterAsync("gr", "7.1.0", ("mkt.test.alpha", LegalStatus.Settled, "Test"), ("mkt.test.gamma", LegalStatus.Unverified, "Test"));

        var (production, refused) = await CertifyAsync("gr", "7.1.0", "PRODUCTION", allowUnverified: true);
        production.ShouldBe(HttpStatusCode.UnprocessableEntity, refused.ToJsonString());
        refused.ToJsonString().ShouldContain("PACK-NOT-SETTLED", Case.Insensitive);

        var (noOptIn, _) = await CertifyAsync("gr", "7.1.0", "NON_PRODUCTION");
        noOptIn.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await StatusOfAsync("gr", "7.1.0")).ShouldBe("BUILT");

        var (optIn, cert) = await CertifyAsync("gr", "7.1.0", "NON_PRODUCTION", allowUnverified: true);
        optIn.ShouldBe(HttpStatusCode.OK, cert.ToJsonString());
        cert.Text("status").ShouldBe("CERTIFIED");
        cert["productionEligible"]!.GetValue<bool>().ShouldBeFalse();
        cert["unverifiedKeys"]!.AsArray().Single()!.GetValue<string>().ShouldBe("mkt.test.gamma");

        var (toProduction, blocked) = await PublishAsync("gr", "7.1.0", "PRODUCTION");
        toProduction.ShouldBe(HttpStatusCode.UnprocessableEntity, blocked.ToJsonString());
        (await StatusOfAsync("gr", "7.1.0")).ShouldBe("CERTIFIED");

        var (toTest, pub) = await PublishAsync("gr", "7.1.0", "NON_PRODUCTION");
        toTest.ShouldBe(HttpStatusCode.OK, pub.ToJsonString());
        pub["productionEligible"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public async Task The_synthetic_cy_pack_cannot_be_certified_or_published_for_production()
    {
        await RegisterAsync("cy-stub", "0.9.0", ("mkt.test.cy", LegalStatus.Draft, "SYNTHETIC cy-stub row"));

        var (certify, refused) = await CertifyAsync("cy-stub", "0.9.0", "PRODUCTION", allowUnverified: true);
        certify.ShouldBe(HttpStatusCode.UnprocessableEntity);
        refused.ToJsonString().ShouldContain("PACK-SYNTHETIC", Case.Insensitive);

        var (ci, _) = await CertifyAsync("cy-stub", "0.9.0", "NON_PRODUCTION", allowUnverified: true);
        ci.ShouldBe(HttpStatusCode.OK);
        var (publish, blocked) = await PublishAsync("cy-stub", "0.9.0", "PRODUCTION");
        publish.ShouldBe(HttpStatusCode.UnprocessableEntity);
        blocked.ToJsonString().ShouldContain("PACK-SYNTHETIC", Case.Insensitive);
        var (ok, _) = await PublishAsync("cy-stub", "0.9.0", "NON_PRODUCTION");
        ok.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_failing_golden_suite_rejects_the_version_and_it_can_be_certified_again()
    {
        await RegisterAsync("gr", "7.2.0", ("mkt.test.alpha", LegalStatus.Settled, "Test"));

        var (failed, body) = await CertifyAsync("gr", "7.2.0", "PRODUCTION", passed: 11);
        failed.ShouldBe(HttpStatusCode.OK, body.ToJsonString());
        body.Text("status").ShouldBe("REJECTED");
        body["productionEligible"]!.GetValue<bool>().ShouldBeFalse();
        (await PublishAsync("gr", "7.2.0", "PRODUCTION")).Status.ShouldBe(HttpStatusCode.Conflict);

        var (fixedUp, cert) = await CertifyAsync("gr", "7.2.0", "PRODUCTION");
        fixedUp.ShouldBe(HttpStatusCode.OK, cert.ToJsonString());
        cert.Text("status").ShouldBe("CERTIFIED");
    }

    [Fact]
    public async Task Deprecating_the_active_pack_is_refused_and_a_deprecated_version_cannot_be_activated()
    {
        var (active, refused) = await DeprecateAsync("gr", "0.3.0");
        active.ShouldBe(HttpStatusCode.Conflict, refused.ToJsonString());
        refused.ToJsonString().ShouldContain("PACK-IN-USE", Case.Insensitive);
        (await StatusOfAsync("gr", "0.3.0")).ShouldBe("PUBLISHED");

        var (older, dep) = await DeprecateAsync("gr", "0.1.0");
        older.ShouldBe(HttpStatusCode.OK, dep.ToJsonString());
        var (rollback, _) = await PostAsync("/api/mkt/v1/packs/rollback", "Platform.ReleaseManager",
            new { pack = "gr", legalEntity = "GR-TEST", toVersion = "0.1.0", reason = "Rolling back to a deprecated version must fail closed here" });
        rollback.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }
}
