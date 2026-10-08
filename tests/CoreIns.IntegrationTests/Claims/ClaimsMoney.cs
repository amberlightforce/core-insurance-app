using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoreIns.Platform.Events;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace CoreIns.IntegrationTests.Claims;

/// <summary>An open claim with one own-damage exposure for the money tests.</summary>
internal sealed record MoneyClaim(string ClaimId, string ClaimNumber, string ExposureId, string InsuredPartyId);

/// <summary>
/// HTTP helpers of the claim financial engine (SL2-CLM-MONEY) over any CLM host: named users (maker ≠ checker is decided by
/// the user id, <see cref="TestAuthHandler.UserHeader"/>), FNOL → exposure, payee capture, set build/submit, the PLT inbox
/// decision, and the outbox dispatch (the worker's role). IBANs are synthetic.
/// </summary>
internal sealed class ClaimsMoney(WebApplicationFactory<Program> factory, HttpClient client, string superuserConnectionString)
{
    public const string Handler = "Staff.ClaimsHandler";
    public const string Manager = "Staff.ClaimsManager";
    public const string HandlerUser = "handler-anna";
    public const string ManagerUser = "manager-nikos";

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async Task<(HttpResponseMessage Response, JsonNode? Body)> SendAsync(
        HttpMethod method, string path, object? body = null, string roles = Handler, string user = HandlerUser, Guid? key = null)
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
            request.Content = new StringContent(body is JsonNode node ? node.ToJsonString() : JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
        }

        var response = await client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    /// <summary>FNOL with an own-damage exposure on coverage <paramref name="coverage"/>, as the handler.</summary>
    public async Task<MoneyClaim> OpenClaimAsync(JsonObject fnol)
    {
        var (response, body) = await SendAsync(HttpMethod.Post, "/api/clm/v1/fnol/submit", fnol);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return new MoneyClaim(body.Text("claimId"), body.Text("claimNumber"), body.Text("exposures.0.exposureId"), body.Text("claim.insuredPartyId"));
    }

