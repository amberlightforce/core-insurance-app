using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Market.Packs;

/// <summary>Native HTTP approvals and outbox effects; no successful approval doubles.</summary>
public sealed class PackActivationApiTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private ApiHostFactory _factory = null!;
    private HttpClient _client = null!;
    public ValueTask InitializeAsync()
    {
        _factory = new ApiHostFactory(database.AppConnectionString);
        _client = _factory.CreateClient();
        return ValueTask.CompletedTask;
    }
    public async ValueTask DisposeAsync() { _client.Dispose(); await _factory.DisposeAsync(); }
    private async Task<(HttpStatusCode Status, JsonNode Body)> SendAsync(string path, string user, string role, object body, Guid? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative));
        request.Headers.Add(TestAuthHandler.UserHeader, user);
        request.Headers.Add(TestAuthHandler.RolesHeader, role);
        request.Headers.Add("Idempotency-Key", (key ?? Guid.NewGuid()).ToString());
        request.Content = JsonContent.Create(body);
        using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        return (response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!);
    }

    [Fact]
    public async Task REQ_MKT_137_138_native_rollback_refuses_self_approval_and_freezes_history_then_restores_pack()
    {
        var body = new { pack = "gr", legalEntity = "GR-TEST", toVersion = "0.1.0", reason = "Restore the previously published pack after defective treatment release" };
        var (previewStatus, preview) = await SendAsync("/api/mkt/v1/packs/rollback?dryRun=true", "pack-maker", "Platform.ReleaseManager", body);
        previewStatus.ShouldBe(HttpStatusCode.OK, preview.ToJsonString());
        preview["activationId"].ShouldBeNull();
        preview.Text("preview.fromVersion").ShouldBe("0.2.0");
        preview["preview"]!["keyDiff"]!.AsArray().Count.ShouldBeGreaterThan(0);

        var key = Guid.NewGuid();
        var (created, pending) = await SendAsync("/api/mkt/v1/packs/rollback", "pack-maker", "Platform.ReleaseManager", body, key);
        created.ShouldBe(HttpStatusCode.OK, pending.ToJsonString());
        var id = pending.Text("activationId");
        var (replayed, replay) = await SendAsync("/api/mkt/v1/packs/rollback", "pack-maker", "Platform.ReleaseManager", body, key);
        replayed.ShouldBe(HttpStatusCode.OK); replay.Text("activationId").ShouldBe(id);

        var decide = new { activationId = id, decision = "APPROVE", reason = "Independent checker confirms rollback to the published previous pack" };
        var (self, _) = await SendAsync("/api/mkt/v1/pack-activations/decide", "pack-maker", "Platform.DesignAuthority", decide);
        self.ShouldBe(HttpStatusCode.Forbidden);
        var (approved, applied) = await SendAsync("/api/mkt/v1/pack-activations/decide", "pack-checker", "Platform.DesignAuthority", decide);
        approved.ShouldBe(HttpStatusCode.OK, applied.ToJsonString());
        applied.Text("activation.status").ShouldBe("ACTIVE");
        applied.Text("activation.reason").ShouldBe(body.reason);
        applied.Text("activation.from").ShouldBe("0.2.0");
        applied.Text("activation.resultingHash").Length.ShouldBe(64);
        var (twice, _) = await SendAsync("/api/mkt/v1/pack-activations/decide", "pack-checker", "Platform.DesignAuthority", decide);
        twice.ShouldBe(HttpStatusCode.Conflict);

        var (restore, restoration) = await SendAsync("/api/mkt/v1/packs/schedule-activation", "pack-maker", "Platform.ReleaseManager",
            new { pack = "gr", legalEntity = "GR-TEST", version = "0.2.0", reason = "Reactivate the treatment pack after rollback acceptance completed" });
        restore.ShouldBe(HttpStatusCode.OK, restoration.ToJsonString());
        var (restored, restoredBody) = await SendAsync("/api/mkt/v1/pack-activations/decide", "pack-checker", "Platform.DesignAuthority",
            new { activationId = restoration.Text("activationId"), decision = "APPROVE", reason = "Restore the treatment configuration after independent validation" });
        restored.ShouldBe(HttpStatusCode.OK, restoredBody.ToJsonString());
        restoredBody.Text("activation.resultingHash").ShouldNotBe(applied.Text("activation.resultingHash"));

        var (_, registry) = await CoreIns.IntegrationTests.Party.PartyApi.SendAsync(_client, HttpMethod.Get, "/api/mkt/v1/packs/gr", roles: "Platform.ReleaseManager");
        var history = registry!["activationHistory"]!.AsArray().Single(a => a!.Text("activationId") == id)!;
        history.Text("status").ShouldBe("SUPERSEDED");
        history.Text("reason").ShouldBe(body.reason);
        history.Text("resultingHash").ShouldBe(applied.Text("activation.resultingHash"));
    }

    [Fact]
    public async Task Activation_refuses_another_entity_unpublished_target_and_non_release_manager()
    {
        var (other, _) = await SendAsync("/api/mkt/v1/packs/rollback", "maker", "Platform.ReleaseManager",
            new { pack = "gr", legalEntity = "OTHER", toVersion = "0.1.0", reason = "Cross entity activation must fail closed without affecting the manifest" });
        other.ShouldBe(HttpStatusCode.NotFound);
        var (missing, _) = await SendAsync("/api/mkt/v1/packs/rollback", "maker", "Platform.ReleaseManager",
            new { pack = "gr", legalEntity = "GR-TEST", toVersion = "99.0.0", reason = "An unpublished version must never be activated by a release request" });
        missing.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var (role, _) = await SendAsync("/api/mkt/v1/packs/rollback", "maker", "Staff.Underwriter",
            new { pack = "gr", legalEntity = "GR-TEST", toVersion = "0.1.0", reason = "A user without release manager role must not create this activation" });
        role.ShouldBe(HttpStatusCode.Forbidden);
    }
}
