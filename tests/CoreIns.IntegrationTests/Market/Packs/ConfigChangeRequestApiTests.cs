using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Market.Packs;

/// <summary>BO-MKT-01: configuration change requests over HTTP on a real PostgreSQL: maker-checker, key registry, atomic apply, new hash, audit.</summary>
public sealed class ConfigChangeRequestApiTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Path = "/api/mkt/v1/config-change-requests";
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

        using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (response.StatusCode, string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text)!);
    }

    private static object Item(string key, string value, string from = "2027-01-01", string legalStatus = "Settled") => new
    {
        key, value, legalStatus, sourceRef = "Test change, BO-MKT-01", motorPath = true, validFrom = from, validTo = (string?)null,
    };

    private Task<(HttpStatusCode Status, JsonNode Body)> CreateAsync(params object[] items) =>
        SendAsync(HttpMethod.Post, Path, "cfg-maker", "Platform.ReleaseManager", new { reason = "Raise the general premium tax rate from 2027", items });

    private async Task<(string Id, JsonNode Submitted)> CreateAndSubmitAsync(params object[] items)
    {
        var (created, body) = await CreateAsync(items);
        created.ShouldBe(HttpStatusCode.OK, body.ToJsonString());
        var id = body.Text("request.id");
        var (submitted, pending) = await SendAsync(HttpMethod.Post, Path + "/submit", "cfg-maker", "Platform.ReleaseManager", new { requestId = id });
        submitted.ShouldBe(HttpStatusCode.OK, pending.ToJsonString());
        pending.Text("request.status").ShouldBe("PENDING_APPROVAL");
        return (id, pending);
    }

    private async Task<(string Value, string Hash)> ResolveAsync(string key, string validAt)
    {
        // The current state is served from memory for up to one second (REQ-MKT-050).
        await Task.Delay(TimeSpan.FromMilliseconds(1100), TestContext.Current.CancellationToken);
        var (response, body) = await SendAsync(HttpMethod.Post, $"/api/mkt/v1/configuration/resolve?validAt={validAt}", "reader", "Staff.Underwriter",
            new { legalEntity = "GR-TEST", jurisdiction = "GR", keys = new[] { key }, timeBasisDates = new Dictionary<string, string> { ["TAX_POINT_DATE"] = validAt } });
        response.ShouldBe(HttpStatusCode.OK, body.ToJsonString());
        return (body.Text("values.0.value"), body.Text("configurationHash"));
    }

    [Fact]
    public async Task An_approved_request_changes_the_resolved_value_and_the_hash_and_old_dates_keep_the_old_value()
    {
        var (_, hashBefore) = await ResolveAsync("tax.ipt.rate.general", "2027-02-01");
        var (id, _) = await CreateAndSubmitAsync(Item("tax.ipt.rate.general", "0.16"));

        var (selfStatus, _) = await SendAsync(HttpMethod.Post, Path + "/approve", "cfg-maker", "Platform.DesignAuthority",
            new { requestId = id, reason = "Self approval must be refused" });
        selfStatus.ShouldBe(HttpStatusCode.Forbidden);

        var (approved, applied) = await SendAsync(HttpMethod.Post, Path + "/approve", "cfg-checker", "Platform.DesignAuthority",
            new { requestId = id, reason = "Independent checker confirms the source" });
        approved.ShouldBe(HttpStatusCode.OK, applied.ToJsonString());
        applied.Text("request.status").ShouldBe("APPLIED");
        var newHash = applied.Text("request.resultingHash");
        newHash.Length.ShouldBe(64);
        newHash.ShouldNotBe(hashBefore);

        var (after, hashAfter) = await ResolveAsync("tax.ipt.rate.general", "2027-02-01");
        after.ShouldBe("0.16");
        hashAfter.ShouldBe(newHash);
        (await ResolveAsync("tax.ipt.rate.general", "2026-12-31")).Value.ShouldBe("0.15", "the change starts on 2027-01-01");

        var (_, current) = await SendAsync(HttpMethod.Get, "/api/mkt/v1/configuration/current-hash", "reader", "Staff.Underwriter");
        current.Text("hash").ShouldBe(newHash);

        var (twice, _) = await SendAsync(HttpMethod.Post, Path + "/approve", "cfg-checker", "Platform.DesignAuthority",
            new { requestId = id, reason = "A decided request cannot be decided again" });
        twice.ShouldBe(HttpStatusCode.Conflict);

        await using var source = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var audit = source.CreateCommand(
            $"SELECT string_agg(operation || ':' || outcome, ',') FROM plt.audit_event WHERE object_module = 'MKT' AND object_type = 'ConfigChangeRequest' AND object_id = '{id}'");
        var operations = ((string?)await audit.ExecuteScalarAsync(TestContext.Current.CancellationToken) ?? string.Empty).Split(',');

        operations.ShouldContain("mkt.ConfigChangeRequest.create:Succeeded");
        operations.ShouldContain("mkt.ConfigChangeRequest.submit:Succeeded");
        operations.ShouldContain("mkt.ConfigChangeRequest.approve:Succeeded");

        var (_, packs) = await SendAsync(HttpMethod.Get, "/api/mkt/v1/packs", "rm", "Platform.ReleaseManager");
        packs["items"]!.AsArray().Select(p => p!.Text("pack")).ShouldBe(["gr"], "the overlay is not a pack");
    }

    [Fact]
    public async Task Unknown_keys_and_invalid_values_are_refused_and_drafts_can_be_changed_and_withdrawn_by_the_maker_only()
    {
        var (unknown, _) = await CreateAsync(Item("tax.made.up", "1"));
        unknown.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var (badValue, _) = await CreateAsync(Item("tax.ipt.rate.general", "not-a-number"));
        badValue.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var (wrongRole, _) = await SendAsync(HttpMethod.Post, Path, "cfg-maker", "Staff.Underwriter",
            new { reason = "Role without the permission", items = new[] { Item("tax.ipt.rate.general", "0.16") } });
        wrongRole.ShouldBe(HttpStatusCode.Forbidden);

        var (created, body) = await CreateAsync(Item("tax.ipt.rate.general", "0.17"));
        created.ShouldBe(HttpStatusCode.OK, body.ToJsonString());
        var id = body.Text("request.id");
        body.Text("request.status").ShouldBe("DRAFT");

        var (patched, updated) = await SendAsync(HttpMethod.Patch, $"{Path}/{id}", "cfg-maker", "Platform.ReleaseManager",
            new { items = new[] { Item("tax.ipt.rate.fire", "0.2") } });
        patched.ShouldBe(HttpStatusCode.OK, updated.ToJsonString());
        updated["request"]!["items"]!.AsArray().Single()!.Text("key").ShouldBe("tax.ipt.rate.fire");
        var (other, _) = await SendAsync(HttpMethod.Patch, $"{Path}/{id}", "someone-else", "Platform.ReleaseManager", new { reason = "Not the maker of this draft" });
        other.ShouldBe(HttpStatusCode.Forbidden);

        var (foreign, _) = await SendAsync(HttpMethod.Post, Path + "/withdraw", "someone-else", "Platform.ReleaseManager", new { requestId = id, reason = "Not mine" });
        foreign.ShouldBe(HttpStatusCode.Forbidden);
        var (withdrawn, gone) = await SendAsync(HttpMethod.Post, Path + "/withdraw", "cfg-maker", "Platform.ReleaseManager", new { requestId = id, reason = "Changed my mind" });
        withdrawn.ShouldBe(HttpStatusCode.OK, gone.ToJsonString());
        gone.Text("request.status").ShouldBe("WITHDRAWN");
        var (afterPatch, _) = await SendAsync(HttpMethod.Patch, $"{Path}/{id}", "cfg-maker", "Platform.ReleaseManager", new { reason = "Too late to change this one" });
        afterPatch.ShouldBe(HttpStatusCode.Conflict);

        var (_, list) = await SendAsync(HttpMethod.Get, Path + "?status=WITHDRAWN", "rm", "Platform.ReleaseManager");
        list["items"]!.AsArray().Select(i => i!.Text("id")).ShouldContain(id);
    }

    [Fact]
    public async Task A_rejected_request_changes_nothing_and_a_pending_one_can_be_withdrawn()
    {
        var (_, hashBefore) = await ResolveAsync("tax.ipt.rate.fire", "2027-02-01");
        var (id, _) = await CreateAndSubmitAsync(Item("tax.ipt.rate.fire", "0.25"));
        var (rejected, body) = await SendAsync(HttpMethod.Post, Path + "/reject", "cfg-checker", "Platform.DesignAuthority", new { requestId = id, reason = "Source not confirmed" });
        rejected.ShouldBe(HttpStatusCode.OK, body.ToJsonString());
        body.Text("request.status").ShouldBe("REJECTED");
        (await ResolveAsync("tax.ipt.rate.fire", "2027-02-01")).Hash.ShouldBe(hashBefore);

        var (pendingId, _) = await CreateAndSubmitAsync(Item("tax.ipt.rate.fire", "0.26"));
        var (withdrawn, gone) = await SendAsync(HttpMethod.Post, Path + "/withdraw", "cfg-maker", "Platform.ReleaseManager", new { requestId = pendingId, reason = "Withdrawn before decision" });
        withdrawn.ShouldBe(HttpStatusCode.OK, gone.ToJsonString());
        gone.Text("request.status").ShouldBe("WITHDRAWN");
        var (decide, _) = await SendAsync(HttpMethod.Post, Path + "/approve", "cfg-checker", "Platform.DesignAuthority", new { requestId = pendingId, reason = "Too late" });
        decide.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_key_changed_since_the_request_was_written_makes_the_approval_stale()
    {
        var (first, _) = await CreateAndSubmitAsync(Item("tax.ipt.rate.general", "0.18"));
        var (second, _) = await CreateAndSubmitAsync(Item("tax.ipt.rate.general", "0.19"));
        var (ok, _) = await SendAsync(HttpMethod.Post, Path + "/approve", "cfg-checker", "Platform.DesignAuthority", new { requestId = first, reason = "First one is fine" });
        ok.ShouldBe(HttpStatusCode.OK);
        var (stale, _) = await SendAsync(HttpMethod.Post, Path + "/approve", "cfg-checker", "Platform.DesignAuthority", new { requestId = second, reason = "Second is now stale" });
        stale.ShouldBe(HttpStatusCode.Conflict);
        (await ResolveAsync("tax.ipt.rate.general", "2027-02-01")).Value.ShouldBe("0.18");
    }

    [Fact]
    public async Task Unrelated_keys_apply_after_another_change_and_the_overlay_keeps_both()
    {
        var (first, _) = await CreateAndSubmitAsync(Item("tax.ipt.rate.general", "0.17"));
        var (second, _) = await CreateAndSubmitAsync(Item("tax.ipt.rate.fire", "0.21"));
        (await SendAsync(HttpMethod.Post, Path + "/approve", "cfg-checker", "Platform.DesignAuthority", new { requestId = first, reason = "Fine" })).Status.ShouldBe(HttpStatusCode.OK);
        (await SendAsync(HttpMethod.Post, Path + "/approve", "cfg-checker", "Platform.DesignAuthority", new { requestId = second, reason = "Also fine" })).Status.ShouldBe(HttpStatusCode.OK);
        (await ResolveAsync("tax.ipt.rate.general", "2027-02-01")).Value.ShouldBe("0.17");
        (await ResolveAsync("tax.ipt.rate.fire", "2027-02-01")).Value.ShouldBe("0.21");
    }
}
