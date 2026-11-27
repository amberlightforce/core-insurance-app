using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CoreIns.IntegrationTests.Claims;
using CoreIns.Modules.Billing.Commands;
using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Billing.Persistence;
using CoreIns.Modules.Billing.Queries;
using CoreIns.Modules.Billing.Services;
using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Platform.Context;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace CoreIns.IntegrationTests.Bil;

/// <summary>
/// BO-BIL-14: levy tracking per levy authority and month (collected net of credits, payable, requested, approved, paid, outstanding),
/// the payment request created from the payable (BIL_LEVY disbursement, second approver, one payable per authority and month) and the
/// refusal of levy that is not legally settled in Production (D-USR-19).
/// </summary>
public sealed class LevyPaymentTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Billing = "Staff.Billing";
    private const string EmptyJson = "{}";
    private const string Manager = "Staff.BillingManager";
    private DisbursementSlice _slice = null!;
    private string _period = string.Empty;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _slice = new DisbursementSlice(database);
        _period = await _slice.ScalarAsync<string>("SELECT to_char(current_date, 'YYYY-MM')");
    }

    public async ValueTask DisposeAsync() => await _slice.DisposeAsync();

    private static decimal Amt(JsonNode? node, string path) => decimal.Parse(node.Text(path), CultureInfo.InvariantCulture);

    private static string NewLevyType() => "LEVY_" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();

    /// <summary>
    /// A balanced WRITTEN (levy credited, LA-01 debited) or CREDIT_WRITTEN (levy debited) entry of one levy type with its charge, written as
    /// the owner with foreign keys relaxed (the levy tracking reads only the ledger lines and the charge's legal status).
    /// </summary>
    private async Task SeedAsync(string levyType, decimal amount, string legalStatus = "Settled", bool credit = false, bool provisional = false)
    {
        var entry = Guid.CreateVersion7();
        var charge = Guid.CreateVersion7();
        var legalEntity = ApiHostFactory.LegalEntityId;
        var (leviedSide, otherSide) = credit ? ("DEBIT", "CREDIT") : ("CREDIT", "DEBIT");
        var sql = $"""
            SET LOCAL session_replication_role = replica;
            INSERT INTO bil.charge (charge_id, legal_entity_id, set_id, set_size, set_index, policy_id, term_id, transaction_id, element_locator, coverage_code,
                charge_type, charge_category, delta_kind, amount, currency, valid_from, booking_date, correlation_key, source_event_id, source_sequence, received_at,
                status, legal_status, provisional)
            VALUES ('{charge}', '{legalEntity}', '{Guid.CreateVersion7()}', 1, 1, '{Guid.CreateVersion7()}', '{Guid.CreateVersion7()}', '{Guid.CreateVersion7()}', 'loc', 'COV',
                '{levyType}', 'LEVY', 'ADD', {(credit ? -amount : amount).ToString(CultureInfo.InvariantCulture)}, 'EUR', current_date, current_date, '{charge}', '{Guid.CreateVersion7()}', 1, now(),
                'WRITTEN', '{legalStatus}', {(provisional ? "true" : "false")});
            INSERT INTO bil.ledger_entry (entry_id, legal_entity_id, jurisdiction, billing_account_id, entry_type, accounting_date, business_date, recorded_at, cause_operation, correlation_id, lineage_keys)
            VALUES ('{entry}', '{legalEntity}', 'GR', '{Guid.CreateVersion7()}', '{(credit ? "CREDIT_WRITTEN" : "WRITTEN")}', current_date, current_date, now(), 'test', 'x', '{EmptyJson}');
            INSERT INTO bil.ledger_line (line_id, entry_id, line_no, account_code, side, amount, currency, rule_id, legal_entity_id, charge_id, charge_type, charge_category)
            VALUES ('{Guid.CreateVersion7()}', '{entry}', 1, 'LA-07', '{leviedSide}', {amount.ToString(CultureInfo.InvariantCulture)}, 'EUR', 'BLR-WRITTEN-LEVY', '{legalEntity}', '{charge}', '{levyType}', 'LEVY'),
                   ('{Guid.CreateVersion7()}', '{entry}', 2, 'LA-01', '{otherSide}', {amount.ToString(CultureInfo.InvariantCulture)}, 'EUR', 'BLR-WRITTEN-LEVY', '{legalEntity}', '{charge}', '{levyType}', 'LEVY');
            """;
        await using var connection = await _slice.DataSource.OpenConnectionAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            await command.ExecuteNonQueryAsync(Ct);
        }

        await transaction.CommitAsync(Ct);
    }

    private static async Task<(HttpResponseMessage Response, JsonNode? Body)> SendAsync(HttpClient client, HttpMethod method, string path, object? body, string roles, string user)
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

        var response = await client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    private Task<(HttpResponseMessage Response, JsonNode? Body)> Rest(HttpMethod method, string path, object? body = null, string roles = Billing, string user = "maker") =>
        SendAsync(_slice.Client, method, path, body, roles, user);

    private async Task<(PartyId Party, Guid Account)> LevyPayeeAsync(PayeeAccountCreateRequest.PurposeValue purpose = PayeeAccountCreateRequest.PurposeValue.Levy)
    {
        var party = PartyId.New();
        await using var scope = _slice.Scope(Billing);
        var account = await scope.ServiceProvider.GetRequiredService<IBillingPayeeAccountService>().CreateAsync(
            new PayeeAccountCreateRequest { PartyId = party, Purpose = purpose, Iban = DisbursementSlice.NewIban(), HolderName = "Ταμείο Εισφορών" },
            CoreIns.Platform.Contracts.CommandOptions.New(), Ct);
        return (party, account.PayeeAccountId);
    }

    private Task<(HttpResponseMessage Response, JsonNode? Body)> RequestAsync(string levyType, PartyId party, Guid account, string period = "", string roles = Billing, string user = "maker") =>
        Rest(HttpMethod.Post, "/api/bil/v1/tax-levy/request-levy-payment",
            new { levyType, period = period.Length == 0 ? _period : period, payeePartyId = party.Value, payeeAccountId = account }, roles, user);

    private async Task<JsonNode> TrackingOfAsync(string levyType)
    {
        var (response, body) = await Rest(HttpMethod.Get, $"/api/bil/v1/tax-levy/detail?period={_period}", roles: "Staff.Finance");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body!["levies"]!.AsArray().Single(l => l!["levyType"]!.GetValue<string>() == levyType)!;
    }

    [Fact]
    public async Task BO_BIL_14_tracking_splits_per_levy_authority_and_nets_credits_exactly()
    {
        var aux = NewLevyType();
        var other = NewLevyType();
        await SeedAsync(aux, 100.10m);
        await SeedAsync(aux, 50.05m);
        await SeedAsync(aux, 30.00m, credit: true);
        await SeedAsync(other, 40m);

        var auxItem = await TrackingOfAsync(aux);
        Amt(auxItem, "collected.amount").ShouldBe(120.15m);
        Amt(auxItem, "payable.amount").ShouldBe(0m);
        Amt(auxItem, "outstanding.amount").ShouldBe(120.15m);
        auxItem.Text("legalStatus").ShouldBe("Settled");
        auxItem.Text("provisional").ShouldBe("false");
        Amt(await TrackingOfAsync(other), "collected.amount").ShouldBe(40m);
    }

    [Fact]
    public async Task BO_BIL_14_a_payment_request_is_created_from_the_payable_and_follows_the_release_to_paid_states()
    {
        var levy = NewLevyType();
        await SeedAsync(levy, 1000.25m);
        await SeedAsync(levy, 200m, credit: true);
        var (party, account) = await LevyPayeeAsync();

        var (created, body) = await RequestAsync(levy, party, account);
        created.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        Amt(body, "amount.amount").ShouldBe(800.25m);
        body.Text("status").ShouldBe("PENDING_APPROVAL");
        body.Text("legalStatus").ShouldBe("Settled");
        var disbursementId = body.Text("disbursementId");
        (await _slice.ScalarAsync<string>($"SELECT source_type || '|' || source_id || '|' || amount FROM bil.disbursement WHERE disbursement_id = '{disbursementId}'"))
            .ShouldBe($"BIL_LEVY|{levy}:{_period}|800.2500");
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.levy_payable WHERE levy_authority = '{levy}' AND period = '{_period}'")).ShouldBe(1);

        // Requested: the payable is the whole of the collected amount, nothing outstanding.
        var requested = await TrackingOfAsync(levy);
        Amt(requested, "collected.amount").ShouldBe(800.25m);
        Amt(requested, "payable.amount").ShouldBe(800.25m);
        Amt(requested, "requested.amount").ShouldBe(800.25m);
        Amt(requested, "approved.amount").ShouldBe(0m);
        Amt(requested, "outstanding.amount").ShouldBe(0m);
        requested.Text("disbursementId").ShouldBe(disbursementId);

        // One payable per authority and month: a retry is refused with the existing ids.
        var (duplicate, duplicateBody) = await RequestAsync(levy, party, account);
        duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict, duplicateBody?.ToJsonString());
        duplicateBody.Text("code").ShouldBe("BIL-ERR-DUPLICATE");
        duplicateBody!.ToJsonString().ShouldContain(disbursementId);
        duplicateBody!.ToJsonString().ShouldContain("existingPayableId");
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.levy_payable WHERE levy_authority = '{levy}'")).ShouldBe(1);

        // The requester cannot approve; a second person releases it to Approved.
        var (self, _) = await Rest(HttpMethod.Post, "/api/bil/v1/disbursements/approve-release", new { disbursementId, decision = "APPROVE" }, Manager, "maker");
        self.StatusCode.ShouldNotBe(HttpStatusCode.OK);
        var (approved, approvedBody) = await Rest(HttpMethod.Post, "/api/bil/v1/disbursements/approve-release", new { disbursementId, decision = "APPROVE" }, Manager, "checker");
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, approvedBody?.ToJsonString());
        var after = await TrackingOfAsync(levy);
        Amt(after, "requested.amount").ShouldBe(0m);
        Amt(after, "approved.amount").ShouldBe(800.25m);
        Amt(after, "paid.amount").ShouldBe(0m);
        Amt(after, "outstanding.amount").ShouldBe(0m);
    }

    [Fact]
    public async Task BO_BIL_14_refusals_nothing_payable_wrong_payee_account_bad_input_and_roles()
    {
        var levy = NewLevyType();
        var (party, account) = await LevyPayeeAsync();

        var (none, noneBody) = await RequestAsync(levy, party, account);
        none.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent, noneBody?.ToJsonString());
        noneBody.Text("code").ShouldBe("BIL-ERR-NOTHING-PAYABLE");

        // Credits that exceed the levy leave nothing to pay.
        await SeedAsync(levy, 10m);
        await SeedAsync(levy, 25m, credit: true);
        (await RequestAsync(levy, party, account)).Body.Text("code").ShouldBe("BIL-ERR-NOTHING-PAYABLE");

        var paying = NewLevyType();
        await SeedAsync(paying, 55m);
        var (claimParty, claimAccount) = await LevyPayeeAsync(PayeeAccountCreateRequest.PurposeValue.ClaimPayment);
        var (wrong, wrongBody) = await RequestAsync(paying, claimParty, claimAccount);
        wrong.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent, wrongBody?.ToJsonString());
        wrongBody.Text("code").ShouldBe("BIL-ERR-PAYEE-ACCOUNT");
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.levy_payable WHERE levy_authority = '{paying}'")).ShouldBe(0);

        (await RequestAsync(paying, party, account, period: "2026-13")).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await RequestAsync("lower case", party, account)).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await RequestAsync(paying, party, account, roles: "Staff.Underwriter")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await RequestAsync(paying, party, account, roles: "Staff.Finance")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task BO_BIL_14_a_stopped_payment_request_can_be_requested_again_on_the_same_payable()
    {
        var levy = NewLevyType();
        await SeedAsync(levy, 300m);
        var (party, account) = await LevyPayeeAsync();

        var (first, firstBody) = await RequestAsync(levy, party, account);
        first.StatusCode.ShouldBe(HttpStatusCode.OK, firstBody?.ToJsonString());
        var payableId = firstBody.Text("payableId");
        var (stopped, stoppedBody) = await Rest(HttpMethod.Post, "/api/bil/v1/disbursements/stop", new { disbursementId = firstBody.Text("disbursementId"), reason = "WRONG_AUTHORITY" }, Manager, "maker");
        stopped.StatusCode.ShouldBe(HttpStatusCode.OK, stoppedBody?.ToJsonString());
        Amt(await TrackingOfAsync(levy), "outstanding.amount").ShouldBe(300m);

        var (again, againBody) = await RequestAsync(levy, party, account);
        again.StatusCode.ShouldBe(HttpStatusCode.OK, againBody?.ToJsonString());
        againBody.Text("payableId").ShouldBe(payableId);
        againBody.Text("disbursementId").ShouldNotBe(firstBody.Text("disbursementId"));
        Amt(await TrackingOfAsync(levy), "requested.amount").ShouldBe(300m);
    }

    [Fact]
    public async Task BO_BIL_14_levy_that_is_not_settled_is_marked_provisional_and_refused_in_Production()
    {
        var pending = NewLevyType();
        var unverified = NewLevyType();
        await SeedAsync(pending, 120m, legalStatus: "PendingOpinion", provisional: true);
        await SeedAsync(unverified, 80m, legalStatus: "Settled", provisional: true);
        (await TrackingOfAsync(pending)).Text("legalStatus").ShouldBe("PendingOpinion");
        var track = await TrackingOfAsync(unverified);
        track.Text("legalStatus").ShouldBe("Unverified");
        track.Text("provisional").ShouldBe("true");

        // Production: the same request is refused before any payable or disbursement exists.
        var (party, account) = await LevyPayeeAsync();
        foreach (var levy in new[] { pending, unverified })
        {
            await using var scope = _slice.Scope(Billing);
            var sp = scope.ServiceProvider;
            var handler = new RequestLevyPaymentHandler(
                sp.GetRequiredService<BillingDbContext>(), sp.GetRequiredService<TaxLevyReader>(), sp.GetRequiredService<LedgerWriter>(),
                sp.GetRequiredService<RequestContext>(), sp.GetRequiredService<IClock>(), new FakeEnvironment("Production"), sp);
            var refused = await handler.HandleAsync(
                new RequestLevyPayment(new TaxLevyRequestLevyPaymentRequest { LevyType = levy, Period = _period, PayeePartyId = party, PayeeAccountId = account }), Ct);
            refused.IsFailure.ShouldBeTrue();
            refused.Error!.Code.Value.ShouldBe("BIL-ERR-LEVY-UNSETTLED");
            (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.levy_payable WHERE levy_authority = '{levy}'")).ShouldBe(0);
        }

        // Outside Production the request goes through, flagged provisional.
        var (ok, body) = await RequestAsync(pending, party, account);
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("provisional").ShouldBe("true");
        body.Text("legalStatus").ShouldBe("PendingOpinion");
    }

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "CoreIns.Host";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
