using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CoreIns.IntegrationTests.Party;
using CoreIns.IntegrationTests.Product;
using Npgsql;
using static CoreIns.IntegrationTests.Finance.FinanceSlice;

namespace CoreIns.IntegrationTests.Finance;

/// <summary>BO-FIN-06a: the earning engine over real posted premium, its idempotent run, simulation and open-period rule.</summary>
public sealed class EarningRunTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string FinanceRole = "Staff.Finance";
    private ApiHostFactory _factory = null!;
    private HttpClient _client = null!;
    private FinanceSlice _slice = null!;
    private NpgsqlDataSource _db = null!;
    private string? _artefactHash;

    public async ValueTask InitializeAsync()
    {
        _factory = new ApiHostFactory(database.AppConnectionString);
        _client = _factory.CreateClient();
        _slice = new FinanceSlice(_factory);
        _db = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        var (response, body) = await ProductApi.ImportAsync(_client, ProductApi.Seed("MOTOR-FIN"));
        response.IsSuccessStatusCode.ShouldBeTrue(body?.ToJsonString());
        _artefactHash = body.Text("artefactHash");

        // Every test starts without runs: terms are new per test, so older journals do not interfere.
        await database.ExecuteAsSuperuserAsync("DELETE FROM fin.earned_premium; DELETE FROM fin.earning_run", TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private async Task<(HttpResponseMessage Response, JsonNode? Body)> SendAsync(HttpMethod method, string path, object? body = null, string roles = FinanceRole)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add(TestAuthHandler.RolesHeader, roles);
        request.Headers.Add(TestAuthHandler.UserHeader, "anna");
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

    /// <summary>A policy term 2026-11-01 to 2027-11-01 (365 days) with the given premium written on 2026-11-02, split over coverages.</summary>
    private async Task<Policy> WrittenTermAsync(string start, string end, params (string Coverage, string Amount)[] premiums)
    {
        var policy = Policy.New($"POL{Random.Shared.Next(100_000_000, 999_999_999)}", "MOTOR-FIN", _artefactHash!);
        await _slice.PolicyBoundAsync(policy);
        await _slice.DrainAsync();
        await database.ExecuteAsSuperuserAsync(
            $"UPDATE fin.policy_context SET term_start = DATE '{start}', term_end = DATE '{end}' WHERE policy_term_id = '{policy.TermId}'", TestContext.Current.CancellationToken);
        var account = Guid.CreateVersion7();
        var total = premiums.Sum(p => decimal.Parse(p.Amount, CultureInfo.InvariantCulture)).ToString("0.00", CultureInfo.InvariantCulture);
        var lines = new List<JsonObject> { Line("LA-01", "DEBIT", total, policy, account, "PREM-MTPL", "PREMIUM", "MTPL") };
        lines.AddRange(premiums.Select(p => Line("LA-04", "CREDIT", p.Amount, policy, account, "PREM-" + p.Coverage, "PREMIUM", p.Coverage)));
        await _slice.EntryAsync(account, "WRITTEN", "2026-11-02", [.. lines]);
        await _slice.DrainAsync();
        return policy;
    }

    private Task<decimal> EarnedAsync(Policy policy) => ScalarAsync<decimal>(_db,
        $"SELECT coalesce(sum(CASE side WHEN 'DEBIT' THEN amount ELSE 0 END), 0) FROM fin.journal_line WHERE policy_term_id = '{policy.TermId}' AND account_code = 'GL-2110' AND rule_code = 'EARN-UPR-RELEASE'");

    private Task<long> JournalCountAsync() => ScalarAsync<long>(_db, "SELECT count(*) FROM fin.journal_entry WHERE source_event_type = 'EarningRun'");

    private static string Money(JsonNode? node) => node!["amount"]!.GetValue<string>();

    [Fact]
    public async Task Status_is_empty_before_the_first_run_and_PolicyBound_gives_the_term_its_dates()
    {
        var (response, body) = await SendAsync(HttpMethod.Get, "/api/fin/v1/earning/status");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        (body?["run"] is null).ShouldBeTrue(body?.ToJsonString());

        var policy = Policy.New($"POL{Random.Shared.Next(100_000_000, 999_999_999)}", "MOTOR-FIN", _artefactHash!);
        await _slice.PolicyBoundAsync(policy);
        await _slice.DrainAsync();
        (await ScalarAsync<long>(_db, $"SELECT count(*) FROM fin.policy_context WHERE policy_term_id = '{policy.TermId}' AND term_start IS NOT NULL AND term_end > term_start")).ShouldBe(1);
        (await SendAsync(HttpMethod.Get, "/api/fin/v1/earning/status", roles: "Staff.Underwriter")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Run_earns_by_day_posts_one_balanced_journal_and_a_rerun_posts_only_the_difference()
    {
        var policy = await WrittenTermAsync("2026-11-01", "2027-11-01", ("MTPL", "250.00"), ("OD", "115.00"));
        var before = await JournalCountAsync();

        // Simulate first: it answers with the movement and writes nothing.
        var (simResponse, sim) = await SendAsync(HttpMethod.Post, "/api/fin/v1/earning/simulate", new { asOf = "2026-11-30" });
        simResponse.StatusCode.ShouldBe(HttpStatusCode.OK, sim?.ToJsonString());
        sim!["run"]!["simulated"]!.GetValue<bool>().ShouldBeTrue();
        sim["run"]!["id"].ShouldBeNull();
        (await JournalCountAsync()).ShouldBe(before);
        (await ScalarAsync<long>(_db, "SELECT count(*) FROM fin.earning_run")).ShouldBe(0);

        // 30 of 365 days of 365.00 = 30.00, split over the coverages by the largest-remainder allocator.
        var (response, run) = await SendAsync(HttpMethod.Post, "/api/fin/v1/earning/run", new { asOf = "2026-11-30" });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, run?.ToJsonString());
        run!["run"]!["state"]!.GetValue<string>().ShouldBe("DONE");
        run["run"]!["period"]!.GetValue<string>().ShouldBe("2026-11");
        run["run"]!["journalIds"]!.AsArray().Count.ShouldBe(1);
        var result = run["results"]!.AsArray().Single(r => r!["policyTermId"]!.GetValue<string>() == policy.TermId.ToString())!;
        Money(result["delta"]).ShouldBe("30.00");
        result["daysEarned"]!.GetValue<int>().ShouldBe(30);
        (await EarnedAsync(policy)).ShouldBe(30.00m);
        (await ScalarAsync<decimal>(_db, $"SELECT sum(amount) FROM fin.journal_line WHERE policy_term_id = '{policy.TermId}' AND account_code = 'GL-4110' AND side = 'CREDIT'")).ShouldBe(30.00m);

        // The same request again earns nothing new and posts nothing.
        var (_, again) = await SendAsync(HttpMethod.Post, "/api/fin/v1/earning/run", new { asOf = "2026-11-30" });
        again!["run"]!["journalIds"]!.AsArray().Count.ShouldBe(0);
        (await EarnedAsync(policy)).ShouldBe(30.00m);

        // A later date in the next period posts only the 31 days since.
        var (_, december) = await SendAsync(HttpMethod.Post, "/api/fin/v1/earning/run", new { asOf = "2026-12-31" });
        december!["run"]!["period"]!.GetValue<string>().ShouldBe("2026-12");
        (await EarnedAsync(policy)).ShouldBe(61.00m);

        // The status shows the latest run; an earlier as-of date than the latest run is refused.
        var (_, status) = await SendAsync(HttpMethod.Get, "/api/fin/v1/earning/status");
        status!["run"]!["asOf"]!.GetValue<string>().ShouldBe("2026-12-31");
        (await SendAsync(HttpMethod.Post, "/api/fin/v1/earning/run", new { asOf = "2026-11-30" })).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // At the end of the term every cent of the written premium is earned.
        (await SendAsync(HttpMethod.Post, "/api/fin/v1/earning/run", new { asOf = "2027-10-31" })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await EarnedAsync(policy)).ShouldBe(365.00m);
        (await ScalarAsync<decimal>(_db, $"SELECT sum(delta) FROM fin.earned_premium WHERE policy_term_id = '{policy.TermId}'")).ShouldBe(365.00m);
    }

    [Fact]
    public async Task Run_into_a_locked_period_is_refused_and_posts_nothing()
    {
        var policy = await WrittenTermAsync("2026-11-01", "2027-11-01", ("MTPL", "365.00"));
        await database.ExecuteAsSuperuserAsync(
            $"INSERT INTO fin.financial_period (period_id, legal_entity_id, period_code, status, created_at) VALUES ('{Guid.CreateVersion7()}', '{ApiHostFactory.LegalEntityId}', '2027-02', 'LOCKED', now()) ON CONFLICT (legal_entity_id, period_code) DO UPDATE SET status = 'LOCKED'",
            TestContext.Current.CancellationToken);
        var (response, body) = await SendAsync(HttpMethod.Post, "/api/fin/v1/earning/run", new { asOf = "2027-02-28" });
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, body?.ToJsonString());
        body!["code"]!.GetValue<string>().ShouldBe("FIN-ERR-PERIOD-LOCKED");
        (await EarnedAsync(policy)).ShouldBe(0m);
    }

    [Fact]
    public async Task The_close_task_is_blocked_until_the_period_is_earned_to_its_last_day()
    {
        await database.ExecuteAsSuperuserAsync(
            $"DELETE FROM fin.financial_period WHERE legal_entity_id = '{ApiHostFactory.LegalEntityId}' AND period_code = '2026-09'", TestContext.Current.CancellationToken);
        var (_, tasks) = await SendAsync(HttpMethod.Get, "/api/fin/v1/close/tasks?period=2026-09");
        tasks!["tasks"]!.AsArray().Single(t => t!["task"]!.GetValue<string>() == "EARNING_RUN")!["blocker"].ShouldNotBeNull();
        (await SendAsync(HttpMethod.Post, "/api/fin/v1/earning/run", new { asOf = "2026-09-15" })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (_, midMonth) = await SendAsync(HttpMethod.Get, "/api/fin/v1/close/tasks?period=2026-09");
        midMonth!["tasks"]!.AsArray().Single(t => t!["task"]!.GetValue<string>() == "EARNING_RUN")!["blocker"].ShouldNotBeNull();
        (await SendAsync(HttpMethod.Post, "/api/fin/v1/earning/run", new { asOf = "2026-09-30" })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (_, done) = await SendAsync(HttpMethod.Get, "/api/fin/v1/close/tasks?period=2026-09");
        done!["tasks"]!.AsArray().Single(t => t!["task"]!.GetValue<string>() == "EARNING_RUN")!["blocker"].ShouldBeNull();
    }

    private async Task<string> RowCountsAsync()
    {
        var counts = new List<long>();
        foreach (var table in new[] { "journal_entry", "journal_line", "earning_run", "earned_premium" })
        {
            counts.Add(await ScalarAsync<long>(_db, $"SELECT count(*) FROM fin.{table}"));
        }

        return string.Join(',', counts);
    }

    [Fact]
    public async Task Simulate_writes_nothing_and_matches_what_the_run_then_does()
    {
        var policy = await WrittenTermAsync("2026-11-01", "2027-11-01", ("MTPL", "250.00"), ("OD", "115.00"));
        var before = await RowCountsAsync();
        var (response, sim) = await SendAsync(HttpMethod.Post, "/api/fin/v1/earning/simulate", new { asOf = "2026-11-30" });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, sim?.ToJsonString());
        sim!["run"]!["state"]!.GetValue<string>().ShouldBe("SIMULATED");
        Money(sim["results"]!.AsArray().Single(r => r!["policyTermId"]!.GetValue<string>() == policy.TermId.ToString())!["delta"]).ShouldBe("30.00");
        (await RowCountsAsync()).ShouldBe(before);

        var (_, run) = await SendAsync(HttpMethod.Post, "/api/fin/v1/earning/run", new { asOf = "2026-11-30" });
        Money(run!["results"]!.AsArray().Single(r => r!["policyTermId"]!.GetValue<string>() == policy.TermId.ToString())!["delta"]).ShouldBe("30.00");
    }

    [Fact]
    public async Task Result_pages_every_term_of_a_run_and_the_totals_equal_the_run()
    {
        var first = await WrittenTermAsync("2026-11-01", "2027-11-01", ("MTPL", "250.00"), ("OD", "115.00"));
        var second = await WrittenTermAsync("2026-11-01", "2027-11-01", ("MTPL", "73.00"));
        (await SendAsync(HttpMethod.Get, "/api/fin/v1/earning/result")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var (_, run) = await SendAsync(HttpMethod.Post, "/api/fin/v1/earning/run", new { asOf = "2026-11-30" });
        var runId = run!["run"]!["id"]!.GetValue<string>();
        var total = decimal.Parse(Money(run["run"]!["earnedToDate"]![0]), CultureInfo.InvariantCulture);

        var seen = new List<JsonNode>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var url = $"/api/fin/v1/earning/result?runId={runId}&limit=1" + (cursor is null ? string.Empty : $"&cursor={cursor}");
            var (response, page) = await SendAsync(HttpMethod.Get, url);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, page?.ToJsonString());
            page!["run"]!["id"]!.GetValue<string>().ShouldBe(runId);
            seen.AddRange(page["items"]!.AsArray().Select(i => i!));
            cursor = page["nextCursor"]?.GetValue<string>();
            pages++;
        }
        while (cursor is not null && pages < 500);

        seen.Count.ShouldBe(pages);
        seen.Select(i => i["policyTermId"]!.GetValue<string>()).ShouldContain(first.TermId.ToString());
        seen.Select(i => i["policyTermId"]!.GetValue<string>()).ShouldContain(second.TermId.ToString());
        seen.Select(i => i["policyTermId"]!.GetValue<string>()).Distinct().Count().ShouldBe(seen.Count);
        seen.Sum(i => decimal.Parse(Money(i["earnedToDate"]), CultureInfo.InvariantCulture)).ShouldBe(total);
        run["run"]!["policiesProcessed"]!.GetValue<int>().ShouldBe(seen.Count);

        // Without a run id the latest run answers; an unknown run, a bad limit and a missing permission are refused.
        var (_, latest) = await SendAsync(HttpMethod.Get, "/api/fin/v1/earning/result");
        latest!["run"]!["id"]!.GetValue<string>().ShouldBe(runId);
        (await SendAsync(HttpMethod.Get, $"/api/fin/v1/earning/result?runId={Guid.NewGuid()}")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await SendAsync(HttpMethod.Get, "/api/fin/v1/earning/result?limit=0")).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await SendAsync(HttpMethod.Get, "/api/fin/v1/earning/result", roles: "Staff.Underwriter")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_earning_run_journal_cannot_be_reversed()
    {
        await WrittenTermAsync("2026-11-01", "2027-11-01", ("MTPL", "365.00"));
        var (_, run) = await SendAsync(HttpMethod.Post, "/api/fin/v1/earning/run", new { asOf = "2026-11-30" });
        var journalId = run!["run"]!["journalIds"]![0]!.GetValue<string>();
        var (response, problem) = await SendAsync(HttpMethod.Post, "/api/fin/v1/journals/reverse", new { journalId, reason = "Undo the run" });
        response.StatusCode.ShouldBe((HttpStatusCode)422, problem?.ToJsonString());
        problem!["code"]!.GetValue<string>().ShouldBe("FIN-ERR-SOURCE-OWNED");
        (await ScalarAsync<long>(_db, $"SELECT count(*) FROM fin.journal_entry WHERE reverses_journal_id = '{journalId}'")).ShouldBe(0);
    }
}
