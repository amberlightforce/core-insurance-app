using System.Text.Json;
using System.Text.Json.Serialization;
using CoreIns.Modules.Finance.Domain;
using CoreIns.Modules.Finance.Persistence;
using CoreIns.Modules.Product.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Finance.Posting;

/// <summary>BIL BillingEntryPosted: FIN's posting source (REQ-FIN-036, D-SLC-12).</summary>
internal sealed class BillingEntryPostedHandler(Intake intake) : IEventHandler<JsonElement>
{
    public async Task HandleAsync(EventEnvelope envelope, JsonElement payload, CancellationToken cancellationToken)
    {
        var row = await intake.ReceiveAsync(envelope, cancellationToken).ConfigureAwait(false);
        if (row is { Status: BusinessEventStatus.Received })
        {
            await intake.PostAsync(row, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// CLM ReserveChanged and PaymentIssued: FIN's posting sources for claims (PRD-09 events table, REQ-FIN-037, D-SL2-08).
/// Idempotent on the source event id; each fact posts on its own, so PaymentIssued and BIL's DISBURSEMENT_RELEASED
/// net GL-2510 to zero per payment in either arrival order (D-ARC-26).
/// </summary>
internal sealed class ClaimFactHandler(Intake intake) : IEventHandler<JsonElement>
{
    public async Task HandleAsync(EventEnvelope envelope, JsonElement payload, CancellationToken cancellationToken)
    {
        var row = await intake.ReceiveAsync(envelope, cancellationToken).ConfigureAwait(false);
        if (row is { Status: BusinessEventStatus.Received })
        {
            await intake.PostAsync(row, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Context-only events (POL ChargeDeltaEmitted; BIL InvoiceIssued, PaymentReceived, CashAllocated, Disbursement*; CLM
/// ClaimReported, ExposureCreated, TransactionSetApproved, ClaimClosed): recorded, never journalised.
/// </summary>
internal sealed class ContextEventHandler(Intake intake) : IEventHandler<JsonElement>
{
    public async Task HandleAsync(EventEnvelope envelope, JsonElement payload, CancellationToken cancellationToken)
    {
        var row = await intake.ReceiveAsync(envelope, cancellationToken).ConfigureAwait(false);
        if (row is { Status: BusinessEventStatus.Received })
        {
            // The catalogue says POSTING but FIN has no posting mapping for this event: an intake exception, not a journal.
            await intake.PostAsync(row, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// FIN's view of POL PolicyBound: only the fields FIN reads (a tolerant reader; the SL-POL contract change makes
/// producerOfRecord and accountId optional, which the generated v1.0 record still requires).
/// </summary>
internal sealed record PolicyBoundContext
{
    [JsonPropertyName("policyId")]
    public Guid PolicyId { get; init; }

    [JsonPropertyName("policyNumber")]
    public string PolicyNumber { get; init; } = string.Empty;

    [JsonPropertyName("termId")]
    public Guid TermId { get; init; }

    [JsonPropertyName("transactionId")]
    public Guid TransactionId { get; init; }

    [JsonPropertyName("productCode")]
    public string ProductCode { get; init; } = string.Empty;

    [JsonPropertyName("productVersion")]
    public string ProductVersion { get; init; } = string.Empty;

    [JsonPropertyName("artefactHash")]
    public string ArtefactHash { get; init; } = string.Empty;
}

/// <summary>
/// POL PolicyBound (CONTEXT): keeps the policy business key, product and artefact per term, loads the artefact's charge
/// types (GL keys, immutable per hash) from PFC once, then releases the BIL entries waiting for this policy (REQ-FIN-040).
/// </summary>
internal sealed class PolicyBoundHandler(
    Intake intake, FinanceDbContext db, ReferenceData reference, IProductChargeTypeService chargeTypes, IClock clock) : IEventHandler<PolicyBoundContext>
{
    public async Task HandleAsync(EventEnvelope envelope, PolicyBoundContext payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(payload);
        var row = await intake.ReceiveAsync(envelope, cancellationToken).ConfigureAwait(false);
        if (row is not { Status: BusinessEventStatus.NoPosting, LegalEntityId: { } legalEntity })
        {
            return;
        }

        var term = Intake.Dependency(payload.TermId, null);
        var policy = Intake.Dependency(null, payload.PolicyId);
        await reference.LockAsync(term, cancellationToken).ConfigureAwait(false);
        await reference.LockAsync(policy, cancellationToken).ConfigureAwait(false);

        var hash = payload.ArtefactHash.ToLowerInvariant();
        var loaded = await LoadChargeTypesAsync(hash, cancellationToken).ConfigureAwait(false);
        var existing = await db.PolicyContexts.SingleOrDefaultAsync(p => p.PolicyTermId == payload.TermId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            db.PolicyContexts.Add(new PolicyContextRow
            {
                PolicyTermId = payload.TermId,
                LegalEntityId = legalEntity,
                PolicyId = payload.PolicyId,
                PolicyNumber = payload.PolicyNumber,
                TransactionId = payload.TransactionId,
                ProductCode = payload.ProductCode,
                ProductVersion = payload.ProductVersion,
                ArtefactHash = hash,
                ChargeTypesLoaded = loaded,
                SourceEventId = envelope.EventId.Value,
                AggregateSequence = envelope.AggregateSequence,
                RecordedAt = clock.Now,
                RecordVersion = 1,
            });
        }
        else if (existing.AggregateSequence < envelope.AggregateSequence)
        {
            // D-ARC-26: only a newer event of the policy aggregate replaces the context.
            existing.PolicyNumber = payload.PolicyNumber;
            existing.TransactionId = payload.TransactionId;
            existing.ProductCode = payload.ProductCode;
            existing.ProductVersion = payload.ProductVersion;
            existing.ArtefactHash = hash;
            existing.ChargeTypesLoaded = loaded;
            existing.SourceEventId = envelope.EventId.Value;
            existing.AggregateSequence = envelope.AggregateSequence;
            existing.RecordedAt = clock.Now;
            existing.RecordVersion++;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await intake.ReleaseAsync([term, policy], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Loads the charge-type view of an artefact from PFC (pfc.ChargeType.list by hash) unless already present. Returns
    /// false when PFC does not know the artefact: lines that need a GL key then become intake exceptions (NO_CHARGE_TYPE).
    /// </summary>
    private async Task<bool> LoadChargeTypesAsync(string hash, CancellationToken cancellationToken)
    {
        if (await db.ChargeTypes.AnyAsync(c => c.ArtefactHash == hash, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        if (!Sha256Hash.TryParse(hash, out var artefact))
        {
            return false;
        }

        var now = clock.Now;
        string? cursor = null;
        try
        {
            do
            {
                var page = await chargeTypes.ListAsync(cursor, 200, artefact, null, cancellationToken).ConfigureAwait(false);
                foreach (var item in page.Items)
                {
                    db.ChargeTypes.Add(new ChargeTypeViewRow
                    {
                        ArtefactHash = hash,
                        ChargeType = item.Code,
                        Category = JsonSerializer.Serialize(item.Category).Trim('"'),
                        GlKey = item.GlKey,
                        Coverage = item.Coverage,
                        LoadedAt = now,
                    });
                }

                cursor = page.NextCursor;
            }
            while (cursor is not null);
        }
        catch (DomainException)
        {
            return false;
        }

        return true;
    }
}
