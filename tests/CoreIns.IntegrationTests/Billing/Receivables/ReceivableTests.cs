using System.Net;
using System.Net.Http.Json;

using System.Text.Json.Nodes;
using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace CoreIns.IntegrationTests.Bil.Receivables;

public sealed class ReceivableTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _factory = new ApiHostFactory(database.AppConnectionString).WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddCommand<ClmRegister, ReceivableRegisterResponse, ClmRegisterHandler>(CommandDescriptor.For("clm.ReceivableProbe.register"))));
        _client = _factory.CreateClient();
        return ValueTask.CompletedTask;
    }
    public async ValueTask DisposeAsync() { _client.Dispose(); await _factory.DisposeAsync(); }

    private AsyncServiceScope Scope(IdempotencyKey? key = null)
    {
        var scope = _factory.Services.CreateAsyncScope();
        var c = scope.ServiceProvider.GetRequiredService<RequestContext>();
        c.Actor = ActorRef.User("receivable-maker"); c.Roles = ["Staff.ClaimsHandler", "Staff.Billing"];
        c.LegalEntity = LegalEntityCode.Parse("GR-TEST"); c.Jurisdiction = Jurisdiction.Parse("GR");
        c.ConfigurationHash = ConfigurationHash.Parse(new string('a', 64)); c.IdempotencyKey = key ?? IdempotencyKey.New();
        return scope;
    }
    private static ReceivableRegisterRequest Request(decimal amount = 1700m, bool fs = false) => new()
    {
        SourceType = fs ? ReceivableSourceType.FsClearing : ReceivableSourceType.ClmClaimPayment,
        SourceId = Guid.NewGuid().ToString("D"), CounterpartyPartyId = PartyId.New(),
        ClaimId = fs ? null : ClaimId.New(), RecoveryId = fs ? null : Guid.CreateVersion7(),
        StatementRef = fs ? "FS-" + Guid.NewGuid().ToString("N") : null,
        Purpose = fs ? ReceivablePurpose.FsNet : ReceivablePurpose.Salvage,
        Amount = Money.Of(amount, "EUR"), DueDate = BusinessDate.Parse("2026-11-01"),
    };
    private async Task<Result<ReceivableRegisterResponse>> RegisterAsync(ReceivableRegisterRequest request, IdempotencyKey? key = null)
    {
        await using var scope = Scope(key);
        var response = await scope.ServiceProvider.GetRequiredService<ICommandHandler<ClmRegister, ReceivableRegisterResponse>>().HandleAsync(new ClmRegister(request), Ct);
        scope.ServiceProvider.GetRequiredService<RequestContext>().ExecutingCommandModule.ShouldBeNull();
        return response;
    }
    private async Task<JsonNode?> PayAsync(ReceivableRegisterResponse row, decimal amount)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/bil/v1/payments/take");
        request.Headers.Add(TestAuthHandler.RolesHeader, "Staff.Billing");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Content = JsonContent.Create(new { billingAccountId = row.BillingAccountId.Value, paymentReference = row.PaymentReference, amount = new { amount = amount.ToString(System.Globalization.CultureInfo.InvariantCulture), currency = "EUR" }, method = "BANK_TRANSFER" });
        using var response = await _client.SendAsync(request, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.IsSuccessStatusCode.ShouldBeTrue(body);
        return JsonNode.Parse(body);
    }

    [Fact]
    public async Task REQ_BIL_346_salvage_cash_closes_receivable_and_publishes_authoritative_allocation_refs()
    {
        var request = Request(); var registered = await RegisterAsync(request); registered.IsSuccess.ShouldBeTrue(registered.Error?.Detail);
        var row = registered.Value; await PayAsync(row, 1700m);
        await using var scope = Scope();
        var view = await scope.ServiceProvider.GetRequiredService<IBillingReceivableService>().GetAsync(row.ReceivableId.ToString("D"), Ct);
        view.Receivable.Status.ShouldBe(ReceivableStatus.Paid); view.Receivable.OpenAmount.Amount.ShouldBe(0m);
        await using var source = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var query = source.CreateCommand("SELECT payload::text FROM plt.outbox_message WHERE event_type='CashAllocated' AND aggregate_id=@id ORDER BY aggregate_sequence DESC LIMIT 1");
        query.Parameters.AddWithValue("id", row.BillingAccountId.Value.ToString("D"));
        var payload = JsonNode.Parse((string)(await query.ExecuteScalarAsync(Ct))!);
        payload!["receivableId"]!.GetValue<string>().ShouldBe(row.ReceivableId.ToString("D"));
        payload["claimId"]!.GetValue<string>().ShouldBe(request.ClaimId!.Value.Value.ToString("D"));
        payload["recoveryId"]!.GetValue<string>().ShouldBe(request.RecoveryId!.Value.ToString("D"));
        payload["allocationId"].ShouldNotBeNull(); payload.ToJsonString().ShouldNotContain("iban", Case.Insensitive);
    }

    [Fact]
    public async Task REQ_BIL_130_subrogation_partial_then_overpayment_bounds_allocation_and_leaves_credit()
    {
        var request = Request(200000m) with { Purpose = ReceivablePurpose.Subrogation };
        var registered = (await RegisterAsync(request)).Value;
        await PayAsync(registered, 150000m);
        await using (var scope = Scope())
        {
            var view = await scope.ServiceProvider.GetRequiredService<IBillingReceivableService>().GetAsync(registered.ReceivableId.ToString("D"), Ct);
            view.Receivable.OpenAmount.Amount.ShouldBe(50000m); view.Receivable.Status.ShouldBe(ReceivableStatus.PartiallyPaid);
        }
        var payment = await PayAsync(registered, 50020m);
        payment!["receipt"]!["unallocated"]!["amount"]!.GetValue<string>().ShouldBe("20.00");
    }

    [Fact]
    public async Task REQ_BIL_356_FS_partial_cash_leaves_fifty_open_on_clearing_account()
    {
        var registered = (await RegisterAsync(Request(8150m, true))).Value;
        await PayAsync(registered, 8100m);
        await using var scope = Scope();
        var view = await scope.ServiceProvider.GetRequiredService<IBillingReceivableService>().GetAsync(registered.ReceivableId.ToString("D"), Ct);
        view.Receivable.OpenAmount.Amount.ShouldBe(50m); view.Receivable.StatementRef.ShouldNotBeNull();
    }

    [Fact]
    public async Task REQ_BIL_346_same_key_replays_new_key_duplicate_refused_and_forged_actor_has_no_module_authority()
    {
        var request = Request(); var key = IdempotencyKey.New();
        var first = await RegisterAsync(request, key); var replay = await RegisterAsync(request, key);
        replay.Value.ReceivableId.ShouldBe(first.Value.ReceivableId);
        (await RegisterAsync(request)).Error!.Code.Value.ShouldBe("BIL-ERR-DUPLICATE");
        await using var scope = Scope();
        scope.ServiceProvider.GetRequiredService<RequestContext>().Actor = ActorRef.Service("clm");
        var error = await Should.ThrowAsync<DomainException>(() => scope.ServiceProvider.GetRequiredService<IBillingReceivableService>().RegisterAsync(Request(), CommandOptions.New(), Ct));
        error.Error.Code.Value.ShouldBe("BIL-ERR-NOT-PERMITTED");
    }

    [Fact]
    public async Task REQ_BIL_346_HTTP_client_cannot_register_even_with_claims_role()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/bil/v1/receivables/register");
        request.Headers.Add(TestAuthHandler.RolesHeader, "Staff.ClaimsHandler"); request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Content = JsonContent.Create(Request());
        using var response = await _client.SendAsync(request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    public sealed record ClmRegister(ReceivableRegisterRequest Request) : ICommand<ReceivableRegisterResponse>;
    public sealed class ClmRegisterHandler(IBillingReceivableService billing, RequestContext context) : ICommandHandler<ClmRegister, ReceivableRegisterResponse>
    {
        public async Task<Result<ReceivableRegisterResponse>> HandleAsync(ClmRegister command, CancellationToken ct)
        {
            context.ExecutingCommandModule.ShouldBe(ModuleCode.CLM);
            try { return await billing.RegisterAsync(command.Request, new CommandOptions(context.IdempotencyKey!.Value), ct); }
            catch (DomainException e) { return e.Error; }
        }
    }
}


