using CoreIns.Modules.Policy.Contracts.Events;
using CoreIns.Modules.Policy.Domain.Servicing;
using CoreIns.Modules.Policy.Persistence;
using CoreIns.Platform.Contracts.Events;
using CoreIns.Platform.Events;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Policy.Commands.Change.Oos;

/// <summary>
/// Port: is a finance period locked (REQ-POL-108, G5)? FIN-OOS-GUARD supplies the real adapter over <c>IFinancePeriodService</c> (replace
/// this registration); until then no period is locked.
/// </summary>
internal interface IOosFinancePeriods
{
    /// <summary>True when postings dated <paramref name="date"/> are refused by a locked finance period.</summary>
    Task<bool> IsLockedAsync(BusinessDate date, CancellationToken cancellationToken);
}

/// <summary>The registered port until FIN-OOS-GUARD is on main: nothing is locked.</summary>
internal sealed class UnlockedFinancePeriods : IOosFinancePeriods
{
    public Task<bool> IsLockedAsync(BusinessDate date, CancellationToken cancellationToken) => Task.FromResult(false);
}

/// <summary>What a charge line of a correction carries beyond an ordinary one.</summary>
internal sealed record OosLineInfo(Guid CorrectionOf, Guid SetId);

/// <summary>Writes the parts of an out-of-sequence bind that differ from an in-sequence one: the booking date, the segments, the record and the reversal events.</summary>
internal sealed class OosApplier(PolicyDbContext db, IEventPublisher events, IOosFinancePeriods periods)
{
    /// <summary>
    /// The date the correction is booked: the day of the record, or, when its finance period is locked, the first day of the next period that
    /// is open (REQ-POL-108): POL never emits an event dated inside a locked month. A run of locked months fails closed.
    /// </summary>
    public async Task<Result<(BusinessDate Date, bool Moved)>> BookingDateAsync(BusinessDate today, CancellationToken cancellationToken)
    {
        var date = today;
        for (var month = 0; month < 36; month++)
        {
            if (!await periods.IsLockedAsync(date, cancellationToken).ConfigureAwait(false))
            {
                return (date, date != today);
            }

            var first = new DateOnly(date.Value.Year, date.Value.Month, 1).AddMonths(1);
            date = new BusinessDate(first);
        }

        return DomainError.Of(ModuleCode.POL, "DEPENDENCY-UNAVAILABLE", "No open finance period was found for the correction; it is not booked.");
    }

    /// <summary>The current segments from the effective time on (other than the one that holds it), loaded to be closed.</summary>
    public Task<List<SegmentRow>> LaterSegmentsAsync(PolicyTermId termId, Instant effectiveAt, CancellationToken cancellationToken) =>
        db.Segments.Where(s => s.TermId == termId && s.RecordedTo == null && s.ValidFrom > effectiveAt).OrderBy(s => s.ValidFrom).ToListAsync(cancellationToken);

    /// <summary>Adds the corrected segments: the part of the base segment before the new change is carried over, then one per step of the plan.</summary>
    public void AddSegments(
        OosPlan plan, SegmentRow baseSegment, IReadOnlyList<SegmentRow> closedLater, PolicyTransactionId transactionId, Instant effectiveAt, Instant t, string? newWorksheetId, Guid jobId)
    {
        var worksheets = closedLater.GroupBy(s => s.TransactionId.Value).ToDictionary(g => g.Key, g => g.First().WorksheetId);
        if (baseSegment.ValidFrom < effectiveAt)
        {
            db.Segments.Add(new SegmentRow
            {
                SegmentId = SegmentId.New(), TermId = baseSegment.TermId, PolicyId = baseSegment.PolicyId, TransactionId = baseSegment.TransactionId,
                LegalEntityId = baseSegment.LegalEntityId, ValidFrom = baseSegment.ValidFrom, ValidTo = effectiveAt, RecordedFrom = t, SnapshotHash = baseSegment.SnapshotHash,
                Snapshot = baseSegment.Snapshot, WorksheetId = baseSegment.WorksheetId,
            });
        }

        foreach (var segment in plan.Segments)
        {
            var json = JobSupport.Json(segment.Tree);
            db.Segments.Add(new SegmentRow
            {
                SegmentId = SegmentId.New(), TermId = baseSegment.TermId, PolicyId = baseSegment.PolicyId, TransactionId = transactionId, LegalEntityId = baseSegment.LegalEntityId,
                ValidFrom = segment.From, ValidTo = segment.To, RecordedFrom = t, SnapshotHash = CanonicalJson.Hash(json).Value, Snapshot = json,
                WorksheetId = segment.Source == jobId ? newWorksheetId : worksheets.GetValueOrDefault(segment.Source) ?? baseSegment.WorksheetId,
            });
        }
    }

    /// <summary>The record of the correction and the reversal / re-application events of the later transactions (one set each).</summary>
    public void Record(
        OosPlan plan, IReadOnlyList<SegmentRow> closedLater, PolicyTermRow term, PolicyId policyId, JobId jobId, PolicyTransactionId transactionId, Guid setId, Guid correctionOf,
        BusinessDate booking, bool moved, decimal netPremium, Instant t, string correlationKey)
    {
        db.OosCorrections.Add(new OosCorrectionRow
        {
            SetId = setId, PolicyId = policyId, TermId = term.TermId, LegalEntityId = term.LegalEntityId, TransactionId = transactionId, JobId = jobId,
            Mode = plan.Mode == OosMode.Net ? "NET" : "GROSS", Status = "APPLIED", OriginalRefs = JobSupport.Json(plan.Reversed.Select(r => r.Source)),
            CorrectionOf = correctionOf, BookingDate = booking, PeriodLocked = moved, NetPremium = netPremium, CreatedAt = t,
        });

        var index = 0;
        foreach (var original in plan.Reversed)
        {
            index++;
            var originalId = new PolicyTransactionId(original.Source);
            var affected = closedLater.Where(s => s.TransactionId == originalId).Select(s => s.SegmentId).ToList();
            events.Publish(new OutgoingEvent(
                EventDescriptor.From(TransactionReversedV1.Descriptor), "Policy", policyId.Value.ToString(),
                new TransactionReversedV1
                {
                    OriginalTransactionId = originalId, ReversalTransactionId = transactionId, CorrelationKey = correlationKey, AffectedSegmentIds = affected, VoidedByCancellation = false,
                },
                BusinessKeys.Empty.With("policyId", policyId.Value.ToString()).With("originalTransactionId", originalId.Value.ToString())
                    .With("reversalTransactionId", transactionId.Value.ToString()))
            {
                OccurredAt = t,
                Set = new EventSet(setId, plan.Reversed.Count, index),
            });
        }

        index = 0;
        foreach (var original in plan.Reversed)
        {
            index++;
            var originalId = new PolicyTransactionId(original.Source);
            events.Publish(new OutgoingEvent(
                EventDescriptor.From(TransactionReappliedV1.Descriptor), "Policy", policyId.Value.ToString(),
                new TransactionReappliedV1 { OriginalTransactionId = originalId, NewTransactionId = transactionId, CorrelationKey = correlationKey },
                BusinessKeys.Empty.With("policyId", policyId.Value.ToString()).With("originalTransactionId", originalId.Value.ToString())
                    .With("newTransactionId", transactionId.Value.ToString()))
            {
                OccurredAt = t,
                Set = new EventSet(setId, plan.Reversed.Count, index),
            });
        }
    }
}
