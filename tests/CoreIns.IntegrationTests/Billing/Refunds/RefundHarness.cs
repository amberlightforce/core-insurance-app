using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CoreIns.Modules.Billing;
using CoreIns.IntegrationTests.Bil.Credits;
using CoreIns.Modules.Party.Contracts;
using CoreIns.Testing.Contracts.Fakes.Party;
using CoreIns.Platform.Context;
using CoreIns.Platform.Events;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using static CoreIns.IntegrationTests.Bil.BillingSlice;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Bil.Refunds;

/// <summary>Test users of the refund flows (roles as the dev users carry them; the superuser holds both billing roles).</summary>
internal static class RefundUsers
{
    public const string Clerk = "Staff.Billing";
    public const string Manager = "Staff.BillingManager";
    public const string Both = "Staff.Billing,Staff.BillingManager";
}

/// <summary>
/// A bound, paid and cancelled policy whose credit sits on the billing account, with the payer's verified REFUND account and a
/// scripted sanctions screening (Clear). Amounts are illustrative test data (D-SLC-04, D-SL3-08): tests assert consistency,
/// not tariff values.
/// </summary>
internal sealed class RefundHarness : IAsyncDisposable
{
    public const string ReasonCode = "CUSTOMER_REQUEST";

    private RefundHarness(BillingSlice slice, FakeTaxCalculator treatment, FakePartyScreeningService screening)
    {
        Slice = slice;
        Treatment = treatment;
        Screening = screening;
    }

    public BillingSlice Slice { get; }

    public FakeTaxCalculator Treatment { get; }

    public FakePartyScreeningService Screening { get; }

    public CreditScenario Scenario { get; private set; } = null!;

    public decimal Credit { get; private set; }

    public string Iban { get; private set; } = string.Empty;

    public string PayeeAccountId { get; private set; } = string.Empty;

    public string AccountId => Scenario.AccountId;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The host with the refund options of the test (null = the defaults).</summary>
    public static async Task<RefundHarness> StartAsync(PostgresFixture database, Action<BillingOptions>? configure = null, bool verifier = true)
    {
        var treatment = new FakeTaxCalculator();
        var screening = new FakePartyScreeningService();
        screening.Setup("pty.Screening.screen", DisbursementSlice.Clear());
        var slice = new BillingSlice(database, services =>
        {
            services.WithFakeTreatment(treatment);
            services.RemoveAll<IPartyScreeningService>();
            services.AddSingleton<IPartyScreeningService>(screening);
            if (!verifier)
            {
                services.RemoveAll<CoreIns.Modules.Billing.Services.IPayeeVerifier>();
            }

            if (configure is not null)
            {
                services.PostConfigure(configure);
            }
        });
        await slice.SeedAsync();
        return new RefundHarness(slice, treatment, screening);
    }

    /// <summary>Bills the policy, takes the payment and applies a policyholder cancellation (60 % of the premium credited, IPT kept).</summary>
    public async Task<RefundHarness> WithCreditAsync(decimal factor = -0.6m, bool registerPayee = true)
    {
        Scenario = await CreditScenario.BilledAsync(Slice);
        await Scenario.PayAsync(Scenario.Policy.Total);
        var (set, credit, transaction) = Scenario.ServicingSet("CANCELLATION", "Policyholder", factor);
        (await Slice.InvokeAsync("BIL.PolicyCancelled.StopBilling", Scenario.Cancelled(transaction))).ShouldBeTrue();
        await Scenario.DeliverAsync(set);
        Credit = -credit;
        if (registerPayee)
        {
            await RegisterPayeeAsync("alice");
        }

        return this;
    }

