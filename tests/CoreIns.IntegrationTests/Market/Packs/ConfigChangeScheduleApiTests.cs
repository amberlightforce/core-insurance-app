using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CoreIns.Modules.Market.Services;
using CoreIns.Platform.Time;
using Microsoft.Extensions.DependencyInjection;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Market.Packs;

/// <summary>BO-MKT-02: schedule (applied by the sweeper at the effective instant), rebase, preview and import of configuration change requests.</summary>
public sealed class ConfigChangeScheduleApiTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Path = "/api/mkt/v1/config-change-requests";
    private ApiHostFactory _factory = null!;
    private HttpClient _client = null!;
    private ShiftableClock _clock = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _factory = new ApiHostFactory(database.AppConnectionString, settings: new Dictionary<string, string?> { [ClockConfiguration.ModeKey] = "Shiftable" });
        _client = _factory.CreateClient();
        _clock = (ShiftableClock)_factory.Services.GetRequiredService<IClock>();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private async Task<(HttpStatusCode Status, JsonNode Body)> SendAsync(HttpMethod method, string path, string user, string role, object? body = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add(TestAuthHandler.UserHeader, user);
        request.Headers.Add(TestAuthHandler.RolesHeader, role);
        if (method != HttpMethod.Get)
        {
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var response = await _client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response.StatusCode, string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text)!);
    }

    private Task<(HttpStatusCode Status, JsonNode Body)> MakerAsync(string path, object body) => SendAsync(HttpMethod.Post, Path + path, "cfg-maker", "Platform.ReleaseManager", body);

    private Task<(HttpStatusCode Status, JsonNode Body)> CheckerAsync(string path, object body) => SendAsync(HttpMethod.Post, Path + path, "cfg-checker", "Platform.DesignAuthority", body);

    private static object Item(string key, string value, string legalStatus = "Settled") => new
    {
        key, value, legalStatus, sourceRef = "Test change, BO-MKT-02", motorPath = true, validFrom = "2027-01-01", validTo = (string?)null,
    };

    private async Task<string> CreateAsync(params object[] items)
    {
        var (created, body) = await MakerAsync(string.Empty, new { reason = "Change a premium tax rate from 2027", items });
        created.ShouldBe(HttpStatusCode.OK, body.ToJsonString());
        return body.Text("request.id");
    }

    private async Task SubmitAsync(string id)
    {
        var (submitted, body) = await MakerAsync("/submit", new { requestId = id });
        submitted.ShouldBe(HttpStatusCode.OK, body.ToJsonString());
    }

    private async Task<JsonNode> ApproveAsync(string id)
    {
        var (approved, body) = await CheckerAsync("/approve", new { requestId = id, reason = "Independent checker confirms" });
        approved.ShouldBe(HttpStatusCode.OK, body.ToJsonString());
        return body;
    }

    private string Later(TimeSpan by) => _clock.Now.ToDateTimeOffset().Add(by).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private async Task<string> CurrentHashAsync()
    {
        await Task.Delay(TimeSpan.FromMilliseconds(1100), Ct);
        var (_, current) = await SendAsync(HttpMethod.Get, "/api/mkt/v1/configuration/current-hash", "reader", "Staff.Underwriter");
        return current.Text("hash");
    }

    private async Task<string> StatusAsync(string id)
    {
        var (_, body) = await SendAsync(HttpMethod.Get, $"{Path}/{id}", "rm", "Platform.ReleaseManager");
        return body.Text("status");
    }

    [Fact]
    public async Task A_scheduled_change_waits_for_its_effective_instant_and_the_sweeper_applies_it()
    {
        var before = await CurrentHashAsync();
        var id = await CreateAsync(Item("tax.ipt.rate.general", "0.16"));
        var effective = Later(TimeSpan.FromDays(1));

        (await MakerAsync("/schedule", new { requestId = id, effectiveAt = Later(TimeSpan.FromHours(-1)) })).Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var (otherUser, _) = await SendAsync(HttpMethod.Post, Path + "/schedule", "someone-else", "Platform.ReleaseManager", new { requestId = id, effectiveAt = effective });
        otherUser.ShouldBe(HttpStatusCode.Forbidden);
        var (scheduled, draft) = await MakerAsync("/schedule", new { requestId = id, effectiveAt = effective });
        scheduled.ShouldBe(HttpStatusCode.OK, draft.ToJsonString());
        draft.Text("request.status").ShouldBe("DRAFT");
        draft.Text("request.effectiveAt").ShouldNotBeNullOrEmpty();

        await SubmitAsync(id);

        // The effective instant is part of what was approved: it cannot be set or moved once approval was asked for.
        (await MakerAsync("/schedule", new { requestId = id, effectiveAt = Later(TimeSpan.FromDays(3)) })).Status.ShouldBe(HttpStatusCode.Conflict);

        var approved = await ApproveAsync(id);
        approved.Text("request.status").ShouldBe("SCHEDULED");
        (await CurrentHashAsync()).ShouldBe(before, "nothing is applied before the effective instant");

        var runner = _factory.Services.GetRequiredService<ConfigChangeSweepRunner>();
        (await runner.RunAsync(Ct)).ShouldBe(0);
        (await StatusAsync(id)).ShouldBe("SCHEDULED");

        _clock.Advance(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(5));
        (await runner.RunAsync(Ct)).ShouldBe(1);
        (await StatusAsync(id)).ShouldBe("APPLIED");
        var (_, applied) = await SendAsync(HttpMethod.Get, $"{Path}/{id}", "rm", "Platform.ReleaseManager");
        var after = await CurrentHashAsync();
        after.ShouldBe(applied.Text("resultingHash"));
        after.ShouldNotBe(before);
        (await runner.RunAsync(Ct)).ShouldBe(0, "a second pass finds nothing due");
    }

    [Fact]
    public async Task A_scheduled_change_that_went_stale_before_its_date_is_closed_instead_of_applied()
    {
        var first = await CreateAsync(Item("tax.ipt.rate.fire", "0.2"));
        var second = await CreateAsync(Item("tax.ipt.rate.fire", "0.21"));
        (await MakerAsync("/schedule", new { requestId = first, effectiveAt = Later(TimeSpan.FromDays(1)) })).Status.ShouldBe(HttpStatusCode.OK);
        await SubmitAsync(first);
        await SubmitAsync(second);
        (await ApproveAsync(first)).Text("request.status").ShouldBe("SCHEDULED");
        (await ApproveAsync(second)).Text("request.status").ShouldBe("APPLIED");

        _clock.Advance(TimeSpan.FromDays(2));
        var runner = _factory.Services.GetRequiredService<ConfigChangeSweepRunner>();
        (await runner.RunAsync(Ct)).ShouldBe(0);
        var (_, closed) = await SendAsync(HttpMethod.Get, $"{Path}/{first}", "rm", "Platform.ReleaseManager");
        closed.Text("status").ShouldBe("REJECTED");
        closed.Text("decisionReason").ShouldContain("changed since the request was written");
    }

    [Fact]
    public async Task Two_requests_on_the_same_key_conflict_and_the_second_is_rebased_previewed_and_applied()
    {
        var first = await CreateAsync(Item("tax.ipt.rate.general", "0.17"));
        var second = await CreateAsync(Item("tax.ipt.rate.general", "0.18"));
        await SubmitAsync(first);
        await SubmitAsync(second);
        (await ApproveAsync(first)).Text("request.status").ShouldBe("APPLIED");
        (await CheckerAsync("/approve", new { requestId = second, reason = "Stale against the first change" })).Status.ShouldBe(HttpStatusCode.Conflict);

        var (previewStatus, preview) = await MakerAsync("/preview", new { requestId = second });
        previewStatus.ShouldBe(HttpStatusCode.OK, preview.ToJsonString());
        var run = preview["previewRun"]!;
        run.Text("stale").ShouldBe("true");
        run["conflicts"]!.AsArray().Single()!.Text("key").ShouldBe("tax.ipt.rate.general");
        run["resultingHash"].ShouldBeNull("a stale request has no resulting hash");

        (await CheckerAsync("/rebase", new { requestId = second, reason = "Not the maker" })).Status.ShouldBe(HttpStatusCode.Forbidden);
        var (rebased, body) = await MakerAsync("/rebase", new { requestId = second, reason = "Rebase on the newer state" });
        rebased.ShouldBe(HttpStatusCode.OK, body.ToJsonString());
        body.Text("request.status").ShouldBe("DRAFT");
        body["conflicts"]!.AsArray().Single()!.Text("currentValue").ShouldBe("0.17");
        body.Text("request.baseHash").ShouldNotBe(body.Text("previousBaseHash"));

        var hashBefore = await CurrentHashAsync();
        var (_, fresh) = await MakerAsync("/preview", new { requestId = second });
        fresh["previewRun"]!.Text("stale").ShouldBe("false");
        var item = fresh["previewRun"]!["items"]!.AsArray().Single()!;
        item.Text("before").ShouldBe("0.17");
        item.Text("after").ShouldBe("0.18");
        var predicted = fresh["previewRun"]!.Text("resultingHash");
        (await CurrentHashAsync()).ShouldBe(hashBefore, "preview writes nothing");

        await SubmitAsync(second);
        var applied = await ApproveAsync(second);
        applied.Text("request.status").ShouldBe("APPLIED");
        applied.Text("request.resultingHash").ShouldBe(predicted, "the preview names the hash the application gives");
    }

    [Fact]
    public async Task A_pending_request_can_be_rebased_and_its_approval_is_withdrawn()
    {
        var first = await CreateAsync(Item("tax.ipt.rate.general", "0.17"));
        var second = await CreateAsync(Item("tax.ipt.rate.fire", "0.3"));
        await SubmitAsync(first);
        await SubmitAsync(second);
        await ApproveAsync(first);

        var (status, body) = await MakerAsync("/rebase", new { requestId = second, reason = "Pick up the newest state" });
        status.ShouldBe(HttpStatusCode.OK, body.ToJsonString());
        body.Text("request.status").ShouldBe("DRAFT");
        body["conflicts"]!.AsArray().Count.ShouldBe(0, "a different key does not conflict");
        (await CheckerAsync("/approve", new { requestId = second, reason = "The approval was withdrawn" })).Status.ShouldBe(HttpStatusCode.Conflict);
        await SubmitAsync(second);
        (await ApproveAsync(second)).Text("request.status").ShouldBe("APPLIED");
    }

    [Fact]
    public async Task Import_reports_every_bad_key_creates_a_draft_and_is_idempotent()
    {
        var bad = await MakerAsync("/import", new
        {
            reason = "Import the baseline", sourceLabel = "baseline-h0.json",
            items = new[] { Item("tax.made.up", "1"), Item("tax.ipt.rate.fire", "not-a-number"), Item("tax.ipt.rate.general", "0.16") },
        });
        bad.Status.ShouldBe(HttpStatusCode.OK, bad.Body.ToJsonString());
        var report = bad.Body["validationReport"]!;
        report.Text("valid").ShouldBe("false");
        report["issues"]!.AsArray().Select(i => i!.Text("line")).ShouldBe(["1", "2"]);
        report["request"].ShouldBeNull();

        var payload = new
        {
            reason = "Import the baseline", sourceLabel = "baseline-h0.json",
            items = new[] { Item("tax.ipt.rate.general", "0.16"), Item("tax.ipt.rate.fire", "0.2") },
        };
        var (created, first) = await MakerAsync("/import", payload);
        created.ShouldBe(HttpStatusCode.OK, first.ToJsonString());
        var request = first["validationReport"]!["request"]!;
        request.Text("status").ShouldBe("DRAFT");
        request.Text("cause").ShouldBe("IMPORT");
        first["validationReport"]!.Text("duplicate").ShouldBe("false");

        var (_, again) = await MakerAsync("/import", payload);
        again["validationReport"]!.Text("duplicate").ShouldBe("true");
        again["validationReport"]!["request"]!.Text("id").ShouldBe(request.Text("id"));

        var (_, list) = await SendAsync(HttpMethod.Get, Path + "?status=DRAFT", "rm", "Platform.ReleaseManager");
        list["items"]!.AsArray().Count(i => i!.Text("cause") == "IMPORT").ShouldBe(1);

        // An import never applies by itself: it goes through submit and a different checker.
        await SubmitAsync(request.Text("id"));
        (await ApproveAsync(request.Text("id"))).Text("request.status").ShouldBe("APPLIED");
    }
}
