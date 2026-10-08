using System.Text.Json.Nodes;
using CoreIns.Modules.Billing.Contracts.Events;
using CoreIns.Modules.Policy.Contracts.Events;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts.Events;
using CoreIns.Platform.Events;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace CoreIns.IntegrationTests.Finance;

/// <summary>
/// Test producer for the FIN tests: publishes POL and BIL events through the real outbox (as SL-POL and SL-BIL do) with
/// payloads built from the generated contract samples (tests/CoreIns.Contracts.Tests/Generated/Samples/events), then
/// drains the outbox so the FIN handlers run as in the worker.
/// </summary>
internal sealed class FinanceSlice(ApiHostFactory factory)
{
    public const string FinanceRole = "Staff.Finance";

    private static readonly Lazy<JsonArray> PolSamples = new(() => Samples("pol"));
    private static readonly Lazy<JsonArray> BilSamples = new(() => Samples("bil"));
    private static readonly Lazy<JsonArray> ClmSamples = new(() => Samples("clm"));

    /// <summary>A sample payload of an event type (a deep copy to modify).</summary>
    public static JsonObject Sample(string module, string eventType) =>
        (module switch { "pol" => PolSamples.Value, "clm" => ClmSamples.Value, _ => BilSamples.Value })
        .Select(e => e!.AsObject())
        .First(e => e["eventType"]!.GetValue<string>() == eventType)["payload"]!.DeepClone().AsObject();