    /// <summary>clm.PayeeAccount.capture for the insured with a fresh synthetic IBAN; returns (payee account id, IBAN).</summary>
    public async Task<(string AccountId, string Iban)> CaptureAsync(MoneyClaim claim, string? partyId = null)
    {
        var iban = Bil.DisbursementSlice.NewIban();
        var (response, body) = await SendAsync(HttpMethod.Post, "/api/clm/v1/payee-accounts/capture", new
        {
            claimId = claim.ClaimId, partyId = partyId ?? claim.InsuredPartyId, iban, holderName = "Ελένη Παπαδοπούλου (synthetic)",
        });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, body?.ToJsonString());
        return (body.Text("payeeAccount.payeeAccountId"), iban);
    }

    public static object Reserve(MoneyClaim claim, decimal amount, string category = "VEHICLE_REPAIR", string reason = "ASSESSMENT", string costType = "INDEMNITY") => new
    {
        kind = "RESERVE", exposureId = claim.ExposureId, costType, costCategory = category, amount = Money(amount), reason,
    };

    public static object Payment(MoneyClaim claim, decimal amount, string accountId, string paymentType = "FINAL", string category = "VEHICLE_REPAIR") => new
    {
        kind = "PAYMENT", exposureId = claim.ExposureId, costType = "INDEMNITY", costCategory = category, amount = Money(amount),
        payeePartyId = claim.InsuredPartyId, payeeAccountId = accountId, paymentType,
    };

    public static object Money(decimal amount) => new { amount = amount.ToString("F2", CultureInfo.InvariantCulture), currency = "EUR" };

    public Task<(HttpResponseMessage Response, JsonNode? Body)> BuildAsync(MoneyClaim claim, object[] transactions, string roles = Handler, string user = HandlerUser, bool dryRun = false) =>
        SendAsync(HttpMethod.Post, "/api/clm/v1/transaction-sets/build" + (dryRun ? "?dryRun=true" : string.Empty), new { claimId = claim.ClaimId, transactions }, roles, user);

    public Task<(HttpResponseMessage Response, JsonNode? Body)> SubmitAsync(string setId, string roles = Handler, string user = HandlerUser) =>
        SendAsync(HttpMethod.Post, "/api/clm/v1/transaction-sets/submit", new { setId }, roles, user);

    /// <summary>Build then submit; asserts both succeed and returns the submit body.</summary>
    public async Task<JsonNode> BuildAndSubmitAsync(MoneyClaim claim, object[] transactions, string roles = Handler, string user = HandlerUser)
    {
        var (built, build) = await BuildAsync(claim, transactions, roles, user);
        built.StatusCode.ShouldBe(HttpStatusCode.OK, build?.ToJsonString());
        var (submitted, submit) = await SubmitAsync(build.Text("setId"), roles, user);
        submitted.StatusCode.ShouldBe(HttpStatusCode.OK, submit?.ToJsonString());
        return submit!;
    }

    /// <summary>plt.Approval.decide on the inbox item (the hash the checker saw comes from plt.Approval.get).</summary>
    public async Task<(HttpResponseMessage Response, JsonNode? Body)> DecideAsync(string requestId, string decision = "Approve", string roles = Manager, string user = ManagerUser, string? comment = null)
    {
        var (got, request) = await SendAsync(HttpMethod.Get, $"/api/plt/v1/approval/{requestId}", roles: roles, user: user);
        got.StatusCode.ShouldBe(HttpStatusCode.OK, request?.ToJsonString());
        return await SendAsync(HttpMethod.Post, "/api/plt/v1/approval/decide",
            new { requestId, decision, payloadHash = request.Text("request.payloadHash"), comment = comment ?? (decision == "Reject" ? "Reserve too high" : null) }, roles, user);
    }

    public Task<OutboxBatchResult> DrainAsync() => factory.Services.GetRequiredService<OutboxProcessor>().DrainAsync(Ct);

    public async Task<JsonNode> SetAsync(string setId)
    {
        var (response, body) = await SendAsync(HttpMethod.Get, $"/api/clm/v1/transaction-sets/{setId}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body!["set"]!;
    }

    public async Task<JsonNode> FinancialsAsync(MoneyClaim claim, string? knownAt = null)
    {
        var (response, body) = await SendAsync(HttpMethod.Get, $"/api/clm/v1/financials/get?claim={claim.ClaimId}" + (knownAt is null ? string.Empty : "&knownAt=" + Uri.EscapeDataString(knownAt)));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body!;
    }

    public async Task<JsonNode> PaymentsAsync(MoneyClaim claim)
    {
        var (response, body) = await SendAsync(HttpMethod.Get, $"/api/clm/v1/claims/{claim.ClaimId}/payments");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body!["items"]!;
    }

    public async Task<JsonNode> ClaimAsync(MoneyClaim claim)
    {
        var (response, body) = await SendAsync(HttpMethod.Get, $"/api/clm/v1/claims/{claim.ClaimId}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body!["claim"]!;
    }

    public Task<(HttpResponseMessage Response, JsonNode? Body)> CloseAsync(MoneyClaim claim, int recordVersion) =>
        SendAsync(HttpMethod.Post, "/api/clm/v1/claims/close", new { claimId = claim.ClaimId, expectedRecordVersion = recordVersion, outcome = "COMPLETED" });

    public static decimal Amount(JsonNode? money) => decimal.Parse(money!["amount"]!.GetValue<string>(), CultureInfo.InvariantCulture);

    public async Task<T> ScalarAsync<T>(string sql)
    {
        await using var dataSource = NpgsqlDataSource.Create(superuserConnectionString);
        await using var command = dataSource.CreateCommand(sql);
        var value = await command.ExecuteScalarAsync(Ct);
        return value switch
        {
            null or DBNull => default!,
            T typed => typed,
            _ => (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture),
        };
    }

    /// <summary>Rows in CLM's tables, the outbox, the audit and the idempotency store that would show <paramref name="secret"/> in clear.</summary>
    public Task<long> ExposuresAsync(string secret) => ScalarAsync<long>(
        $"""
        SELECT (SELECT count(*) FROM clm.claim t WHERE t::text LIKE '%{secret}%')
             + (SELECT count(*) FROM clm.reserve_line t WHERE t::text LIKE '%{secret}%')
             + (SELECT count(*) FROM clm.transaction_set t WHERE t::text LIKE '%{secret}%')
             + (SELECT count(*) FROM clm.financial_transaction t WHERE t::text LIKE '%{secret}%')
             + (SELECT count(*) FROM clm.claim_payment t WHERE t::text LIKE '%{secret}%')
             + (SELECT count(*) FROM clm.payee_account_view t WHERE t::text LIKE '%{secret}%')
             + (SELECT count(*) FROM plt.outbox_message t WHERE t::text LIKE '%{secret}%')
             + (SELECT count(*) FROM plt.event_archive t WHERE t::text LIKE '%{secret}%')
             + (SELECT count(*) FROM plt.audit_event t WHERE t::text LIKE '%{secret}%')
             + (SELECT count(*) FROM plt.idempotency_record t WHERE t::text LIKE '%{secret}%' OR convert_from(response_body, 'UTF8') LIKE '%{secret}%')
        """);
}
