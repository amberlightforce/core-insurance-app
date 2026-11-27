using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Npgsql;

namespace CoreIns.IntegrationTests.Finance;

/// <summary>BO-FIN-01a: manual journal create, list, get and withdraw on a real PostgreSQL through the real Host.</summary>
public sealed class ManualJournalTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Roles = "Staff.Finance";
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

    private async Task<(HttpResponseMessage Response, JsonNode? Body)> SendAsync(HttpMethod method, string path, object? body = null, string roles = Roles, string user = "maker-1")
    {
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

    private static object Journal(string amountCredit = "120.00", string date = "2026-10-15", string? evidence = "DOC-77", string debitAccount = "GL-1110", string creditAccount = "GL-1210") => new
    {
        description = "Correction of commissions",
        book = "IFRS17",
        accountingDate = date,
        currency = "EUR",
        evidence = new { reference = evidence },
        lines = new[]
        {
            new { account = debitAccount, side = "DEBIT", amount = "120.00" },
            new { account = creditAccount, side = "CREDIT", amount = amountCredit },
        },
    };

    [Fact]
    public async Task A_balanced_evidenced_journal_is_created_as_a_draft_read_back_and_posts_nothing()
    {
        var before = await CountAsync("fin.journal_entry");
        var (created, body) = await SendAsync(HttpMethod.Post, "/api/fin/v1/manual-journals", Journal());
        created.StatusCode.ShouldBe(HttpStatusCode.Created, body?.ToJsonString());
        var journal = body!["manualJournal"]!;
        journal["status"]!.GetValue<string>().ShouldBe("DRAFT");
        journal["number"]!.GetValue<string>().ShouldStartWith("MJ");
        journal["lines"]!.AsArray().Count.ShouldBe(2);
        var id = journal["id"]!.GetValue<string>();

        var (got, read) = await SendAsync(HttpMethod.Get, $"/api/fin/v1/manual-journals/{id}");
        got.StatusCode.ShouldBe(HttpStatusCode.OK);
        read!["manualJournal"]!["evidenceRef"]!.GetValue<string>().ShouldBe("DOC-77");
        read["manualJournal"]!["lines"]![0]!["amount"]!.GetValue<string>().ShouldBe("120.00");
        (await CountAsync("fin.journal_entry")).ShouldBe(before);
    }

    [Fact]
    public async Task An_unbalanced_journal_and_a_journal_without_evidence_are_refused()
    {
        var (unbalanced, problem) = await SendAsync(HttpMethod.Post, "/api/fin/v1/manual-journals", Journal(amountCredit: "100.00"));
        unbalanced.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, problem?.ToJsonString());
        problem!.ToJsonString().ShouldContain("FIN-ERR-UNBALANCED");

        var (noEvidence, missing) = await SendAsync(HttpMethod.Post, "/api/fin/v1/manual-journals", Journal(evidence: " "));
        noEvidence.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        missing!.ToJsonString().ShouldContain("FIN-ERR-EVIDENCE-REQUIRED");
    }

    [Fact]
    public async Task An_unknown_or_system_only_account_and_a_locked_period_are_refused()
    {
        var (unknown, a) = await SendAsync(HttpMethod.Post, "/api/fin/v1/manual-journals", Journal(creditAccount: "GL-9999"));
        unknown.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, a?.ToJsonString());
        a!.ToJsonString().ShouldContain("FIN-ERR-TEMPLATE-ACCOUNT");

        var (systemOnly, b) = await SendAsync(HttpMethod.Post, "/api/fin/v1/manual-journals", Journal(creditAccount: "GL-2110"));
        systemOnly.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        b!.ToJsonString().ShouldContain("FIN-ERR-TEMPLATE-ACCOUNT");

        await database.ExecuteAsSuperuserAsync(
            $"INSERT INTO fin.financial_period (period_id, legal_entity_id, period_code, status, created_at) VALUES ('{Guid.CreateVersion7()}', '{ApiHostFactory.LegalEntityId}', '2025-03', 'LOCKED', now()) ON CONFLICT (legal_entity_id, period_code) DO UPDATE SET status = 'LOCKED'",
            TestContext.Current.CancellationToken);
        var (locked, c) = await SendAsync(HttpMethod.Post, "/api/fin/v1/manual-journals", Journal(date: "2025-03-10"));
        locked.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, c?.ToJsonString());
        c!.ToJsonString().ShouldContain("FIN-ERR-PERIOD-LOCKED");
    }

    [Fact]
    public async Task The_list_pages_newest_first_and_withdraw_is_for_the_maker_only_and_once()
    {
        var ids = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var (created, body) = await SendAsync(HttpMethod.Post, "/api/fin/v1/manual-journals", Journal());
            created.StatusCode.ShouldBe(HttpStatusCode.Created);
            ids.Add(body!["manualJournal"]!["id"]!.GetValue<string>());
        }

        var (_, first) = await SendAsync(HttpMethod.Get, "/api/fin/v1/manual-journals?limit=2");
        first!["items"]!.AsArray().Count.ShouldBe(2);
        first["items"]![0]!["manualJournal"]!["id"]!.GetValue<string>().ShouldBe(ids[2]);
        var cursor = first["nextCursor"]!.GetValue<string>();
        var (_, second) = await SendAsync(HttpMethod.Get, $"/api/fin/v1/manual-journals?limit=2&cursor={Uri.EscapeDataString(cursor)}");
        second!["items"]!.AsArray().Select(i => i!["manualJournal"]!["id"]!.GetValue<string>()).ShouldContain(ids[0]);

        var (other, sod) = await SendAsync(HttpMethod.Post, "/api/fin/v1/manual-journals/withdraw", new { id = ids[0] }, user: "someone-else");
        other.StatusCode.ShouldBe(HttpStatusCode.Forbidden, sod?.ToJsonString());

        var (withdrawn, done) = await SendAsync(HttpMethod.Post, "/api/fin/v1/manual-journals/withdraw", new { id = ids[0] });
        withdrawn.StatusCode.ShouldBe(HttpStatusCode.OK, done?.ToJsonString());
        done!["manualJournal"]!["status"]!.GetValue<string>().ShouldBe("WITHDRAWN");

        var (again, state) = await SendAsync(HttpMethod.Post, "/api/fin/v1/manual-journals/withdraw", new { id = ids[0] });
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict, state?.ToJsonString());

        var (filtered, drafts) = await SendAsync(HttpMethod.Get, "/api/fin/v1/manual-journals?status=WITHDRAWN&limit=50");
        filtered.StatusCode.ShouldBe(HttpStatusCode.OK);
        drafts!["items"]!.AsArray().Select(i => i!["manualJournal"]!["id"]!.GetValue<string>()).ShouldContain(ids[0]);
    }

    [Fact]
    public async Task A_role_without_the_permission_gets_403_and_a_withdrawn_row_cannot_be_reopened_in_the_database()
    {
        var (denied, _) = await SendAsync(HttpMethod.Post, "/api/fin/v1/manual-journals", Journal(), roles: "Staff.Underwriter");
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Get, "/api/fin/v1/manual-journals", roles: "Staff.Underwriter")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var (_, body) = await SendAsync(HttpMethod.Post, "/api/fin/v1/manual-journals", Journal());
        var id = body!["manualJournal"]!["id"]!.GetValue<string>();
        await SendAsync(HttpMethod.Post, "/api/fin/v1/manual-journals/withdraw", new { id });
        // The status guard trigger holds even for the superuser: a final journal never moves again (PITFALLS 47).
        var ex = await Should.ThrowAsync<PostgresException>(() => database.ExecuteAsSuperuserAsync(
            $"UPDATE fin.manual_journal SET status = 'APPROVED' WHERE manual_journal_id = '{id}'", TestContext.Current.CancellationToken));
        ex.SqlState.ShouldBe("55000");
    }

    private async Task<long> CountAsync(string table)
    {
        await using var source = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var command = source.CreateCommand($"SELECT count(*) FROM {table}");
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}
