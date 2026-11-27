using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Npgsql;
using static CoreIns.IntegrationTests.Finance.FinanceSlice;

namespace CoreIns.IntegrationTests.Finance;

/// <summary>BO-FIN-08: the reconciliation framework (control totals against the ledger per currency), break handling and its link to the period close.</summary>
public sealed class ReconciliationTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string BilDefinition = "BIL_RECEIVABLE";
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

    private async Task<(HttpResponseMessage Response, JsonNode? Body)> SendAsync(HttpMethod method, string path, string user, object? body = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add(TestAuthHandler.RolesHeader, FinanceRole);
        request.Headers.Add(TestAuthHandler.UserHeader, user);
        request.Headers.AcceptLanguage.ParseAdd("en");
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

    /// <summary>One balanced journal in the period: a debit of GL-1210 (the receivable control account) against GL-1215, in one currency.</summary>
    private async Task PostReceivableAsync(string period, string amount, string currency = "EUR")
    {
        var day = period + "-10";
        var periodId = Guid.CreateVersion7();
        await database.ExecuteAsSuperuserAsync(
            $"INSERT INTO fin.financial_period (period_id, legal_entity_id, period_code, status, created_at) VALUES ('{periodId}', '{ApiHostFactory.LegalEntityId}', '{period}', 'OPEN', now()) ON CONFLICT (legal_entity_id, period_code) DO NOTHING",
            TestContext.Current.CancellationToken);
        await using var super = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        var ruleSet = await ScalarAsync<Guid>(super, "SELECT rule_set_id FROM fin.posting_rule_set WHERE version_no = 1");
        var period_ = await ScalarAsync<Guid>(super, $"SELECT period_id FROM fin.financial_period WHERE legal_entity_id = '{ApiHostFactory.LegalEntityId}' AND period_code = '{period}'");
        var journal = Guid.CreateVersion7();
        var number = "JNL2026" + Random.Shared.NextInt64(9_000_000_000, 9_999_999_999).ToString(CultureInfo.InvariantCulture);
        var line = (int no, string account, string side) =>
            $"""
            INSERT INTO fin.journal_line (line_id, journal_id, line_no, legal_entity_id, book, account_code, side, amount, currency, amount_functional, functional_currency, rule_code, business_date)
            VALUES (gen_random_uuid(), '{journal}', {no}, '{ApiHostFactory.LegalEntityId}', 'IFRS17', '{account}', '{side}', {amount}, '{currency}', {amount}, 'EUR', 'RAW', DATE '{day}');
            """;
        await database.ExecuteAsSuperuserAsync(
            $"""
            BEGIN;
            INSERT INTO fin.journal_entry (journal_id, journal_number, legal_entity_id, legal_entity_code, jurisdiction, book, accounting_date, business_date,
                period_id, source_type, source_module, source_event_type, source_event_ids, source_ref, rule_set_id, rule_set_version, rule_codes,
                functional_currency, correlation_id, posted_at, posted_by)
            VALUES ('{journal}', '{number}', '{ApiHostFactory.LegalEntityId}', 'GR-TEST', 'GR', 'IFRS17', DATE '{day}', DATE '{day}',
                '{period_}', 'EVENT', 'BIL', 'BillingEntryPosted', ARRAY[]::uuid[], 'recon-test', '{ruleSet}', 1, ARRAY['RAW'], 'EUR', 'x', now(), 'test');
            {line(1, "GL-1210", "DEBIT")}
            {line(2, "GL-1215", "CREDIT")}
            COMMIT;
            """, TestContext.Current.CancellationToken);
    }

    private Task<(HttpResponseMessage Response, JsonNode? Body)> ExchangeAsync(string user, string date, params (string Currency, string Amount, int? Count)[] totals) =>
        SendAsync(HttpMethod.Post, "/api/fin/v1/reconciliation/exchange", user, new
        {
            source = "BIL",
            date,
            totals = totals.Select(t => new { currency = t.Currency, amount = t.Amount, count = t.Count }).ToArray(),
        });

    private async Task<JsonArray> BreaksAsync(string period, string? state = null)
    {
        var (response, body) = await SendAsync(HttpMethod.Get, $"/api/fin/v1/reconciliation/breaks?period={period}&definition={BilDefinition}" + (state is null ? string.Empty : $"&state={state}"), "anna");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body!["items"]!.AsArray();
    }

    [Fact]
    public async Task A_one_cent_difference_creates_a_break_that_a_rerun_keeps_and_a_match_clears()
    {
        const string period = "2026-08";
        await PostReceivableAsync(period, "100.00");

        var (response, body) = await ExchangeAsync("anna", "2026-08-31", ("EUR", "100.01", 1));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body!["comparisonResult"]!.GetValue<string>().ShouldBe("BREAKS");
        var breakId = body["breakIds"]!.AsArray().Single()!.GetValue<string>();

        var found = (await BreaksAsync(period)).Single()!;
        found["kind"]!.GetValue<string>().ShouldBe("AMOUNT");
        found["state"]!.GetValue<string>().ShouldBe("OPEN");
        found["difference"]!.GetValue<string>().ShouldBe("0.01");
        found["sourceAmount"]!.GetValue<string>().ShouldBe("100.01");
        found["ledgerAmount"]!.GetValue<string>().ShouldBe("100.00");

        // A rerun with the same figures is idempotent: the same break, a new run.
        var (rerun, rerunBody) = await SendAsync(HttpMethod.Post, "/api/fin/v1/reconciliation/run", "anna", new { period, definition = BilDefinition });
        rerun.StatusCode.ShouldBe(HttpStatusCode.OK, rerunBody?.ToJsonString());
        rerunBody!["runs"]!.AsArray().Single()!["runNo"]!.GetValue<int>().ShouldBe(2);
        (await BreaksAsync(period)).Single()!["breakId"]!.GetValue<string>().ShouldBe(breakId);

        var (matched, matchBody) = await SendAsync(HttpMethod.Post, "/api/fin/v1/reconciliation/match", "ben", new { breakId, reason = "Rounding of the BIL day total, explained in ticket FIN-1" });
        matched.StatusCode.ShouldBe(HttpStatusCode.OK, matchBody?.ToJsonString());
        matchBody!["break"]!["state"]!.GetValue<string>().ShouldBe("RESOLVED");
        (await BreaksAsync(period, "OPEN")).Count.ShouldBe(0);

        // The match stands for the same figures: a new run does not reopen it.
        var (again, againBody) = await SendAsync(HttpMethod.Post, "/api/fin/v1/reconciliation/run", "anna", new { period, definition = BilDefinition });
        again.StatusCode.ShouldBe(HttpStatusCode.OK, againBody?.ToJsonString());
        againBody!["runs"]!.AsArray().Single()!["state"]!.GetValue<string>().ShouldBe("CLEAN");

        var (matchedAgain, _) = await SendAsync(HttpMethod.Post, "/api/fin/v1/reconciliation/match", "ben", new { breakId, reason = "again" });
        matchedAgain.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Totals_are_compared_per_currency_and_a_currency_on_one_side_only_is_a_break()
    {
        const string period = "2026-07";
        await PostReceivableAsync(period, "50.00");
        await PostReceivableAsync(period, "20.00", "USD");
        await PostReceivableAsync(period, "7.00", "GBP");

        var (response, body) = await ExchangeAsync("anna", "2026-07-31", ("EUR", "50.00", 1), ("USD", "20.01", 1), ("CHF", "5.00", null));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body!["comparisonResult"]!.GetValue<string>().ShouldBe("BREAKS");

        var breaks = await BreaksAsync(period, "OPEN");
        breaks.Count.ShouldBe(3);
        string Kind(string currency) => breaks.Single(b => b!["currency"]!.GetValue<string>() == currency)!["kind"]!.GetValue<string>();
        Kind("USD").ShouldBe("AMOUNT");
        Kind("CHF").ShouldBe("MISSING_LEDGER");
        Kind("GBP").ShouldBe("MISSING_SOURCE");
        breaks.Any(b => b!["currency"]!.GetValue<string>() == "EUR").ShouldBeFalse();

        var (detail, detailBody) = await SendAsync(HttpMethod.Get, $"/api/fin/v1/reconciliation/detail?period={period}&definition={BilDefinition}", "anna");
        detail.StatusCode.ShouldBe(HttpStatusCode.OK, detailBody?.ToJsonString());
        var totals = detailBody!["runs"]!.AsArray().Single()!["currencies"]!.AsArray();
        totals.Select(t => t!["currency"]!.GetValue<string>()).ShouldBe(["CHF", "EUR", "GBP", "USD"]);
        var usd = totals.Single(t => t!["currency"]!.GetValue<string>() == "USD")!;
        usd["sourceAmount"]!.GetValue<string>().ShouldBe("20.01");
        usd["ledgerAmount"]!.GetValue<string>().ShouldBe("20.00");
        usd["difference"]!.GetValue<string>().ShouldBe("0.01");
        detailBody["definitions"]!.AsArray().Count.ShouldBe(3);

        // Fixing the source clears the breaks of the currencies that now agree when the next run finds nothing.
        var (fixedUp, fixedBody) = await ExchangeAsync("anna", "2026-07-31", ("EUR", "50.00", 1), ("USD", "20.00", 1), ("GBP", "7.00", 1), ("CHF", "0.00", 0));
        fixedUp.StatusCode.ShouldBe(HttpStatusCode.OK, fixedBody?.ToJsonString());
        fixedBody!["comparisonResult"]!.GetValue<string>().ShouldBe("MATCHED");
        (await BreaksAsync(period, "OPEN")).Count.ShouldBe(0);
        (await BreaksAsync(period, "RESOLVED")).Count.ShouldBe(3);
    }

    [Fact]
    public async Task Open_breaks_block_the_close_task_until_a_second_person_accepts_them_with_a_reason()
    {
        const string period = "2026-06";
        await PostReceivableAsync(period, "10.00");
        (await ExchangeAsync("anna", "2026-06-30", ("EUR", "9.00", 1))).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var (tasks, tasksBody) = await SendAsync(HttpMethod.Get, $"/api/fin/v1/close/tasks?period={period}", "anna");
        tasks.StatusCode.ShouldBe(HttpStatusCode.OK, tasksBody?.ToJsonString());
        tasksBody!["tasks"]!.AsArray().Single(t => t!["task"]!.GetValue<string>() == "RECONCILIATIONS")!["blocker"]!.GetValue<string>().ShouldContain("open");

        var (blocked, _) = await SendAsync(HttpMethod.Post, "/api/fin/v1/close/sign-off", "anna", new { period, task = "RECONCILIATIONS" });
        blocked.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        // The person who ran the reconciliation cannot accept its breaks; a reason is mandatory.
        var (same, _) = await SendAsync(HttpMethod.Post, "/api/fin/v1/close/accept-break", "anna", new { period, reason = "timing" });
        same.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var (noReason, _) = await SendAsync(HttpMethod.Post, "/api/fin/v1/close/accept-break", "ben", new { period, reason = string.Empty });
        noReason.StatusCode.ShouldNotBe(HttpStatusCode.OK);
        (await BreaksAsync(period, "OPEN")).Count.ShouldBe(1);

        var (accepted, acceptedBody) = await SendAsync(HttpMethod.Post, "/api/fin/v1/close/accept-break", "ben", new { period, reason = "BIL day total posted after the cut-off; settles next month" });
        accepted.StatusCode.ShouldBe(HttpStatusCode.OK, acceptedBody?.ToJsonString());
        acceptedBody!["tasks"]!.AsArray().Single(t => t!["task"]!.GetValue<string>() == "RECONCILIATIONS")!["status"]!.GetValue<string>().ShouldBe("SIGNED_OFF");

        var acceptedBreak = (await BreaksAsync(period, "ACCEPTED")).Single()!;
        acceptedBreak["decidedBy"]!.GetValue<string>().ShouldNotBeNullOrEmpty();
        acceptedBreak["reason"]!.GetValue<string>().ShouldContain("cut-off");

        // A rerun finding the same difference leaves it accepted.
        var (rerun, _) = await SendAsync(HttpMethod.Post, "/api/fin/v1/reconciliation/run", "anna", new { period });
        rerun.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await BreaksAsync(period, "OPEN")).Count.ShouldBe(0);
        (await BreaksAsync(period, "ACCEPTED")).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Accepting_breaks_of_a_clean_period_and_malformed_control_totals_are_refused()
    {
        const string period = "2026-05";
        var (nothing, _) = await SendAsync(HttpMethod.Post, "/api/fin/v1/close/accept-break", "ben", new { period, reason = "nothing to accept" });
        nothing.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var (cents, _) = await ExchangeAsync("anna", "2026-05-31", ("EUR", "1.005", 1));
        cents.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var (repeated, _) = await ExchangeAsync("anna", "2026-05-31", ("EUR", "1.00", 1), ("EUR", "2.00", 1));
        repeated.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }
}