    /// <summary>Registers (or changes) the payer's REFUND account as <paramref name="user"/>; returns the response.</summary>
    public async Task<JsonNode> RegisterPayeeAsync(string user)
    {
        Iban = DisbursementSlice.NewIban();
        var (response, body) = await SendAsync(
            HttpMethod.Post, "/api/bil/v1/payee-accounts", new { partyId = Scenario.Policy.PartyId, purpose = "REFUND", iban = Iban, holderName = "Ελένη Παπαδοπούλου", evidenceRef = "DOC-1" },
            RefundUsers.Clerk, user);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, body?.ToJsonString());
        PayeeAccountId = body.Text("payeeAccountId");
        return body!;
    }

    public async Task<(HttpResponseMessage Response, JsonNode? Body)> SendAsync(HttpMethod method, string path, object? body, string roles, string user, Guid? key = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add(TestAuthHandler.RolesHeader, roles);
        request.Headers.Add(TestAuthHandler.UserHeader, user);
        request.Headers.AcceptLanguage.ParseAdd("en");
        if (method != HttpMethod.Get)
        {
            request.Headers.Add("Idempotency-Key", (key ?? Guid.NewGuid()).ToString());
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        var response = await Slice.Client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    public Task<(HttpResponseMessage Response, JsonNode? Body)> ProposeAsync(string user, string roles = RefundUsers.Clerk, object? extra = null) =>
        SendAsync(HttpMethod.Post, "/api/bil/v1/refunds/propose", extra ?? new { billingAccountId = AccountId, reasonCode = ReasonCode, comment = "customer cancelled" }, roles, user);

    public Task<(HttpResponseMessage Response, JsonNode? Body)> DecideAsync(string refundId, string decision, string user, string roles = RefundUsers.Manager, string? comment = null) =>
        SendAsync(HttpMethod.Post, "/api/bil/v1/refunds/decide", new { refundId, decision, comment }, roles, user);

    public Task<(HttpResponseMessage Response, JsonNode? Body)> ResubmitAsync(string refundId, string user, string roles = RefundUsers.Clerk) =>
        SendAsync(HttpMethod.Post, "/api/bil/v1/refunds/resubmit", new { refundId, comment = "payee corrected" }, roles, user);

    public Task<(HttpResponseMessage Response, JsonNode? Body)> GetAsync(string refundId, string user = "alice", string roles = RefundUsers.Clerk) =>
        SendAsync(HttpMethod.Get, $"/api/bil/v1/refunds/{refundId}", null, roles, user);

    /// <summary>Proposes and expects success; returns the refund view.</summary>
    public async Task<JsonNode> ProposedAsync(string user = "alice", string roles = RefundUsers.Clerk)
    {
        var (response, body) = await ProposeAsync(user, roles);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body!["refund"]!;
    }

    public async Task<JsonNode> ReloadAsync(string refundId)
    {
        var (response, body) = await GetAsync(refundId);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body!["refund"]!;
    }

    public static decimal Money(JsonNode? node) => decimal.Parse(node!["amount"]!.GetValue<string>(), CultureInfo.InvariantCulture);

    /// <summary>Credit still on the account: Σ credit items less Σ credit applications.</summary>
    public Task<decimal> CreditOnAccountAsync() => Slice.ScalarAsync<decimal>(
        $"""
        SELECT coalesce(sum(i.amount), 0) - coalesce((SELECT sum(a.amount) FROM bil.credit_application a WHERE a.billing_account_id = '{AccountId}'), 0)
        FROM bil.invoice_item i JOIN bil.invoice n ON n.invoice_id = i.invoice_id
        WHERE n.billing_account_id = '{AccountId}' AND n.kind = 'CREDIT_NOTE'
        """);

    /// <summary>The ledger lines of the refund entry type for this account as "account side amount".</summary>
    public async Task<List<string>> LinesAsync(string entryType)
    {
        var rows = new List<string>();
        await using var command = Slice.DataSource.CreateCommand(
            $"""
            SELECT l.account_code || ' ' || l.side || ' ' || l.amount::text || ' ' || coalesce(l.source_type, '-') || ' ' || coalesce(l.source_id, '-') || ' ' || coalesce(l.transaction_kind, '-')
            FROM bil.ledger_line l JOIN bil.ledger_entry e ON e.entry_id = l.entry_id
            WHERE e.entry_type = '{entryType}' AND (e.billing_account_id = '{AccountId}' OR l.billing_account_id = '{AccountId}')
            ORDER BY e.recorded_at, l.line_no
            """);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    /// <summary>The first column of every row of a query, as text.</summary>
    public async Task<List<string>> TextsAsync(string sql)
    {
        var result = new List<string>();
        await using var command = Slice.DataSource.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            result.Add(reader.IsDBNull(0) ? string.Empty : reader.GetValue(0).ToString()!);
        }

        return result;
    }

    /// <summary>Rows anywhere in the stores that would expose <paramref name="secret"/> in clear (text, JSON or bytea hex).</summary>
    public Task<long> ExposuresAsync(string secret) => Slice.ScalarAsync<long>(
        $"""
        SELECT (SELECT count(*) FROM bil.refund t WHERE t::text LIKE '%{secret}%')
             + (SELECT count(*) FROM bil.disbursement t WHERE t::text LIKE '%{secret}%')
             + (SELECT count(*) FROM bil.ledger_entry t WHERE t::text LIKE '%{secret}%')
             + (SELECT count(*) FROM bil.ledger_line t WHERE t::text LIKE '%{secret}%')
             + (SELECT count(*) FROM plt.outbox_message t WHERE t::text LIKE '%{secret}%')
             + (SELECT count(*) FROM plt.audit_event t WHERE t::text LIKE '%{secret}%')
             + (SELECT count(*) FROM plt.idempotency_record t WHERE t::text LIKE '%{secret}%' OR t::text LIKE '%{DisbursementSlice.Hex(secret)}%')
        """);

    public async ValueTask DisposeAsync() => await Slice.DisposeAsync();
}
