using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Migration;

/// <summary>
/// BO-MIG-11: the wave sign-off. A sign-off binds to the latest reconciliation run of a scope and its result rows; the wave may close only when the run is
/// green (or its variances are waived) and the finance controller, compliance officer and migration lead each approved. Amounts are illustrative.
/// </summary>
public sealed class WaveSignOffTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Admin = "Platform.Admin,Staff.MigrationManager";
    private const string Finance = "Staff.Finance";
    private const string Compliance = "Staff.ComplianceOfficer";
    private const string Lead = "Staff.MigrationManager";
    private ApiHostFactory _factory = null!;
    private HttpClient _client = null!;

    private static readonly string[] UnknownKey = ["BIL/Nope//EUR"];

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

    [Fact]
    public async Task The_wave_closes_only_when_the_run_is_green_and_each_named_signatory_approved()
    {
        var scope = await GreenRunAsync(cents: "350.35");
        var status = await GetStatusAsync(scope);
        status["ready"]!.GetValue<bool>().ShouldBeFalse();
        Blockers(status).ShouldContain("NO_SIGNOFF");

        var (requested, requestBody) = await SendAsync(HttpMethod.Post, "/api/mig/v1/signoff/request", new { scope }, "carol", Lead);
        requested.StatusCode.ShouldBe(HttpStatusCode.OK, requestBody?.ToJsonString());
        var signOff = requestBody.Text("signoffId");
        Blockers(requestBody).Order().ShouldBe(["SIGNATORY_PENDING:COMPLIANCE_OFFICER", "SIGNATORY_PENDING:FINANCE_CONTROLLER", "SIGNATORY_PENDING:MIGRATION_LEAD"]);

        // A missing signatory blocks the close.
        ShouldBeOk(await DecideAsync("approve", signOff, "MIGRATION_LEAD", "bob", Lead));
        var afterTwo = (await DecideAsync("approve", signOff, "FINANCE_CONTROLLER", "dave", Finance)).Body!;
        afterTwo["ready"]!.GetValue<bool>().ShouldBeFalse();
        Blockers(afterTwo).ShouldBe(["SIGNATORY_PENDING:COMPLIANCE_OFFICER"]);

        var done = (await DecideAsync("approve", signOff, "COMPLIANCE_OFFICER", "erin", Compliance)).Body!;
        done["ready"]!.GetValue<bool>().ShouldBeTrue(done.ToJsonString());
        (await GetStatusAsync(scope))["ready"]!.GetValue<bool>().ShouldBeTrue();

        // A signatory decides once; a rejection blocks until a new sign-off is requested.
        (await DecideAsync("approve", signOff, "COMPLIANCE_OFFICER", "frank", Compliance)).Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_rerun_after_an_approval_invalidates_it_and_a_rejection_blocks()
    {
        var scope = await GreenRunAsync(cents: "350.35");
        var signOff = (await SendAsync(HttpMethod.Post, "/api/mig/v1/signoff/request", new { scope }, "carol", Lead)).Body.Text("signoffId");
        ShouldBeOk(await DecideAsync("approve", signOff, "MIGRATION_LEAD", "bob", Lead));

        // A newer run of the scope: the earlier approvals no longer count and deciding on them is refused.
        await RunAsync(scope);
        var stale = await GetStatusAsync(scope);
        stale["ready"]!.GetValue<bool>().ShouldBeFalse();
        Blockers(stale).ShouldContain("NEWER_RUN");
        var refused = await DecideAsync("approve", signOff, "FINANCE_CONTROLLER", "dave", Finance);
        refused.Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        refused.Body!.ToJsonString().ShouldContain("SIGNOFF-STALE");

        // A fresh request covers the new run, with every signatory pending again; a rejection blocks.
        var again = (await SendAsync(HttpMethod.Post, "/api/mig/v1/signoff/request", new { scope }, "carol", Lead)).Body!;
        again.Text("signoffId").ShouldNotBe(signOff);
        Blockers(again).Length.ShouldBe(3);
        var rejected = (await DecideAsync("reject", again.Text("signoffId"), "FINANCE_CONTROLLER", "dave", Finance)).Body!;
        rejected["ready"]!.GetValue<bool>().ShouldBeFalse();
        Blockers(rejected).ShouldContain("SIGNATORY_REJECTED:FINANCE_CONTROLLER");
    }

    [Fact]
    public async Task The_person_who_ran_the_reconciliation_or_loaded_totals_cannot_sign_and_nobody_signs_twice()
    {
        var scope = await GreenRunAsync(cents: "350.35");
        var (requested, requestBody) = await SendAsync(HttpMethod.Post, "/api/mig/v1/signoff/request", new { scope }, "carol", Lead + "," + Finance);
        requested.StatusCode.ShouldBe(HttpStatusCode.OK, requestBody?.ToJsonString());
        var signOff = requestBody.Text("signoffId");

        // alice loaded the totals and ran the reconciliation, even holding a signatory role.
        var ran = await DecideAsync("approve", signOff, "FINANCE_CONTROLLER", "alice", Finance);
        ran.Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        ran.Body!.ToJsonString().ShouldContain("SIGNOFF-SOD");

        // The requester cannot approve their own request (platform maker-checker).
        (await DecideAsync("approve", signOff, "FINANCE_CONTROLLER", "carol", Finance)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // One person signs for one signatory only.
        ShouldBeOk(await DecideAsync("approve", signOff, "MIGRATION_LEAD", "bob", Lead + "," + Finance));
        var twice = await DecideAsync("approve", signOff, "FINANCE_CONTROLLER", "bob", Lead + "," + Finance);
        twice.Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        twice.Body!.ToJsonString().ShouldContain("SIGNOFF-SOD");

        // A signatory without the matching authority is refused by the platform.
        (await DecideAsync("approve", signOff, "COMPLIANCE_OFFICER", "gina", Finance)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Variances_must_be_waived_with_a_reason_before_sign_off_and_a_waiver_binds_to_its_run()
    {
        var scope = await GreenRunAsync(cents: "350.36");
        var run = await GetResultsAsync(scope);
        var key = run["variances"]!.AsArray().Single()!.GetValue<string>();
        key.ShouldBe("BIL/Receivable//EUR");

        // Not green: no sign-off while the variance stands.
        var blocked = await SendAsync(HttpMethod.Post, "/api/mig/v1/signoff/request", new { scope }, "carol", Lead);
        blocked.Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        Blockers(await GetStatusAsync(scope)).ShouldContain("UNWAIVED_VARIANCES");

        // The runner cannot waive; a reason and a real variance key of the run are required.
        var runId = run.Text("runId");
        (await SendAsync(HttpMethod.Post, "/api/mig/v1/signoff/waive", new { runId, varianceKeys = new[] { key }, reason = "rounding" }, "alice", Admin + "," + Finance))
            .Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Post, "/api/mig/v1/signoff/waive", new { runId, varianceKeys = new[] { key }, reason = " " }, "dave", Finance)).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await SendAsync(HttpMethod.Post, "/api/mig/v1/signoff/waive", new { runId, varianceKeys = UnknownKey, reason = "x" }, "dave", Finance)).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var (waived, waivedBody) = await SendAsync(HttpMethod.Post, "/api/mig/v1/signoff/waive", new { runId, varianceKeys = new[] { key }, reason = "legacy rounding, agreed with finance" }, "dave", Finance);
        waived.StatusCode.ShouldBe(HttpStatusCode.OK, waivedBody?.ToJsonString());

        var (requested, requestBody) = await SendAsync(HttpMethod.Post, "/api/mig/v1/signoff/request", new { scope }, "carol", Lead);
        requested.StatusCode.ShouldBe(HttpStatusCode.OK, requestBody?.ToJsonString());
        requestBody!["waivedVariances"]!.AsArray().Select(v => v!.GetValue<string>()).ToArray().ShouldBe([key]);
        var signOff = requestBody.Text("signoffId");
        ShouldBeOk(await DecideAsync("approve", signOff, "MIGRATION_LEAD", "bob", Lead));
        ShouldBeOk(await DecideAsync("approve", signOff, "FINANCE_CONTROLLER", "dave", Finance));
        (await DecideAsync("approve", signOff, "COMPLIANCE_OFFICER", "erin", Compliance)).Body!["ready"]!.GetValue<bool>().ShouldBeTrue();

        // A newer run needs its own waiver: the old one does not carry over.
        await RunAsync(scope);
        var rerun = await GetStatusAsync(scope);
        rerun["ready"]!.GetValue<bool>().ShouldBeFalse();
        Blockers(rerun).ShouldContain("UNWAIVED_VARIANCES");
        Blockers(rerun).ShouldContain("NEWER_RUN");
    }

    private static void ShouldBeOk((HttpResponseMessage Response, JsonNode? Body) r) => r.Response.StatusCode.ShouldBe(HttpStatusCode.OK, r.Body?.ToJsonString());

    private static string[] Blockers(JsonNode? status) => [.. status!["blockers"]!.AsArray().Select(b => b!.GetValue<string>())];

    private async Task<JsonNode> GetStatusAsync(string scope)
    {
        var (response, body) = await SendAsync(HttpMethod.Get, $"/api/mig/v1/signoff/status?scope={scope}", null, "alice", Admin);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body!;
    }

    private async Task<JsonNode> GetResultsAsync(string scope) =>
        (await SendAsync(HttpMethod.Get, $"/api/mig/v1/reconciliation/results?scope={scope}", null, "alice", Admin)).Body!;

    private Task<(HttpResponseMessage Response, JsonNode? Body)> DecideAsync(string verb, string signOff, string signatory, string user, string roles) =>
        SendAsync(HttpMethod.Post, $"/api/mig/v1/signoff/{verb}", new { signoffId = signOff, signatory, comment = "checked" }, user, roles);

    private async Task RunAsync(string scope)
    {
        var (response, body) = await SendAsync(HttpMethod.Post, "/api/mig/v1/reconciliation/run", new { scope, batchId = _batch }, "alice", Admin);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
    }

    private string _batch = string.Empty;

    /// <summary>Imports a batch (receivables 350.35, ledger 350.35), loads the legacy totals (BIL receivable = <paramref name="cents"/>) and runs the reconciliation as alice.</summary>
    private async Task<string> GreenRunAsync(string cents)
    {
        _batch = await ImportAsync();
        var scope = "signoff-" + Guid.NewGuid().ToString("N");
        var totals = new[]
        {
            Total("BIL", "Receivable", "", 2, cents), Total("FIN", "OpeningBalance", "", 1, "350.35"), Total("FIN", "OpeningBalance", "GL-1210", 1, "350.35"),
            Total("RI", "TreatyYear", "", 1, "1200.00"),
        };
        var (loaded, loadBody) = await SendAsync(HttpMethod.Post, "/api/mig/v1/reconciliation/control-totals/load", new { scope, totals }, "alice", Admin);
        loaded.StatusCode.ShouldBe(HttpStatusCode.OK, loadBody?.ToJsonString());
        await RunAsync(scope);
        return scope;
    }

    private static object Total(string module, string objectType, string key, long count, string amount) => new { module, objectType, key, currency = "EUR", count, amount };

    private async Task<string> ImportAsync()
    {
        var id = Guid.NewGuid().ToString("N");
        var payer = Guid.NewGuid();
        var opening = $"TB-{id}";
        var items = new object[]
        {
            Receivable($"INV-{id}-1", payer, 250.35m),
            Receivable($"INV-{id}-2", payer, 100.00m),
            new
            {
                module = "FIN", objectType = "OpeningBalance", legacyId = opening,
                payload = new
                {
                    legacyId = opening, accountingDate = "2026-09-30", currency = "EUR",
                    lines = new object[] { new { accountCode = "GL-1210", debit = 350.35m, credit = 0m }, new { accountCode = "GL-2110", debit = 0m, credit = 350.35m } },
                    trialBalance = new { debit = 350.35m, credit = 350.35m },
                },
            },
            new { module = "RI", objectType = "TreatyYear", legacyId = $"TY-{id}", payload = new { treatyId = $"XOL-{id}", contractYear = 2026, openingAggregate = 1200.00m } },
        };
        var (created, body) = await SendAsync(HttpMethod.Post, "/api/mig/v1/batches", new { name = "signoff", items }, "alice", Admin);
        created.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        var batch = body.Text("batch.batchId");
        (await SendAsync(HttpMethod.Post, "/api/mig/v1/batches/start", new { batchId = batch, dryRun = true }, "alice", Admin)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SendAsync(HttpMethod.Post, "/api/mig/v1/batches/start", new { batchId = batch, dryRun = false }, "alice", Admin)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (approved, done) = await SendAsync(HttpMethod.Post, "/api/mig/v1/batches/approve", new { batchId = batch, decision = "APPROVE" }, "bob", Admin);
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, done?.ToJsonString());
        return batch;
    }

    private static object Receivable(string invoice, Guid payer, decimal outstanding) => new
    {
        module = "BIL", objectType = "Receivable", legacyId = invoice,
        payload = new
        {
            legacyInvoiceNumber = invoice, payerPartyId = payer, policyLegacyNumber = "LEG-POL-1", dueDate = "2026-09-01", originalAmount = outstanding + 10m,
            outstanding, openItems = new[] { new { amount = outstanding, description = "premium instalment" } },
        },
    };

    private async Task<(HttpResponseMessage Response, JsonNode? Body)> SendAsync(HttpMethod method, string path, object? body, string user, string roles)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add(TestAuthHandler.RolesHeader, roles);
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

        var response = await _client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response, text.Length == 0 ? null : JsonNode.Parse(text));
    }
}