    /// <summary>Publishes one event through the outbox in its own transaction and returns its envelope.</summary>
    public async Task<EventEnvelope> PublishAsync(
        EventContract contract, string aggregateType, string aggregateId, JsonObject payload, BusinessKeys keys, EventSet? set = null, string legalEntity = "GR-TEST")
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<RequestContext>();
        context.Actor = ActorRef.Service("fin-test-producer");
        context.LegalEntity = LegalEntityCode.Parse(legalEntity);
        context.Jurisdiction = Jurisdiction.Parse("GR");
        context.ConfigurationHash = ConfigurationHash.Parse(new string('c', 64));
        var session = scope.ServiceProvider.GetRequiredService<DbSession>();
        await using var transaction = await session.BeginTransactionAsync(TestContext.Current.CancellationToken);
        var envelope = scope.ServiceProvider.GetRequiredService<IEventPublisher>()
            .Publish(new OutgoingEvent(EventDescriptor.From(contract), aggregateType, aggregateId, payload, keys) { Set = set });
        await transaction.CommitAsync(TestContext.Current.CancellationToken);
        return envelope;
    }

    /// <summary>Runs the outbox dispatcher until it is empty (the worker's job).</summary>
    public Task<OutboxBatchResult> DrainAsync() =>
        factory.Services.GetRequiredService<OutboxProcessor>().DrainAsync(TestContext.Current.CancellationToken);

    /// <summary>POL PolicyBound for a policy (FIN reads number, term, transaction, product and artefact hash).</summary>
    public Task<EventEnvelope> PolicyBoundAsync(Policy policy)
    {
        var payload = Sample("pol", "PolicyBound");
        payload["policyId"] = policy.PolicyId.ToString();
        payload["policyNumber"] = policy.Number;
        payload["termId"] = policy.TermId.ToString();
        payload["transactionId"] = policy.TransactionId.ToString();
        payload["productCode"] = policy.ProductCode;
        payload["productVersion"] = "1.0";
        payload["artefactHash"] = policy.ArtefactHash;
        return PublishAsync(PolicyBoundV1.Descriptor, "Policy", policy.PolicyId.ToString(), payload,
            BusinessKeys.Empty.With("policyId", policy.PolicyId.ToString()).With("policyTermId", policy.TermId.ToString())
                .With("transactionId", policy.TransactionId.ToString()).With("jobId", Guid.CreateVersion7().ToString())
                .With("quoteId", Guid.CreateVersion7().ToString()));
    }

    /// <summary>BIL BillingEntryPosted with the given sub-ledger lines.</summary>
    public Task<EventEnvelope> EntryAsync(Guid billingAccount, string entryType, string date, params JsonObject[] lines)
    {
        var entryId = Guid.CreateVersion7();
        var payload = Sample("bil", "BillingEntryPosted");
        payload["entryId"] = entryId.ToString();
        payload["eventType"] = entryType;
        payload["accountingDate"] = date;
        payload["businessDate"] = date;
        payload["lines"] = new JsonArray([.. lines]);
        return PublishAsync(BillingEntryPostedV1.Descriptor, "BillingAccount", billingAccount.ToString(), payload,
            BusinessKeys.Empty.With("entryId", entryId.ToString()).With("billingAccountId", billingAccount.ToString()));
    }

    /// <summary>
    /// BIL BillingEntryPosted for a disbursement (SL2-BIL-DISB: aggregate Disbursement, no billing account): one leg pair
    /// with the disbursement dimensions BIL sets (disbursementId, sourceType, sourceId = claim payment id, claimId).
    /// </summary>
    public Task<EventEnvelope> DisbursementEntryAsync(
        string entryType, string date, Guid disbursementId, Guid claimPaymentId, Guid claimId, string amount, string debitAccount, string creditAccount)
    {
        var entryId = Guid.CreateVersion7();
        var payload = Sample("bil", "BillingEntryPosted");
        payload["entryId"] = entryId.ToString();
        payload["eventType"] = entryType;
        payload["accountingDate"] = date;
        payload["businessDate"] = date;
        JsonObject Leg(string account, string side) => new()
        {
            ["account"] = account,
            ["side"] = side,
            ["amount"] = new JsonObject { ["amount"] = amount, ["currency"] = "EUR" },
            ["dimensions"] = new JsonObject
            {
                ["legalEntity"] = "GR-TEST",
                ["jurisdiction"] = "GR",
                ["ruleId"] = entryType == "DISBURSEMENT_RELEASED" ? "BLR-DISB-RELEASED-CLM" : "BLR-DISB-CLEARED",
                ["billingAccountId"] = null,
                ["disbursementId"] = disbursementId.ToString(),
                ["sourceType"] = "CLM_CLAIM_PAYMENT",
                ["sourceId"] = claimPaymentId.ToString(),
                ["claimId"] = claimId.ToString(),
            },
        };
        payload["lines"] = new JsonArray(Leg(debitAccount, "DEBIT"), Leg(creditAccount, "CREDIT"));
        return PublishAsync(BillingEntryPostedV1.Descriptor, "Disbursement", disbursementId.ToString(), payload,
            BusinessKeys.Empty.With("entryId", entryId.ToString()).With("disbursementId", disbursementId.ToString())
                .With("sourceId", claimPaymentId.ToString()).With("claimId", claimId.ToString()));
    }

    /// <summary>One sub-ledger line with the dimension keys agreed with SL-BIL.</summary>
    public static JsonObject Line(string account, string side, string amount, Policy? policy = null, Guid? billingAccount = null,
        string? chargeType = null, string? category = null, string? coverage = null, Guid? invoiceId = null, Guid? receiptId = null, string currency = "EUR") => new()
    {
        ["account"] = account,
        ["side"] = side,
        ["amount"] = new JsonObject { ["amount"] = amount, ["currency"] = currency },
        ["dimensions"] = new JsonObject
        {
            ["legalEntity"] = "GR-TEST",
            ["jurisdiction"] = "GR",
            ["billingAccountId"] = billingAccount?.ToString(),
            ["policyId"] = policy?.PolicyId.ToString(),
            ["policyTermId"] = policy?.TermId.ToString(),
            ["transactionId"] = policy?.TransactionId.ToString(),
            ["chargeId"] = chargeType is null ? null : Guid.CreateVersion7().ToString(),
            ["chargeType"] = chargeType,
            ["chargeCategory"] = category,
            ["coverageCode"] = coverage,
            ["productCode"] = policy?.ProductCode,
            ["billMode"] = "DIRECT_BILL",
            ["invoiceId"] = invoiceId?.ToString(),
            ["invoiceItemId"] = null,
            ["receiptId"] = receiptId?.ToString(),
            ["allocationId"] = null,
            ["ruleId"] = "TEST-RULE",
        },
    };

    public static async Task<T> ScalarAsync<T>(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static JsonArray Samples(string module) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(RepositoryPaths.Root, "tests", "CoreIns.Contracts.Tests", "Generated", "Samples", "events", module + ".json")))!.AsArray();
}

/// <summary>A synthetic bound policy (ids, number and the PFC artefact it was written on).</summary>
internal sealed record Policy(Guid PolicyId, string Number, Guid TermId, Guid TransactionId, string ProductCode, string ArtefactHash)
{
    public static Policy New(string number, string productCode, string artefactHash) =>
        new(Guid.CreateVersion7(), number, Guid.CreateVersion7(), Guid.CreateVersion7(), productCode, artefactHash);
}
