using System.Globalization;
using System.Text.Json.Nodes;
using CoreIns.IntegrationTests.Finance;
using CoreIns.IntegrationTests.Reinsurance.Registry;
using CoreIns.Modules.Claims.Contracts.Events;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts.Events;
using CoreIns.Platform.Events;
using CoreIns.Platform.Persistence;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Reinsurance.Recovery;

/// <summary>
/// The RI recovery host: the RI registry slice (real Host and database, real PTY) plus a CLM event producer. CLM events are
/// published through the real outbox and handed to RI's consumers one by one (the worker's dispatch without the other modules'
/// consumers), so each test controls order and redelivery. Every claim, amount and treaty is synthetic and ILLUSTRATIVE (D-SL4-04).
/// </summary>
internal sealed class RecoverySlice : IAsyncDisposable
{
    public const string Coverage = "TPL";

    private readonly NpgsqlDataSource _superuser;

    public RecoverySlice(PostgresFixture database)
    {
        Registry = new RegistrySlice(database);
        _superuser = NpgsqlDataSource.Create(database.SuperuserConnectionString);
    }

    public RegistrySlice Registry { get; }

    public IServiceProvider Services => Registry.Factory.Services;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>An Active XoL treaty 500,000 xs 500,000 (AAL 1.5M) for a fresh product, panel 60/40 at 100%; returns (product, contract id, lead, follow).</summary>
    public async Task<(string Product, string ContractId, string Lead, string Follow)> TreatyAsync(string from = "2026-01-01", string to = "2027-01-01", string coverage = Coverage)
    {
        var lead = await Registry.OrganisationAsync("Synthetic Lead Re " + Guid.NewGuid().ToString("N")[..6]);
        var follow = await Registry.OrganisationAsync("Synthetic Follow Re " + Guid.NewGuid().ToString("N")[..6]);
        var product = RegistrySlice.NewProduct();
        var (id, approved) = await Registry.ApprovedAsync(RegistrySlice.Body(product, coverage, lead, follow, from, to));
        approved.Text("contract.status").ShouldBe("ACTIVE");
        return (product, id, lead, follow);
    }

    private static JsonObject Eur3(string amount) => new()
    {
        ["transaction"] = new JsonObject { ["amount"] = amount, ["currency"] = "EUR" },
        ["functional"] = new JsonObject { ["amount"] = amount, ["currency"] = "EUR" },
        ["group"] = new JsonObject { ["amount"] = amount, ["currency"] = "EUR" },
    };

