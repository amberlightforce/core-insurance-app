using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CoreIns.Modules.Channels.Domain;

namespace CoreIns.IntegrationTests.Channels;

/// <summary>BO-CHN-05: the pure decision logic (specificity, limits, fail closed).</summary>
public sealed class PermissionMatrixEngineTests
{
    private static MatrixEntry E(string t, string c, string a, string p, string outcome, decimal? max = null) => new(t, c, a, p, outcome, max, MatrixLevels.LegalEntity);

    private static MatrixDecision Ask(MatrixEntry[] entries, string t, string c, string a, string? p = null, decimal? amount = null) =>
        MatrixEngine.Evaluate(entries, new MatrixQuery(t, c, a, p, amount));

    [Fact]
    public void REQ_CHN_049_the_most_specific_entry_wins_whatever_the_order()
    {
        var entries = new[]
        {
            E("policy.change", "*", "CUSTOMER", "*", MatrixOutcomes.Allow),
            E("policy.change", "WEB_DIRECT", "CUSTOMER", "MOTOR", MatrixOutcomes.Refer),
            E("*", "*", "*", "*", MatrixOutcomes.Deny),
        };

        Ask(entries, "policy.change", "WEB_DIRECT", "CUSTOMER", "MOTOR").Outcome.ShouldBe(MatrixOutcomes.Refer);
        Ask(entries, "policy.change", "WEB_DIRECT", "CUSTOMER", "HOME").Outcome.ShouldBe(MatrixOutcomes.Allow);
        Ask(entries.Reverse().ToArray(), "policy.change", "WEB_DIRECT", "CUSTOMER", "MOTOR").Outcome.ShouldBe(MatrixOutcomes.Refer);
        Ask(entries, "policy.cancel", "WEB_DIRECT", "CUSTOMER").Outcome.ShouldBe(MatrixOutcomes.Deny);
    }

    [Fact]
    public void REQ_CHN_049_an_equally_specific_tie_goes_to_the_more_restrictive_outcome()
    {
        var entries = new[] { E("policy.change", "WEB_DIRECT", "*", "*", MatrixOutcomes.Allow), E("policy.change", "*", "CUSTOMER", "*", MatrixOutcomes.Deny) };

        // actor (4) outranks channel (2): DENY. Same weight on both sides is the tie case below.
        Ask(entries, "policy.change", "WEB_DIRECT", "CUSTOMER").Outcome.ShouldBe(MatrixOutcomes.Deny);
        var tie = new[] { E("policy.change", "WEB_DIRECT", "*", "*", MatrixOutcomes.Allow), E("policy.change", "WEB_DIRECT", "*", "*", MatrixOutcomes.Refer) };
        Ask(tie, "policy.change", "WEB_DIRECT", "CUSTOMER").Outcome.ShouldBe(MatrixOutcomes.Refer);
    }

    [Fact]
    public void REQ_CHN_050_an_amount_over_the_limit_or_missing_refers_and_no_entry_is_denied()
    {
        var entries = new[] { E("policy.change", "*", "CUSTOMER", "*", MatrixOutcomes.Allow, 500m) };

        Ask(entries, "policy.change", "WEB_DIRECT", "CUSTOMER", amount: 500m).Outcome.ShouldBe(MatrixOutcomes.Allow);
        var over = Ask(entries, "policy.change", "WEB_DIRECT", "CUSTOMER", amount: 500.01m);
        over.Outcome.ShouldBe(MatrixOutcomes.Refer);
        over.Reasons.ShouldContain("LIMIT_EXCEEDED");
        Ask(entries, "policy.change", "WEB_DIRECT", "CUSTOMER").Reasons.ShouldContain("AMOUNT_MISSING");
        Ask([], "policy.change", "WEB_DIRECT", "CUSTOMER").Outcome.ShouldBe(MatrixOutcomes.Deny);
    }

    [Fact]
    public void The_baseline_denies_by_default_and_refers_an_AI_agents_bind()
    {
        var baseline = MatrixBaseline.Entries.ToArray();

        Ask(baseline, "policy.bind", "AI_CHN", "AI_AGENT").Outcome.ShouldBe(MatrixOutcomes.Refer);
        Ask(baseline, "policy.bind", "WEB_DIRECT", "CUSTOMER").Outcome.ShouldBe(MatrixOutcomes.Allow);
        Ask(baseline, "payment.pay", "AI_CHN", "AI_AGENT").Outcome.ShouldBe(MatrixOutcomes.Deny);
        Ask(baseline, "policy.change", "WEB_DIRECT", "PARTNER").Outcome.ShouldBe(MatrixOutcomes.Deny);
    }

