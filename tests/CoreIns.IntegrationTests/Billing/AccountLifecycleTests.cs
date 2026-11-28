using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using static CoreIns.IntegrationTests.Bil.BillingSlice;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Bil;

/// <summary>
/// BO-BIL-01: billing account lifecycle: search with cursor paging, suspend/resume, close (refused with open items or a live mandate),
/// reopen, and the payer change (PTY party check, ledger untouched), with audit entries and role checks.
/// </summary>
public sealed class AccountLifecycleTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Manager = "Staff.BillingManager";

    private BillingSlice _slice = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _slice = new BillingSlice(database);
        await _slice.SeedAsync();
    }

    public async ValueTask DisposeAsync() => await _slice.DisposeAsync();

    private const string Api = "/api/bil/v1/billing-accounts";

    /// <summary>A bound and drained policy: its payer's account holds an invoice and unbilled/billed ledger lines.</summary>
    private async Task<(BoundPolicy Policy, string Account)> BusyAccountAsync()
    {
        var policy = await _slice.BindAsync();
        await _slice.DrainAsync();
        var account = await _slice.ScalarAsync<string>($"SELECT billing_account_id::text FROM bil.billing_account WHERE payer_party_id = '{policy.PartyId}'");
        return (policy, account);
    }

    /// <summary>An ACTIVE account without terms, invoices or ledger lines for a new party (same legal entity and jurisdiction as the busy one).</summary>
    private async Task<(string Party, string Account)> EmptyAccountAsync(string template)
    {
        var party = await _slice.Policy.CreatePartyAsync();
        var id = Guid.CreateVersion7();
        await _slice.ScalarAsync<long>($"""
            WITH ins AS (
              INSERT INTO bil.billing_account (billing_account_id, legal_entity_id, jurisdiction, account_number, account_type, payer_party_id, currency, status, created_at, created_by, record_version)
              SELECT '{id}', legal_entity_id, jurisdiction, 'BA-T' || substr(replace('{id}', '-', ''), 1, 12), account_type, '{party}', currency, 'ACTIVE', now(), 'test', 1
              FROM bil.billing_account WHERE billing_account_id = '{template}'
              RETURNING 1)
            SELECT count(*) FROM ins
            """);
        return (party, id.ToString());
    }

    private async Task<(HttpResponseMessage Response, JsonNode? Body)> Rest(HttpMethod method, string path, object? body = null, string roles = Billing) =>
        await SendAsync(_slice.Client, method, path, body, roles);

    private async Task<string> StatusAsync(string account) => await _slice.ScalarAsync<string>($"SELECT status FROM bil.billing_account WHERE billing_account_id = '{account}'");

    /// <summary>The account's ledger as a fingerprint: every ledger account and side with its summed amount.</summary>
    private async Task<string> LedgerNetAsync(string account) => await _slice.ScalarAsync<string>($"""
        SELECT COALESCE(string_agg(account_code || ':' || side || ':' || total::text, ',' ORDER BY account_code, side), '')
        FROM (SELECT account_code, side, sum(amount) AS total FROM bil.ledger_line WHERE billing_account_id = '{account}' GROUP BY account_code, side) s
        """);

    [Fact]
    public async Task BO_BIL_01_close_is_refused_with_an_open_invoice_and_works_on_an_empty_account_then_reopen()
    {
        var (_, busy) = await BusyAccountAsync();
        var (response, problem) = await Rest(HttpMethod.Post, $"{Api}/close", new { billingAccountId = busy, reason = "test" });
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, problem?.ToJsonString());
        problem.Text("code").ShouldBe("BIL-ERR-BALANCE-NOT-ZERO");
        (await StatusAsync(busy)).ShouldBe("ACTIVE");

        var (_, empty) = await EmptyAccountAsync(busy);
        var (closed, body) = await Rest(HttpMethod.Post, $"{Api}/close", new { billingAccountId = empty, reason = "customer left" });
        closed.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("account.status").ShouldBe("CLOSED");
        (await StatusAsync(empty)).ShouldBe("CLOSED");

        // Closing twice, and suspending a closed account, are refused.
        (await Rest(HttpMethod.Post, $"{Api}/close", new { billingAccountId = empty })).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await Rest(HttpMethod.Patch, $"{Api}/{empty}", new { status = "SUSPENDED" })).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var (reopened, again) = await Rest(HttpMethod.Post, $"{Api}/reopen", new { billingAccountId = empty }, Manager);
        reopened.StatusCode.ShouldBe(HttpStatusCode.OK, again?.ToJsonString());
        again.Text("account.status").ShouldBe("ACTIVE");
        (await Rest(HttpMethod.Post, $"{Api}/reopen", new { billingAccountId = empty }, Manager)).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        // Audit entries for the successful commands.
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM plt.audit_event WHERE operation = 'bil.BillingAccount.close' AND outcome = 'Succeeded' AND object_id = '{empty}'")).ShouldBe(1);
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM plt.audit_event WHERE operation = 'bil.BillingAccount.reopen' AND outcome = 'Succeeded' AND object_id = '{empty}'")).ShouldBe(1);
    }

    [Fact]
    public async Task BO_BIL_01_reopen_is_refused_when_the_payer_has_another_active_account()
    {
        var (_, busy) = await BusyAccountAsync();
        var (party, empty) = await EmptyAccountAsync(busy);
        (await Rest(HttpMethod.Post, $"{Api}/close", new { billingAccountId = empty })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (_, second) = await EmptyAccountAsync(busy);
        await _slice.ScalarAsync<long>($"WITH u AS (UPDATE bil.billing_account SET payer_party_id = '{party}' WHERE billing_account_id = '{second}' RETURNING 1) SELECT count(*) FROM u");

        var (response, problem) = await Rest(HttpMethod.Post, $"{Api}/reopen", new { billingAccountId = empty }, Manager);
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, problem?.ToJsonString());
        problem.Text("code").ShouldBe("BIL-ERR-NOT-ELIGIBLE");
        (await StatusAsync(empty)).ShouldBe("CLOSED");
    }

    [Fact]
    public async Task BO_BIL_01_change_payer_keeps_the_ledger_and_balances_and_checks_the_party()
    {
        var (policy, account) = await BusyAccountAsync();
        var (_, before) = await _slice.GetAsync($"{Api}/{account}");
        var netBefore = await LedgerNetAsync(account);
        netBefore.ShouldNotBeEmpty();
        var lines = await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.ledger_line WHERE billing_account_id = '{account}'");

        var newPayer = await _slice.Policy.CreatePartyAsync();
        var (response, body) = await Rest(HttpMethod.Post, $"{Api}/change-payer", new { billingAccountId = account, newPayerPartyId = newPayer, reason = "policyholder moved the debt" }, Manager);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("account.payerPartyId").ShouldBe(newPayer);
        body.Text("previousPayerPartyId").ShouldBe(policy.PartyId);

        var (_, after) = await _slice.GetAsync($"{Api}/{account}");
        after!["balancesByState"]!.ToJsonString().ShouldBe(before!["balancesByState"]!.ToJsonString());
        (await LedgerNetAsync(account)).ShouldBe(netBefore);
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.ledger_line WHERE billing_account_id = '{account}'")).ShouldBe(lines);
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM plt.audit_event WHERE operation = 'bil.BillingAccount.changePayer' AND outcome = 'Succeeded' AND object_id = '{account}'")).ShouldBe(1);

        // Same payer, unknown party, future date and a missing reason are refused.
        (await Rest(HttpMethod.Post, $"{Api}/change-payer", new { billingAccountId = account, newPayerPartyId = newPayer, reason = "again" }, Manager))
            .Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var (unknown, problem) = await Rest(HttpMethod.Post, $"{Api}/change-payer", new { billingAccountId = account, newPayerPartyId = Guid.NewGuid(), reason = "x" }, Manager);
        unknown.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, problem?.ToJsonString());
        problem.Text("code").ShouldBe("BIL-ERR-PAYER-ROLE");
        (await _slice.ScalarAsync<string>($"SELECT payer_party_id::text FROM bil.billing_account WHERE billing_account_id = '{account}'")).ShouldBe(newPayer);
        var future = DateTime.UtcNow.AddDays(10).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        (await Rest(HttpMethod.Post, $"{Api}/change-payer", new { billingAccountId = account, newPayerPartyId = await _slice.Policy.CreatePartyAsync(), reason = "x", effectiveDate = future }, Manager))
            .Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Rest(HttpMethod.Post, $"{Api}/change-payer", new { billingAccountId = account, newPayerPartyId = await _slice.Policy.CreatePartyAsync() }, Manager))
            .Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task BO_BIL_01_a_live_mandate_blocks_close_and_change_payer_until_it_is_cancelled()
    {
        var (_, busy) = await BusyAccountAsync();
        var (party, account) = await EmptyAccountAsync(busy);
        var iban = NewIban();
        var (added, instrument) = await _slice.PostAsync("/api/bil/v1/payment-instruments/add", new { partyId = party, iban, holder = "Μαρία Παπαδοπούλου" });
        added.StatusCode.ShouldBe(HttpStatusCode.OK, instrument?.ToJsonString());
        var (created, mandate) = await _slice.PostAsync("/api/bil/v1/mandates", new { billingAccountId = account, instrumentId = instrument.Text("instrument.instrumentId") });
        created.StatusCode.ShouldBe(HttpStatusCode.Created, mandate?.ToJsonString());

        var (closeResponse, closeProblem) = await Rest(HttpMethod.Post, $"{Api}/close", new { billingAccountId = account });
        closeResponse.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        closeProblem.Text("code").ShouldBe("BIL-ERR-MANDATE-STATE");
        var (payerResponse, payerProblem) = await Rest(HttpMethod.Post, $"{Api}/change-payer", new { billingAccountId = account, newPayerPartyId = await _slice.Policy.CreatePartyAsync(), reason = "x" }, Manager);
        payerResponse.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        payerProblem.Text("code").ShouldBe("BIL-ERR-MANDATE-STATE");

        var (cancelled, _) = await _slice.PostAsync("/api/bil/v1/mandates/cancel", new { mandateId = mandate!["mandate"]!["mandateId"]!.GetValue<string>() });
        cancelled.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Rest(HttpMethod.Post, $"{Api}/close", new { billingAccountId = account })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task BO_BIL_01_update_suspends_and_resumes_and_a_suspended_account_takes_no_payment()
    {
        var (_, account) = await BusyAccountAsync();
        var (suspended, body) = await Rest(HttpMethod.Patch, $"{Api}/{account}", new { status = "SUSPENDED", reason = "dispute" });
        suspended.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("account.status").ShouldBe("SUSPENDED");
        (await StatusAsync(account)).ShouldBe("SUSPENDED");

        var (paid, problem) = await _slice.PostAsync("/api/bil/v1/payments/take", new
        {
            billingAccountId = account,
            amount = new { amount = "1.00", currency = "EUR" },
            method = "BANK_TRANSFER",
        });
        paid.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, problem?.ToJsonString());

        var (resumed, again) = await Rest(HttpMethod.Patch, $"{Api}/{account}", new { status = "ACTIVE" });
        resumed.StatusCode.ShouldBe(HttpStatusCode.OK, again?.ToJsonString());
        again.Text("account.status").ShouldBe("ACTIVE");
        (await Rest(HttpMethod.Patch, $"{Api}/{account}", new { status = "ACTIVE" })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task BO_BIL_01_search_filters_and_pages_by_cursor()
    {
        var (policy, busy) = await BusyAccountAsync();
        var (_, extra) = await EmptyAccountAsync(busy);
        var (_, extra2) = await EmptyAccountAsync(busy);

        var (response, page) = await _slice.GetAsync($"{Api}/search?payerPartyId={policy.PartyId}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, page?.ToJsonString());
        page!["items"]!.AsArray().Count.ShouldBe(1);
        page.Text("items.0.account.billingAccountId").ShouldBe(busy);
        page.Text("items.0.account.status").ShouldBe("ACTIVE");

        var number = await _slice.ScalarAsync<string>($"SELECT account_number FROM bil.billing_account WHERE billing_account_id = '{extra}'");
        var (byNumber, found) = await _slice.GetAsync($"{Api}/search?accountNumber={number}");
        byNumber.StatusCode.ShouldBe(HttpStatusCode.OK);
        found!["items"]!.AsArray().Count.ShouldBe(1);
        found.Text("items.0.account.billingAccountId").ShouldBe(extra);

        // Cursor paging over everything ACTIVE: one at a time, no id twice, the three accounts all seen.
        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var (_, next) = await _slice.GetAsync($"{Api}/search?status=ACTIVE&limit=1" + (cursor is null ? string.Empty : "&cursor=" + cursor));
            var items = next!["items"]!.AsArray();
            items.Count.ShouldBeLessThanOrEqualTo(1);
            seen.AddRange(items.Select(i => i!["account"]!["billingAccountId"]!.GetValue<string>()));
            cursor = next["nextCursor"]?.GetValue<string>();
        }
        while (cursor is not null);

        seen.Distinct().Count().ShouldBe(seen.Count);
        seen.ShouldContain(busy);
        seen.ShouldContain(extra);
        seen.ShouldContain(extra2);

        (await _slice.GetAsync($"{Api}/search?status=NOPE")).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _slice.GetAsync($"{Api}/search?cursor=zzz")).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task BO_BIL_01_roles_and_missing_accounts()
    {
        var (_, account) = await BusyAccountAsync();
        const string Claims = "Staff.ClaimsHandler";
        (await Rest(HttpMethod.Get, $"{Api}/search", roles: Claims)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await Rest(HttpMethod.Patch, $"{Api}/{account}", new { status = "SUSPENDED" }, Claims)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await Rest(HttpMethod.Post, $"{Api}/close", new { billingAccountId = account }, Claims)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        // Billing staff may not decide a payer change or a reopen; those are manager decisions.
        (await Rest(HttpMethod.Post, $"{Api}/change-payer", new { billingAccountId = account, newPayerPartyId = Guid.NewGuid(), reason = "x" })).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await Rest(HttpMethod.Post, $"{Api}/reopen", new { billingAccountId = account })).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var missing = Guid.NewGuid();
        (await Rest(HttpMethod.Patch, $"{Api}/{missing}", new { status = "SUSPENDED" })).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Rest(HttpMethod.Patch, $"{Api}/not-a-guid", new { status = "SUSPENDED" })).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Rest(HttpMethod.Post, $"{Api}/close", new { billingAccountId = missing })).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Rest(HttpMethod.Post, $"{Api}/reopen", new { billingAccountId = missing }, Manager)).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Rest(HttpMethod.Post, $"{Api}/change-payer", new { billingAccountId = missing, newPayerPartyId = Guid.NewGuid(), reason = "x" }, Manager)).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>A valid synthetic Greek IBAN (ISO 13616 check digits computed), unique per call.</summary>
    private static string NewIban()
    {
        var bban = string.Concat(Guid.NewGuid().ToByteArray().Select(b => (b % 10).ToString(CultureInfo.InvariantCulture))).PadRight(23, '7')[..23];
        var remainder = 0;
        foreach (var c in bban + "GR00")
        {
            var value = char.IsAsciiDigit(c) ? c - '0' : c - 'A' + 10;
            remainder = value >= 10 ? ((remainder * 100) + value) % 97 : ((remainder * 10) + value) % 97;
        }

        return "GR" + (98 - remainder).ToString("00", CultureInfo.InvariantCulture) + bban;
    }
}