    /// <summary>Publishes one CLM event of the claim aggregate through the outbox and returns the stored envelope (with its aggregate sequence).</summary>
    private async Task<EventEnvelope> PublishAsync(EventContract contract, Guid claimId, JsonObject payload, BusinessKeys keys)
    {
        await using var scope = Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<RequestContext>();
        context.Actor = ActorRef.Service("clm-test-producer");
        context.LegalEntity = LegalEntityCode.Parse("GR-TEST");
        context.Jurisdiction = Jurisdiction.Parse("GR");
        context.ConfigurationHash = ConfigurationHash.Parse(new string('c', 64));
        var session = scope.ServiceProvider.GetRequiredService<DbSession>();
        await using var transaction = await session.BeginTransactionAsync(Ct);
        var staged = scope.ServiceProvider.GetRequiredService<IEventPublisher>()
            .Publish(new OutgoingEvent(EventDescriptor.From(contract), "Claim", claimId.ToString(), payload, keys));
        await transaction.CommitAsync(Ct);
        await using var command = _superuser.CreateCommand($"SELECT {EnvelopeColumnsReader.Columns} FROM plt.outbox_message WHERE event_id = @id");
        command.Parameters.AddWithValue("id", staged.EventId.Value);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).ShouldBeTrue();
        return EnvelopeColumnsReader.Read(reader);
    }

    /// <summary>Hands one envelope to one RI consumer (what the dispatcher does); false when that consumer had already processed the event.</summary>
    public async Task<bool> DeliverAsync(string handlerName, EventEnvelope envelope, bool replay = false)
    {
        var registration = Services.GetRequiredService<EventHandlerRegistry>().Find(handlerName) ?? throw new InvalidOperationException(handlerName);
        return await new HandlerInvoker(Services.GetRequiredService<IServiceScopeFactory>())
            .InvokeAsync(registration, envelope, replay, Services.GetRequiredService<IClock>(), Ct);
    }

    public async Task<EventEnvelope> ReportedAsync(Guid claimId, string product, string lossDate = "2026-03-01")
    {
        var payload = FinanceSlice.Sample("clm", "ClaimReported");
        payload["claimId"] = claimId.ToString();
        payload["policyId"] = Guid.CreateVersion7().ToString();
        payload["lossDate"] = lossDate;
        payload["productCode"] = product;
        var envelope = await PublishAsync(ClaimReportedV1.Descriptor, claimId, payload,
            BusinessKeys.Empty.With("claimId", claimId.ToString()).With("policyId", payload["policyId"]!.GetValue<string>()));
        (await DeliverAsync("RI.ClaimReported.ClaimView", envelope)).ShouldBeTrue();
        return envelope;
    }

    public async Task<EventEnvelope> ExposureAsync(Guid claimId, Guid exposureId, string coverage = Coverage)
    {
        var payload = FinanceSlice.Sample("clm", "ExposureCreated");
        payload["exposureId"] = exposureId.ToString();
        payload["coverageCode"] = coverage;
        payload["siiLob"] = "MOTOR_VEHICLE_LIABILITY";
        var envelope = await PublishAsync(ExposureCreatedV1.Descriptor, claimId, payload,
            BusinessKeys.Empty.With("claimId", claimId.ToString()).With("exposureId", exposureId.ToString()));
        (await DeliverAsync("RI.ExposureCreated.ClaimView", envelope)).ShouldBeTrue();
        return envelope;
    }

    public async Task<EventEnvelope> ReserveAsync(Guid claimId, Guid exposureId, string newOpen, string cost = "INDEMNITY", string kind = "RESERVE")
    {
        var setId = Guid.CreateVersion7();
        var payload = FinanceSlice.Sample("clm", "ReserveChanged");
        payload["claimId"] = claimId.ToString();
        payload["exposureId"] = exposureId.ToString();
        payload["reserveLine"] = new JsonObject { ["costType"] = cost, ["category"] = "BODILY_INJURY" };
        payload["kind"] = kind;
        payload["newOpenAmount"] = Eur3(newOpen);
        payload["setId"] = setId.ToString();
        payload["recoveryId"] = kind == "RESERVE" ? null : Guid.CreateVersion7().ToString();
        var envelope = await PublishAsync(ReserveChangedV1.Descriptor, claimId, payload,
            BusinessKeys.Empty.With("claimId", claimId.ToString()).With("exposureId", exposureId.ToString())
                .With("setId", setId.ToString()).With("policyTermId", payload["policyTermId"]!.GetValue<string>()));
        (await DeliverAsync("RI.ReserveChanged.ClaimView", envelope)).ShouldBeTrue();
        return envelope;
    }

    public async Task<EventEnvelope> PaidAsync(Guid claimId, Guid exposureId, string amount, string cost = "INDEMNITY", string method = "SEPA_CT")
    {
        var paymentId = Guid.CreateVersion7();
        var transactionId = Guid.CreateVersion7();
        var payload = FinanceSlice.Sample("clm", "PaymentIssued");
        payload["claimId"] = claimId.ToString();
        payload["paymentId"] = paymentId.ToString();
        payload["transactionIds"] = new JsonArray(JsonValue.Create(transactionId.ToString()));
        payload["lines"] = new JsonArray(new JsonObject
        {
            ["lineKey"] = transactionId.ToString(), ["amount"] = Eur3(amount), ["exposureId"] = exposureId.ToString(),
            ["costType"] = cost, ["costCategory"] = "BODILY_INJURY", ["eroding"] = true,
        });
        payload["amount"] = Eur3(amount);
        payload["method"] = method;
        payload["disbursementId"] = method == "CLEARING" ? null : Guid.CreateVersion7().ToString();
        payload["fsStatementId"] = method == "CLEARING" ? Guid.CreateVersion7().ToString() : null;
        var keys = BusinessKeys.Empty.With("claimId", claimId.ToString()).With("paymentId", paymentId.ToString());
        keys = method == "CLEARING" ? keys.With("fsStatementId", payload["fsStatementId"]!.GetValue<string>()) : keys.With("disbursementId", payload["disbursementId"]!.GetValue<string>());
        var envelope = await PublishAsync(PaymentIssuedV1.Descriptor, claimId, payload, keys);
        (await DeliverAsync("RI.PaymentIssued.ClaimView", envelope)).ShouldBeTrue();
        return envelope;
    }

    public async Task<EventEnvelope> VoidedAsync(Guid claimId, Guid exposureId, string amount, string cost = "INDEMNITY")
    {
        var transactionId = Guid.CreateVersion7();
        var paymentId = Guid.CreateVersion7();
        var payload = FinanceSlice.Sample("clm", "PaymentVoided");
        payload["claimId"] = claimId.ToString();
        payload["paymentId"] = paymentId.ToString();
        payload["reversalTransactionIds"] = new JsonArray(JsonValue.Create(transactionId.ToString()));
        payload["lines"] = new JsonArray(new JsonObject
        {
            ["lineKey"] = transactionId.ToString(), ["amount"] = Eur3(amount), ["exposureId"] = exposureId.ToString(),
            ["costType"] = cost, ["costCategory"] = "BODILY_INJURY", ["eroding"] = true,
        });
        payload["amount"] = Eur3(amount);
        var envelope = await PublishAsync(PaymentVoidedV1.Descriptor, claimId, payload,
            BusinessKeys.Empty.With("claimId", claimId.ToString()).With("paymentId", paymentId.ToString()));
        (await DeliverAsync("RI.PaymentVoided.ClaimView", envelope)).ShouldBeTrue();
        return envelope;
    }

    public async Task<EventEnvelope> ClosedAsync(Guid claimId)
    {
        var payload = FinanceSlice.Sample("clm", "ClaimClosed");
        var envelope = await PublishAsync(ClaimClosedV1.Descriptor, claimId, payload, BusinessKeys.Empty.With("claimId", claimId.ToString()));
        (await DeliverAsync("RI.ClaimClosed.ClaimView", envelope)).ShouldBeTrue();
        return envelope;
    }

    public Task<(HttpResponseMessage Response, JsonNode? Body)> ByClaimAsync(Guid claimId, string? knownAt = null) =>
        Registry.SendAsync(HttpMethod.Get, $"/api/ri/v1/recoveries/list-by-claim?claimId={claimId}" + (knownAt is null ? string.Empty : $"&knownAt={Uri.EscapeDataString(knownAt)}"));

    public Task<(HttpResponseMessage Response, JsonNode? Body)> ByContractAsync(string contractId, string? knownAt = null) =>
        Registry.SendAsync(HttpMethod.Get, $"/api/ri/v1/recoveries/list-by-contract?contractId={contractId}" + (knownAt is null ? string.Empty : $"&knownAt={Uri.EscapeDataString(knownAt)}"));

    /// <summary>(incurred, paid) recoverable per participant (party id) from a list response.</summary>
    public static Dictionary<string, (decimal Incurred, decimal Paid)> PerParticipant(JsonNode? page)
    {
        var result = new Dictionary<string, (decimal, decimal)>(StringComparer.Ordinal);
        foreach (var item in page!["items"]!.AsArray())
        {
            var key = item!["participantId"]!.GetValue<string>();
            var incurred = decimal.Parse(item["recoverableIncurred"]!["amount"]!.GetValue<string>(), CultureInfo.InvariantCulture);
            var paid = decimal.Parse(item["recoverablePaid"]!["amount"]!.GetValue<string>(), CultureInfo.InvariantCulture);
            result[key] = result.TryGetValue(key, out var existing) ? (existing.Item1 + incurred, existing.Item2 + paid) : (incurred, paid);
        }

        return result;
    }

    public async Task<T> ScalarAsync<T>(string sql)
    {
        await using var command = _superuser.CreateCommand(sql);
        var value = await command.ExecuteScalarAsync(Ct);
        return value switch { null or DBNull => default!, T typed => typed, _ => (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture) };
    }

    public async ValueTask DisposeAsync()
    {
        await Registry.DisposeAsync();
        await _superuser.DisposeAsync();
    }
}
