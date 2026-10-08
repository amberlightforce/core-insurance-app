using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Billing.Services;
using CoreIns.Modules.Party.Contracts;
using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.Testing.Contracts.Fakes.Party;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Bil;

/// <summary>
/// The SL2-BIL-DISB host: the real Host and database with the generated PTY screening double in place of
/// <c>pty.Screening.screen</c> (built by another work package) and BIL's own VoP and bank-channel stubs (Testing is not
/// Production). Disbursements are requested in process, exactly as CLM will call them. IBANs and names are synthetic.
/// </summary>
internal sealed class DisbursementSlice : IAsyncDisposable
{
    private readonly ApiHostFactory _host;

    public DisbursementSlice(PostgresFixture database, bool screening = true, bool payeeVerifier = true, bool bankChannel = true)
    {
        Database = database;
        _host = new ApiHostFactory(database.AppConnectionString);
        Screening.Setup("pty.Screening.screen", Clear());
        Factory = _host.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IPartyScreeningService>();
            if (screening)
            {
                services.AddSingleton<IPartyScreeningService>(Screening);
            }

            if (!payeeVerifier)
            {
                services.RemoveAll<IPayeeVerifier>();
            }

            if (!bankChannel)
            {
                services.RemoveAll<IBankChannel>();
            }
        }));
        Client = Factory.CreateClient();
        DataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
    }

    public PostgresFixture Database { get; }

    public FakePartyScreeningService Screening { get; } = new();

    public WebApplicationFactory<Program> Factory { get; }

    public HttpClient Client { get; }

    public NpgsqlDataSource DataSource { get; }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static ScreeningScreenResponse Clear() =>
        new() { Result = ScreeningScreenResponse.ResultValue.Clear, ListVersions = ["EU-TEST-2026-10"], PaymentBlock = false };

    /// <summary>A valid synthetic Greek IBAN (ISO 13616 check digits computed), unique per call.</summary>
    public static string NewIban()
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

    /// <summary>The IBAN's ASCII as lower-case hex (how a bytea column would show it in clear).</summary>
    public static string Hex(string text) => Convert.ToHexStringLower(Encoding.ASCII.GetBytes(text));

    /// <summary>An in-process caller scope (as CLM's handler would run), with a filled request context.</summary>
    public AsyncServiceScope Scope(string roles = "Staff.ClaimsHandler") => Rating.RatingTestSupport.Scope(Factory.Services, roles);

    public async Task<PayeeAccountCreateResponse> CreateAccountAsync(PartyId party, string iban, PayeeAccountCreateRequest.PurposeValue purpose = PayeeAccountCreateRequest.PurposeValue.ClaimPayment)
    {
        await using var scope = Scope();
        return await scope.ServiceProvider.GetRequiredService<IBillingPayeeAccountService>().CreateAsync(
            new PayeeAccountCreateRequest { PartyId = party, Purpose = purpose, Iban = iban, HolderName = "Ελένη Παπαδοπούλου" }, CommandOptions.New(), Ct);
    }

    /// <summary>A CLM claim-payment request with the content hash the approval covered.</summary>
    public static DisbursementRequestRequest ClaimPayment(PartyId payee, Guid account, decimal amount, string? sourceId = null, ClaimId? claim = null, decimal? approvedAmount = null)
    {
        var source = sourceId ?? Guid.CreateVersion7().ToString();
        return new DisbursementRequestRequest
        {
            SourceType = DisbursementCodes.ClaimPayment,
            SourceId = source,
            PayeePartyId = payee,
            PayeeAccountId = account,
            Amount = new Money(amount, Currency.EUR),
            Method = DisbursementCodes.SepaCreditTransfer,
            ApprovalEvidenceRef = "PLT-APPROVAL-" + source,
            ApprovalContentHash = DisbursementContent.Hash(DisbursementCodes.ClaimPayment, source, payee, account, new Money(approvedAmount ?? amount, Currency.EUR)),
            ClaimId = claim ?? ClaimId.New(),
            PurposeText = "Claim payment",
        };
    }

    public async Task<DisbursementRequestResponse> RequestAsync(DisbursementRequestRequest request, CommandOptions? options = null)
    {
        await using var scope = Scope();
        return await scope.ServiceProvider.GetRequiredService<IBillingDisbursementService>().RequestAsync(request, options ?? CommandOptions.New(), Ct);
    }

    /// <summary>The BIL error code of a refused in-process request.</summary>
    public async Task<string> RefusedAsync(DisbursementRequestRequest request)
    {
        var error = await Should.ThrowAsync<DomainException>(() => RequestAsync(request));
        return error.Error.Code.Value;
    }

    public Task<(HttpResponseMessage Response, JsonNode? Body)> GetAsync(string path, string roles = Billing) => SendAsync(Client, HttpMethod.Get, path, roles: roles);

    public Task<(HttpResponseMessage Response, JsonNode? Body)> PostAsync(string path, object body, string roles = Billing, Guid? key = null) =>
        SendAsync(Client, HttpMethod.Post, path, body, roles: roles, key: key);

    public async Task<T> ScalarAsync<T>(string sql)
    {
        await using var command = DataSource.CreateCommand(sql);
        var value = await command.ExecuteScalarAsync(Ct);
        return value switch
        {
            null or DBNull => default!,
            T typed => typed,
            _ => (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture),
        };
    }

    public async Task<List<string>> TextsAsync(string sql)
    {
        var result = new List<string>();
        await using var command = DataSource.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            result.Add(reader.IsDBNull(0) ? string.Empty : reader.GetValue(0).ToString()!);
        }

        return result;
    }

    /// <summary>Rows anywhere in the stores that would expose <paramref name="secret"/> in clear (text, JSON or bytea hex).</summary>
    public Task<long> ExposuresAsync(string secret) => ScalarAsync<long>(
        $"""
        SELECT (SELECT count(*) FROM bil.payee_account t WHERE t::text LIKE '%{secret}%' OR t::text LIKE '%{Hex(secret)}%')
             + (SELECT count(*) FROM bil.disbursement t WHERE t::text LIKE '%{secret}%')
             + (SELECT count(*) FROM bil.ledger_entry t WHERE t::text LIKE '%{secret}%')
             + (SELECT count(*) FROM bil.ledger_line t WHERE t::text LIKE '%{secret}%')
             + (SELECT count(*) FROM plt.outbox_message t WHERE t::text LIKE '%{secret}%')
             + (SELECT count(*) FROM plt.audit_event t WHERE t::text LIKE '%{secret}%')
             + (SELECT count(*) FROM plt.idempotency_record t WHERE t::text LIKE '%{secret}%' OR t::text LIKE '%{Hex(secret)}%')
        """);

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await DataSource.DisposeAsync();
        await Factory.DisposeAsync();
        await _host.DisposeAsync();
    }
}
