using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace CoreIns.IntegrationTests.Finance;

/// <summary>BO-FIN-02: fin.Journal.reverse, fin.Balance.get and fin.WrittenPremium.query on a real PostgreSQL through the real Host.</summary>
public sealed class JournalReverseTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
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

    private async Task<(HttpResponseMessage Response, JsonNode? Body)> SendAsync(HttpMethod method, string path, object? body = null, string roles = "Staff.Finance", string user = "maker-1", bool dryRun = false)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add(TestAuthHandler.RolesHeader, roles);
        request.Headers.Add(TestAuthHandler.UserHeader, user);
        if (method != HttpMethod.Get)
        {
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        }

        if (dryRun)
        {
            request.Headers.Add("X-Dry-Run", "true");
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (response, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    /// <summary>Posts a manual journal (debit GL-1110, credit GL-1210) through maker-checker and returns the posted journal id.</summary>
    private async Task<string> PostedAsync(string date, string amount = "120.00")
    {
        var (created, draft) = await SendAsync(HttpMethod.Post, "/api/fin/v1/manual-journals", new
        {
            description = "Reverse test",
            book = "IFRS17",
            accountingDate = date,
            currency = "EUR",
            evidence = new { reference = "DOC-1" },
            lines = new[] { new { account = "GL-1110", side = "DEBIT", amount }, new { account = "GL-1210", side = "CREDIT", amount } },
        });
        created.StatusCode.ShouldBe(HttpStatusCode.Created, draft?.ToJsonString());
        var id = draft!["manualJournal"]!["id"]!.GetValue<string>();
        (await SendAsync(HttpMethod.Post, "/api/fin/v1/manual-journals/submit", new { id })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (approved, body) = await SendAsync(HttpMethod.Post, "/api/fin/v1/manual-journals/decide", new { id, decision = "APPROVE" }, user: "checker-1");
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body!["postedIds"]![0]!.GetValue<string>();
    }

    [Fact]
    public async Task Reversing_posts_the_opposite_journal_and_a_second_reverse_is_refused()
    {
        var id = await PostedAsync("2026-09-10");
        (await SendAsync(HttpMethod.Post, "/api/fin/v1/journals/reverse", new { journalId = id, reason = " " })).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var (ok, body) = await SendAsync(HttpMethod.Post, "/api/fin/v1/journals/reverse", new { journalId = id, reason = "Wrong account" });
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        var reversal = body!["reversalJournal"]!;
        reversal["sourceType"]!.GetValue<string>().ShouldBe("REVERSAL");
        reversal["accountingDate"]!.GetValue<string>().ShouldBe("2026-09-10");
        reversal["lines"]!.AsArray().Count.ShouldBe(2);

        var (again, problem) = await SendAsync(HttpMethod.Post, "/api/fin/v1/journals/reverse", new { journalId = id, reason = "Again" });
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict, problem?.ToJsonString());
        problem!.ToJsonString().ShouldContain("FIN-ERR-ALREADY-REVERSED");

        // A reversal itself is not reversible, and the original shows who reversed it.
        var reversalId = reversal["journalId"]!.GetValue<string>();
        (await SendAsync(HttpMethod.Post, "/api/fin/v1/journals/reverse", new { journalId = reversalId, reason = "No" })).Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var (_, original) = await SendAsync(HttpMethod.Get, $"/api/fin/v1/journals/{id}");
        original!["journal"]!["reversedByJournalId"]!.GetValue<string>().ShouldBe(reversalId);

        (await SendAsync(HttpMethod.Post, "/api/fin/v1/journals/reverse", new { journalId = id, reason = "x" }, roles: "Staff.Underwriter")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_reversal_into_a_locked_period_goes_to_the_next_open_period_and_dry_run_posts_nothing()
    {
        var id = await PostedAsync("2026-06-12");
        await database.ExecuteAsSuperuserAsync(
            $"UPDATE fin.financial_period SET status = 'LOCKED' WHERE legal_entity_id = '{ApiHostFactory.LegalEntityId}' AND period_code = '2026-06'",
            TestContext.Current.CancellationToken);

        var (dry, dryBody) = await SendAsync(HttpMethod.Post, "/api/fin/v1/journals/reverse", new { journalId = id, reason = "Dry" }, dryRun: true);
        dry.StatusCode.ShouldBe(HttpStatusCode.OK, dryBody?.ToJsonString());

        var (ok, body) = await SendAsync(HttpMethod.Post, "/api/fin/v1/journals/reverse", new { journalId = id, reason = "Period was closed" });
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body!["reversalJournal"]!["accountingDate"]!.GetValue<string>().ShouldBe("2026-07-01");
        body["reversalJournal"]!["period"]!.GetValue<string>().ShouldBe("2026-07");
    }

    [Fact]
    public async Task A_journal_from_another_module_or_an_unknown_journal_is_refused()
    {
        (await SendAsync(HttpMethod.Post, "/api/fin/v1/journals/reverse", new { journalId = Guid.NewGuid(), reason = "x" })).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var id = await PostedAsync("2026-08-05");
        await database.ExecuteAsSuperuserAsync(
            $"ALTER TABLE fin.journal_entry DISABLE TRIGGER USER; UPDATE fin.journal_entry SET source_module = 'BIL' WHERE journal_id = '{id}'; ALTER TABLE fin.journal_entry ENABLE TRIGGER USER;",
            TestContext.Current.CancellationToken);
        var (response, problem) = await SendAsync(HttpMethod.Post, "/api/fin/v1/journals/reverse", new { journalId = id, reason = "x" });
        response.StatusCode.ShouldBe((HttpStatusCode)422, problem?.ToJsonString());
        problem!.ToJsonString().ShouldContain("FIN-ERR-SOURCE-OWNED");
    }

    [Fact]
    public async Task The_balance_equals_the_sum_of_the_lines_and_a_reversal_brings_it_back()
    {
        var id = await PostedAsync("2026-03-03", "50.00");
        await PostedAsync("2026-03-20", "25.50");
        var (_, before) = await SendAsync(HttpMethod.Get, "/api/fin/v1/balances/GL-1110?period=2026-03&book=IFRS17");
        var b = before!["balances"]!;
        b["debit"]!.GetValue<string>().ShouldBe("75.50");
        b["credit"]!.GetValue<string>().ShouldBe("0.00");
        b["balance"]!.GetValue<string>().ShouldBe("75.50");
        b["lines"]!.GetValue<int>().ShouldBe(2);

        var (_, tb) = await SendAsync(HttpMethod.Get, "/api/fin/v1/balances/trial-balance?period=2026-03&book=IFRS17");
        tb!["balances"]!.AsArray().Single(r => r!["account"]!.GetValue<string>() == "GL-1110")!["debit"]!.GetValue<string>().ShouldBe("75.50");

        (await SendAsync(HttpMethod.Post, "/api/fin/v1/journals/reverse", new { journalId = id, reason = "Undo" })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (_, after) = await SendAsync(HttpMethod.Get, "/api/fin/v1/balances/GL-1110?period=2026-03&book=IFRS17");
        after!["balances"]!["balance"]!.GetValue<string>().ShouldBe("25.50");

        var (_, cumulative) = await SendAsync(HttpMethod.Get, "/api/fin/v1/balances/GL-1110?view=CUMULATIVE&book=IFRS17");
        cumulative!["balances"]!["lines"]!.GetValue<int>().ShouldBeGreaterThanOrEqualTo(3);
        (await SendAsync(HttpMethod.Get, "/api/fin/v1/balances/GL-1110")).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await SendAsync(HttpMethod.Get, "/api/fin/v1/balances/GL-1110?period=2026-03&dimensions=bogus:1")).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await SendAsync(HttpMethod.Get, "/api/fin/v1/balances/GL-1110?period=2026-03", roles: "Staff.Underwriter")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Written_premium_query_pages_and_validates()
    {
        var (ok, page) = await SendAsync(HttpMethod.Get, "/api/fin/v1/written-premium/query?limit=5");
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, page?.ToJsonString());
        page!["items"]!.AsArray().ShouldNotBeNull();
        (await SendAsync(HttpMethod.Get, "/api/fin/v1/written-premium/query?limit=0")).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await SendAsync(HttpMethod.Get, "/api/fin/v1/written-premium/query?dimensions=policyNumber:1")).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await SendAsync(HttpMethod.Get, "/api/fin/v1/written-premium/query", roles: "Staff.Underwriter")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
