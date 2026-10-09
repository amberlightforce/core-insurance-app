using CoreIns.Modules.Claims.Contracts.Events;
using CoreIns.Modules.Claims.Domain;
using CoreIns.Modules.Claims.Persistence;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CoreIns.Modules.Claims.Commands;

/// <summary>
/// Internal command run by the <c>PolicyChanged</c> / <c>PolicyCancelled</c> consumers (REQ-CLM-057): look at every open
/// claim of the policy whose loss is on or after the change's effective date. Returns the number of demands raised.
/// </summary>
/// <param name="PolicyId">The policy the POL event is about.</param>
/// <param name="EffectiveDate">Effective date of the change or cancellation.</param>
/// <param name="CauseEventId">Event id of the POL event (the idempotency key of a demand together with the claim).</param>
/// <param name="CauseEventType">Event type of the POL event (PolicyChanged, PolicyCancelled).</param>
internal sealed record RaiseReverification(PolicyId PolicyId, BusinessDate EffectiveDate, Guid CauseEventId, string CauseEventType) : ICommand<int>;

/// <summary>
/// For each open claim on the policy with a loss date at or after the effective date, asks POL (<c>pol.Snapshot.get</c> by
/// the claim's stored ref) whether the snapshot is superseded (D-SL3-03 c). If so it records one demand per claim and cause
/// (unique index), sets <c>snapshot_status = REVERIFICATION_REQUIRED</c> and publishes <c>ReverificationRequired</c> in the
/// same transaction. It NEVER changes the claim's ref, coverage, exposures or reserves (REQ-CLM-002): a human keeps or
/// adopts later (<c>clm.Coverage.reverify</c>). A POL outage fails the handler so the outbox retries; a ref POL cannot
/// resolve is logged and skipped (no new ref to propose). No personal data is logged or published.
/// </summary>
internal sealed partial class RaiseReverificationHandler(
    ClaimsDbContext db,
    RequestContext context,
    IClock clock,
    IEventPublisher events,
    ClaimProtection protection,
    ICoverageSource coverage,
    ILogger<RaiseReverificationHandler> logger) : ICommandHandler<RaiseReverification, int>
{
    public async Task<Result<int>> HandleAsync(RaiseReverification command, CancellationToken cancellationToken)
    {
        var legalEntity = protection.Current(context);
        var open = ClaimStates.Open;
        var onPolicy = await db.Claims.AsNoTracking()
            .Where(c => c.LegalEntityId == legalEntity && c.PolicyId == command.PolicyId && c.Status == open)
            .Select(c => new { c.ClaimId, c.LossDate })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var candidates = onPolicy.Where(c => c.LossDate >= command.EffectiveDate).Select(c => c.ClaimId).OrderBy(id => id.Value).ToList();

        var raised = 0;
        foreach (var claimId in candidates)
        {
            // Lock order: claim rows by id; the lock serialises a redelivery against the first delivery (PITFALLS 15).
            var claim = await ClaimSupport.LoadAsync(db, legalEntity, claimId, cancellationToken).ConfigureAwait(false);
            if (claim is null || claim.Status != open || claim.LossDate < command.EffectiveDate)
            {
                continue;
            }

            if (await db.Reverifications.AnyAsync(r => r.ClaimId == claimId && r.CauseEventId == command.CauseEventId, cancellationToken).ConfigureAwait(false))
            {
                LogAlreadyRaised(logger, command.CauseEventType);
                continue;
            }

            var read = await coverage.ReadByRefAsync(claim.SnapshotRef, cancellationToken).ConfigureAwait(false);
            if (read.Outcome == SnapshotReadOutcome.Unavailable)
            {
                // Retry later through the outbox; never guess that nothing changed.
                throw new DomainException(DomainError.Of(ModuleCode.CLM, "DEPENDENCY-UNAVAILABLE", "The policy service could not answer the supersession check."));
            }

            if (read.Outcome == SnapshotReadOutcome.Unverified)
            {
                LogUnresolved(logger, command.CauseEventType);
                continue;
            }

            var supersession = read.Facts!.Supersession;
            if (supersession is not { Superseded: true } || string.IsNullOrWhiteSpace(supersession.SuccessorRef)
                || string.Equals(supersession.SuccessorRef, claim.SnapshotRef, StringComparison.Ordinal))
            {
                LogNotSuperseded(logger, command.CauseEventType);
                continue;
            }

            var now = clock.Now;
            var row = new ReverificationRow
            {
                ReverificationId = Guid.CreateVersion7(),
                ClaimId = claim.ClaimId,
                CauseEventId = command.CauseEventId,
                CauseEventType = command.CauseEventType,
                OldSnapshotRef = claim.SnapshotRef,
                NewSnapshotRef = supersession.SuccessorRef,
                RaisedAt = now,
                Status = ReverificationRow.Open,
                LegalEntityId = legalEntity,
                Jurisdiction = claim.Jurisdiction,
                CreatedAt = now,
                CreatedBy = context.Actor.ToString(),
            };
            db.Reverifications.Add(row);

            // The status moves; the ref, coverage, exposures and reserves do not (REQ-CLM-002).
            claim.SnapshotStatus = Codes.Of(SnapshotStatus.ReverificationRequired);
            claim.UpdatedAt = now;
            claim.RecordVersion++;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            events.Publish(new OutgoingEvent(
                EventDescriptor.From(ReverificationRequiredV1.Descriptor),
                ClaimEvents.AggregateType,
                claim.ClaimId.Value.ToString(),
                new ReverificationRequiredV1
                {
                    OldSnapshotRef = row.OldSnapshotRef,
                    NewSnapshotRef = row.NewSnapshotRef,
                    CauseEventId = row.CauseEventId,
                    CauseEventType = row.CauseEventType,
                    ClaimId = claim.ClaimId,
                },
                BusinessKeys.Empty.With("claimId", claim.ClaimId.Value.ToString())));
            raised++;
        }

        return raised;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "{CauseEventType}: snapshot of a claim is not superseded; nothing recorded")]
    private static partial void LogNotSuperseded(ILogger logger, string causeEventType);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{CauseEventType}: demand already recorded for this claim and cause")]
    private static partial void LogAlreadyRaised(ILogger logger, string causeEventType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{CauseEventType}: POL could not resolve a claim's stored snapshot ref; skipped")]
    private static partial void LogUnresolved(ILogger logger, string causeEventType);
}

/// <summary>Audit facts of the internal re-verification scan: the policy and the cause, no claim data.</summary>
internal sealed class RaiseReverificationAuditor : ICommandAuditor<RaiseReverification, int>
{
    public CommandAuditFacts Describe(RaiseReverification command, Result<int>? result) => new()
    {
        ObjectRef = ObjectRef.For(ModuleCode.POL, "Policy", command.PolicyId),
        BusinessKeys = BusinessKeys.Empty.With("policyId", command.PolicyId.Value.ToString()).With("causeEventId", command.CauseEventId.ToString()),
        Changes = result is { IsSuccess: true } ok ? AuditDiff.Compute(null, new { causeEventType = command.CauseEventType, raised = ok.Value }) : [],
    };
}