    [Fact]
    public void REQ_CHN_057_the_diff_lists_added_removed_and_changed_entries()
    {
        var older = new[] { E("policy.change", "*", "CUSTOMER", "*", MatrixOutcomes.Allow), E("contact.update", "*", "CUSTOMER", "*", MatrixOutcomes.Allow) };
        var newer = new[] { E("policy.change", "*", "CUSTOMER", "*", MatrixOutcomes.Refer), E("quote.start", "*", "CUSTOMER", "*", MatrixOutcomes.Allow) };

        var json = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(MatrixEngine.Diff(older, newer)))!;
        json["added"]!.AsArray().Count.ShouldBe(1);
        json["removed"]!.AsArray().Count.ShouldBe(1);
        json["changed"]!.AsArray().Count.ShouldBe(1);
    }
}

/// <summary>BO-CHN-05: the maintenance flow over HTTP: draft, scenarios, maker-checker, effective activation, evaluate with a ticket.</summary>
public sealed class PermissionMatrixFlowTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncDisposable
{
    private const string Maker = "Staff.Underwriter";
    private const string Checker = "Staff.UnderwritingManager";
    private readonly ApiHostFactory _factory = new(database.AppConnectionString);
    private HttpClient? _client;

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        await _factory.DisposeAsync();
    }

    private async Task<(HttpResponseMessage Response, JsonNode? Body)> SendAsync(HttpMethod method, string path, string roles, string user, object? body = null)
    {
        _client ??= _factory.CreateClient();
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add(TestAuthHandler.RolesHeader, roles);
        request.Headers.Add(TestAuthHandler.UserHeader, user);
        if (method != HttpMethod.Get)
        {
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (response, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    private Task<(HttpResponseMessage Response, JsonNode? Body)> EvaluateAsync(string roles, object body) =>
        SendAsync(HttpMethod.Post, "/api/chn/v1/permissions/evaluate", roles, "customer-1", body);

    [Fact]
    public async Task REQ_CHN_047_to_052_draft_test_submit_schedule_then_evaluate()
    {
        // Fail closed: no matrix in force yet.
        var (_, none) = await EvaluateAsync("Customer.Portal", new { transactionType = "contact.update", channel = "WEB_DIRECT" });
        none!["outcome"]!.GetValue<string>().ShouldBe("DENY");
        none["reasons"]![0]!.GetValue<string>().ShouldBe("NO_ACTIVE_MATRIX");

        // Unknown transaction type.
        var (unknown, _) = await EvaluateAsync("Customer.Portal", new { transactionType = "teleport", channel = "WEB_DIRECT" });
        unknown.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        // Maker drafts from the baseline with one change and a scenario that must hold.
        var scenarios = new object[]
        {
            new { name = "customer renews", transactionType = "policy.renewal.accept", channel = "WEB_DIRECT", actorType = "CUSTOMER", expect = "ALLOW" },
            new { name = "agent cannot bind unattended", transactionType = "policy.bind", channel = "AI_CHN", actorType = "AI_AGENT", expect = "REFER" },
        };
        var (created, draft) = await SendAsync(HttpMethod.Post, "/api/chn/v1/permission-matrices/create-draft", Maker, "alice", new
        {
            entries = new object[] { new { transactionType = "policy.change", actorType = "CUSTOMER", outcome = "ALLOW", maxAmount = 300 } },
            scenarios,
        });
        created.StatusCode.ShouldBe(HttpStatusCode.OK, draft?.ToJsonString());
        var id = draft!["version"]!["matrix"]!["matrixId"]!.GetValue<string>();
        draft["version"]!["matrix"]!["status"]!.GetValue<string>().ShouldBe("DRAFT");
        draft["results"]!.AsArray().All(r => r!["pass"]!.GetValue<bool>()).ShouldBeTrue();
        draft["diff"]!["changed"]!.AsArray().Count.ShouldBe(1);

        // A final (EU) entry cannot be changed.
        var (finalEdit, finalBody) = await SendAsync(HttpMethod.Post, "/api/chn/v1/permission-matrices/update-entry", Maker, "alice",
            new { matrixId = id, entries = new object[] { new { transactionType = "policy.bind", actorType = "AI_AGENT", outcome = "ALLOW" } } });
        finalEdit.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        finalBody!.ToJsonString().ShouldContain("FINAL-ENTRY");

        // A failing scenario blocks the submit.
        var (bad, _) = await SendAsync(HttpMethod.Post, "/api/chn/v1/permission-matrices/update-entry", Maker, "alice", new
        {
            matrixId = id,
            scenarios = new object[] { new { name = "wrong hope", transactionType = "policy.cancel", channel = "WEB_DIRECT", actorType = "CUSTOMER", expect = "ALLOW" } },
        });
        bad.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (blocked, blockedBody) = await SendAsync(HttpMethod.Post, "/api/chn/v1/permission-matrices/submit", Maker, "alice", new { matrixId = id });
        blocked.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        blockedBody!.ToJsonString().ShouldContain("SCENARIO-FAILED");

        // Restore the scenarios, submit, then the maker cannot approve their own matrix; a different checker can.
        (await SendAsync(HttpMethod.Post, "/api/chn/v1/permission-matrices/update-entry", Maker, "alice", new { matrixId = id, scenarios })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (submitted, submittedBody) = await SendAsync(HttpMethod.Post, "/api/chn/v1/permission-matrices/submit", Maker, "alice", new { matrixId = id });
        submitted.StatusCode.ShouldBe(HttpStatusCode.OK, submittedBody?.ToJsonString());

        var (own, ownBody) = await SendAsync(HttpMethod.Post, "/api/chn/v1/permission-matrices/schedule", "Platform.Admin", "alice", new { matrixId = id });
        own.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        ownBody!.ToJsonString().ShouldContain("SELF-APPROVAL");

        var future = DateTimeOffset.UtcNow.AddDays(30).ToString("o");
        var (scheduledLater, laterBody) = await SendAsync(HttpMethod.Post, "/api/chn/v1/permission-matrices/schedule", Checker, "bob", new { matrixId = id, effectiveFrom = future });
        scheduledLater.StatusCode.ShouldBe(HttpStatusCode.OK, laterBody?.ToJsonString());

        // Effective-dated: still nothing in force today.
        var (stillNone, stillNoneBody) = await EvaluateAsync("Customer.Portal", new { transactionType = "contact.update", channel = "WEB_DIRECT" });
        stillNone.StatusCode.ShouldBe(HttpStatusCode.OK);
        stillNoneBody!["reasons"]![0]!.GetValue<string>().ShouldBe("NO_ACTIVE_MATRIX");

        // A second matrix, effective now, with the same checker rule.
        var (second, secondBody) = await SendAsync(HttpMethod.Post, "/api/chn/v1/permission-matrices/create-draft", Maker, "alice", new { scenarios });
        second.StatusCode.ShouldBe(HttpStatusCode.OK, secondBody?.ToJsonString());
        var id2 = secondBody!["version"]!["matrix"]!["matrixId"]!.GetValue<string>();
        (await SendAsync(HttpMethod.Post, "/api/chn/v1/permission-matrices/submit", Maker, "alice", new { matrixId = id2 })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SendAsync(HttpMethod.Post, "/api/chn/v1/permission-matrices/schedule", Checker, "bob", new { matrixId = id2 })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Evaluate as a customer: allowed, over the limit refers with a ticket, cancel refers, partner is denied.
        var (allow, allowBody) = await EvaluateAsync("Customer.Portal", new { transactionType = "policy.renewal.accept", channel = "WEB_DIRECT" });
        allow.StatusCode.ShouldBe(HttpStatusCode.OK, allowBody?.ToJsonString());
        allowBody!["outcome"]!.GetValue<string>().ShouldBe("ALLOW");
        allowBody["ticketId"].ShouldBeNull();

        var (refer, referBody) = await EvaluateAsync("Customer.Portal", new { transactionType = "policy.change", channel = "WEB_DIRECT", proposedValues = new { amount = "900.00" } });
        referBody!["outcome"]!.GetValue<string>().ShouldBe("REFER");
        referBody["reasons"]![0]!.GetValue<string>().ShouldBe("LIMIT_EXCEEDED");
        var ticket = referBody["ticketId"]!.GetValue<string>();
        (await SendAsync(HttpMethod.Get, $"/api/chn/v1/tickets/{ticket}", "Staff.Underwriter", "alice")).Body!["status"]!.GetValue<string>().ShouldBe("PENDING");

        // A what-if does not raise a ticket.
        var (_, whatIf) = await EvaluateAsync("Customer.Portal", new { transactionType = "policy.change", channel = "WEB_DIRECT", raiseTicket = false, proposedValues = new { amount = 900 } });
        whatIf!["outcome"]!.GetValue<string>().ShouldBe("REFER");
        whatIf["ticketId"].ShouldBeNull();

        // A customer cannot claim another actor type: the supplied actor is ignored for a non-staff caller.
        var (spoof, spoofBody) = await EvaluateAsync("Customer.Portal", new { transactionType = "payment.pay", channel = "WEB_DIRECT", actor = "STAFF" });
        spoof.StatusCode.ShouldBe(HttpStatusCode.OK);
        spoofBody!["entry"]!["actorType"]!.GetValue<string>().ShouldBe("CUSTOMER");

        // Staff can ask what-if for another actor.
        var (_, partner) = await EvaluateAsync(Maker, new { transactionType = "policy.change", channel = "API", actor = "PARTNER" });
        partner!["outcome"]!.GetValue<string>().ShouldBe("DENY");

        // Reads: active matrix and the diff of the first against the second.
        var (_, active) = await SendAsync(HttpMethod.Get, "/api/chn/v1/permission-matrices/active", Maker, "alice");
        active!["version"]!["matrix"]!["matrixId"]!.GetValue<string>().ShouldBe(id2);
        var (_, diff) = await SendAsync(HttpMethod.Get, $"/api/chn/v1/permission-matrices/diff?id={id}&against={id2}", Maker, "alice");
        diff!["diff"]!["changed"]!.AsArray().Count.ShouldBe(1);

        // A customer cannot touch the matrix.
        (await SendAsync(HttpMethod.Post, "/api/chn/v1/permission-matrices/create-draft", "Customer.Portal", "customer-1", new { })).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
