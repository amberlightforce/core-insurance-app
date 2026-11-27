using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Npgsql;

namespace CoreIns.IntegrationTests.Finance;

/// <summary>BO-FIN-01a/01b: manual journal create, list, get, withdraw, submit and decide (maker-checker posting) on a real PostgreSQL through the real Host.</summary>
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

    private async Task<string> DraftAsync(object journal, string user = "maker-1")
    {
        var (created, body) = await SendAsync(HttpMethod.Post, "/api/fin/v1/manual-journals", journal, user: user);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, body?.ToJsonString());
        return body!["manualJournal"]!["id"]!.GetValue<string>();
    }

    private async Task<string> SubmittedAsync(object journal, string user = "maker-1")
    {
        var id = await DraftAsync(journal, user);
        var (submitted, body) = await SendAsync(HttpMethod.Post, "/api/fin/v1/manual-journals/submit", new { id }, user: user);
        submitted.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body!["manualJournal"]!["status"]!.GetValue<string>().ShouldBe("SUBMITTED");
        return id;
    }

    [Fact]
    public async Task Submit_then_a_second_person_approves_and_one_sealed_balanced_journal_posts_exactly_once()
    {
        var id = await SubmittedAsync(Journal(date: "2026-10-16"));
        var before = await CountAsync("fin.journal_entry");

        // Maker is not checker: FIN refuses the maker before the platform is asked.
        var (self, sod) = await SendAsync(HttpMethod.Post, "/api/fin/v1/manual-journals/decide", new { id, decision = "APPROVE" });
        self.StatusCode.ShouldBe(HttpStatusCode.Forbidden, sod?.ToJsonString());
        sod!.ToJsonString().ShouldContain("FIN-ERR-SOD");
        (await CountAsync("fin.journal_entry")).ShouldBe(before);

        var (approved, body) = await SendAsync(HttpMethod.Post, "/api/fin/v1/manual-journals/decide", new { id, decision = "APPROVE" }, user: "checker-1");
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body!["manualJournal"]!["status"]!.GetValue<string>().ShouldBe("POSTED");
        body["manualJournal"]!["decidedBy"]!.GetValue<string>().ShouldContain("checker-1");
        var postedIds = body["postedIds"]!.AsArray();
        postedIds.Count.ShouldBe(1);
        var postedId = postedIds[0]!.GetValue<string>();
        (await CountAsync("fin.journal_entry")).ShouldBe(before + 1);

        // The posted journal is an ordinary sealed MANUAL entry, readable through the journal API, and in the trial balance of its period.
        var (got, journal) = await SendAsync(HttpMethod.Get, $"/api/fin/v1/journals/{postedId}");
        got.StatusCode.ShouldBe(HttpStatusCode.OK, journal?.ToJsonString());
        journal!.ToJsonString().ShouldContain("MANUAL");
        var (queried, page) = await SendAsync(HttpMethod.Get, "/api/fin/v1/journals/query?accountingDateFrom=2026-10-16&accountingDateTo=2026-10-16&limit=200");
        queried.StatusCode.ShouldBe(HttpStatusCode.OK, page?.ToJsonString());
        page!["items"]!.ToJsonString().ShouldContain(postedId);
        var (_, balance) = await SendAsync(HttpMethod.Get, "/api/fin/v1/balances/trial-balance?period=2026-10");
        balance!["balances"]!.AsArray().Any(b => b!["account"]!.GetValue<string>() == "GL-1110").ShouldBeTrue();
        balance["totalDebit"]!.GetValue<string>().ShouldBe(balance["totalCredit"]!.GetValue<string>());

        // Deciding again is a replay: same state, no second journal.
        var (again, replay) = await SendAsync(HttpMethod.Post, "/api/fin/v1/manual-journals/decide", new { id, decision = "APPROVE" }, user: "checker-2");
        again.StatusCode.ShouldBe(HttpStatusCode.OK, replay?.ToJsonString());
        replay!["postedIds"]!.AsArray().Count.ShouldBe(1);
        replay["postedIds"]![0]!.GetValue<string>().ShouldBe(postedId);
        (await CountAsync("fin.journal_entry")).ShouldBe(before + 1);

        // A decided journal cannot be rejected or withdrawn afterwards.
        (await SendAsync(HttpMethod.Post, "/api/fin/v1/manual-journals/decide", new { id, decision = "REJECT", comment = "late" }, user: "checker-1")).Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await SendAsync(HttpMethod.Post, "/api/fin/v1/manual-journals/withdraw", new { id })).Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_checker_below_the_total_is_refused_and_the_admin_holding_the_authority_approves()
    {
        var big = new
        {
            description = "Large reclassification", book = "IFRS17", accountingDate = "2026-10-17", currency = "EUR", evidence = new { reference = "DOC-90" },
            lines = new[] { new { account = "GL-1110", side = "DEBIT", amount = "50000.00" }, new { account = "GL-1210", side = "CREDIT", amount = "50000.00" } },
        };
        var id = await SubmittedAsync(big);
        var before = await CountAsync("fin.journal_entry");

        var (low, problem) = await SendAsync(HttpMethod.Post, "/api/fin/v1/manual-journals/decide", new { id, decision = "APPROVE" }, user: "checker-1");
        low.StatusCode.ShouldBe(HttpStatusCode.Forbidden, problem?.ToJsonString());
        problem!.ToJsonString().ShouldContain("PLT-ERR-AUTHORITY");
        (await CountAsync("fin.journal_entry")).ShouldBe(before);
        (await SendAsync(HttpMethod.Get, $"/api/fin/v1/manual-journals/{id}")).Body!["manualJournal"]!["status"]!.GetValue<string>().ShouldBe("SUBMITTED");

        var (ok, body) = await SendAsync(HttpMethod.Post, "/api/fin/v1/manual-journals/decide", new { id, decision = "APPROVE" }, roles: "Platform.Admin", user: "admin-1");
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body!["manualJournal"]!["status"]!.GetValue<string>().ShouldBe("POSTED");
    }

    [Fact]
    public async Task A_rejection_needs_a_reason_posts_nothing_and_is_final_and_only_the_maker_submits_a_draft_once()
    {
        var id = await DraftAsync(Journal());
        (await SendAsync(HttpMethod.Post, "/api/fin/v1/manual-journals/submit", new { id }, user: "someone-else")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Post, "/api/fin/v1/manual-journals/decide", new { id, decision = "APPROVE" }, user: "checker-1")).Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await SendAsync(HttpMethod.Post, "/api/fin/v1/manual-journals/submit", new { id })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SendAsync(HttpMethod.Post, "/api/fin/v1/manual-journals/submit", new { id })).Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var before = await CountAsync("fin.journal_entry");
        (await SendAsync(HttpMethod.Post, "/api/fin/v1/manual-journals/decide", new { id, decision = "REJECT" }, user: "checker-1")).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var (rejected, body) = await SendAsync(HttpMethod.Post, "/api/fin/v1/manual-journals/decide", new { id, decision = "REJECT", comment = "Wrong account" }, user: "checker-1");
        rejected.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body!["manualJournal"]!["status"]!.GetValue<string>().ShouldBe("REJECTED");
        body["manualJournal"]!["decisionReason"]!.GetValue<string>().ShouldBe("Wrong account");
        body["postedIds"]!.AsArray().Count.ShouldBe(0);
        (await CountAsync("fin.journal_entry")).ShouldBe(before);
        (await SendAsync(HttpMethod.Post, "/api/fin/v1/manual-journals/decide", new { id, decision = "APPROVE" }, user: "checker-2")).Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Approval_into_a_period_locked_after_submit_is_refused_and_posts_nothing_then_the_maker_withdraws()
    {
        var id = await SubmittedAsync(Journal(date: "2025-04-10"));
        await database.ExecuteAsSuperuserAsync(
            $"INSERT INTO fin.financial_period (period_id, legal_entity_id, period_code, status, created_at) VALUES ('{Guid.CreateVersion7()}', '{ApiHostFactory.LegalEntityId}', '2025-04', 'LOCKED', now()) ON CONFLICT (legal_entity_id, period_code) DO UPDATE SET status = 'LOCKED'",
            TestContext.Current.CancellationToken);
        var before = await CountAsync("fin.journal_entry");

        var (locked, problem) = await SendAsync(HttpMethod.Post, "/api/fin/v1/manual-journals/decide", new { id, decision = "APPROVE" }, user: "checker-1");
        locked.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, problem?.ToJsonString());
        problem!.ToJsonString().ShouldContain("FIN-ERR-PERIOD-LOCKED");
        (await CountAsync("fin.journal_entry")).ShouldBe(before);

        var (withdrawn, body) = await SendAsync(HttpMethod.Post, "/api/fin/v1/manual-journals/withdraw", new { id });
        withdrawn.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body!["manualJournal"]!["status"]!.GetValue<string>().ShouldBe("WITHDRAWN");
    }

    private async Task<long> CountAsync(string table)
    {
        await using var source = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var command = source.CreateCommand($"SELECT count(*) FROM {table}");
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}
