using System.Text.Json;
using CoreIns.Modules.Claims.Contracts.Api;
using CoreIns.Modules.Claims.Contracts.Events;
using CoreIns.Modules.Claims.Domain;
using CoreIns.Modules.Claims.Persistence;
using CoreIns.Modules.Claims.Queries;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Claims.Commands;

/// <summary><c>clm.Claim.merge</c> (BO-CLM-02).</summary>
internal sealed record MergeClaims(ClaimMergeRequest Request) : ICommand<ClaimMergeResponse>;

/// <summary>Shape rules.</summary>
internal sealed class MergeClaimsValidator : AbstractValidator<MergeClaims>
{
    public MergeClaimsValidator()
    {
        RuleFor(c => c.Request.ExpectedRecordVersion).GreaterThanOrEqualTo(1);
        RuleFor(c => c.Request.ReasonCode).NotEmpty().Matches("^[A-Z][A-Z0-9_]{0,63}$").WithErrorCode("CODE");
        RuleFor(c => c.Request).Must(r => r.SurvivorClaimId != r.DuplicateClaimId).WithErrorCode("SAME_CLAIM").WithMessage("A claim cannot merge into itself.");
    }
}

/// <summary>
/// Merges a duplicate claim into the surviving one (BO-CLM-02, REQ-CLM-042): both claims must be open, of the same legal entity, policy
/// and loss date. The duplicate keeps its claim number and closes with outcome DUPLICATE, <c>duplicate_of_claim_id</c> pointing at the
/// survivor and the reason recorded; its open exposures close with it and their statutory clocks stop. Money never moves: the merge is
/// refused (CLM-ERR-MERGE-FINANCIALS, <c>errors[]</c> with the reason codes) while the duplicate has any financial transaction, payment,
/// transaction set, friendly-settlement case or open recovery, so reserves, payments and recoveries are neither double counted nor silently
/// relocated and the FIN, RI and DAT consumers keep one consistent set of claim events. Publishes <c>ClaimClosed</c> (DUPLICATE) for the
/// duplicate in the same transaction. TODO(hardening): move the duplicate's exposures (and, with them, claimants and incidents) to the survivor
/// when the duplicate carries no money; today they stay on the closed duplicate.
/// </summary>
internal sealed class MergeClaimsHandler(
    ClaimsDbContext db,
    RequestContext context,
    IClock clock,
    IEventPublisher events,
    ClaimProtection protection,
    ExposureClocks exposureClocks,
    ClaimReader reader) : ICommandHandler<MergeClaims, ClaimMergeResponse>
{
    public async Task<Result<ClaimMergeResponse>> HandleAsync(MergeClaims command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = protection.Current(context);

        // Lock both claims in a fixed order so two merges (or a merge and another command) cannot deadlock.
        ClaimRow? survivor = null;
        ClaimRow? duplicate = null;
        foreach (var id in new[] { request.SurvivorClaimId, request.DuplicateClaimId }.Order())
        {
            var row = await ClaimSupport.LoadAsync(db, legalEntity, new ClaimId(id), cancellationToken).ConfigureAwait(false);
            if (row is null)
            {
                return ClaimSupport.NotFound("claim");
            }

            if (id == request.SurvivorClaimId)
            {
                survivor = row;
            }
            else
            {
                duplicate = row;
            }
        }

        if (survivor is null || duplicate is null)
        {
            return ClaimSupport.NotFound("claim");
        }

        if (duplicate.RecordVersion != request.ExpectedRecordVersion)
        {
            return ClaimSupport.Stale(duplicate.RecordVersion);
        }

        if (survivor.PolicyId != duplicate.PolicyId || survivor.LossDate != duplicate.LossDate
            || survivor.Status != ClaimStates.Open || duplicate.Status != ClaimStates.Open)
        {
            return DomainError.Of(ModuleCode.CLM, "MERGE-NOT-DUPLICATE", "Only two open claims of the same policy and loss date can be merged.");
        }

        var closed = ClaimStateModel.Fire(ClaimStates.FromColumns(duplicate.Status, duplicate.SubStatus), ClaimTrigger.Close);
        if (closed.IsFailure)
        {
            return closed.Error!;
        }

        var reasons = new List<string>();
        if (await db.FinancialTransactions.AsNoTracking().AnyAsync(t => t.ClaimId == duplicate.ClaimId, cancellationToken).ConfigureAwait(false))
        {
            reasons.Add("FINANCIAL_TRANSACTIONS");
        }

        if (await db.ClaimPayments.AsNoTracking().AnyAsync(p => p.ClaimId == duplicate.ClaimId, cancellationToken).ConfigureAwait(false))
        {
            reasons.Add("PAYMENTS");
        }

        if (await db.TransactionSets.AsNoTracking().AnyAsync(s => s.ClaimId == duplicate.ClaimId, cancellationToken).ConfigureAwait(false))
        {
            reasons.Add("TRANSACTION_SETS");
        }

        if (await db.Recoveries.AsNoTracking().AnyAsync(r => r.ClaimId == duplicate.ClaimId, cancellationToken).ConfigureAwait(false))
        {
            reasons.Add("RECOVERIES");
        }

        if (await db.FsCases.AsNoTracking().AnyAsync(f => f.ClaimId == duplicate.ClaimId, cancellationToken).ConfigureAwait(false))
        {
            reasons.Add("FRIENDLY_SETTLEMENT");
        }

        if (reasons.Count > 0)
        {
            var guards = reasons.Select(r => new CloseGuardError { Code = r, ExposureIds = [] }).ToList();
            return new DomainError(ErrorCode.For(ModuleCode.CLM, "MERGE-FINANCIALS"), "The duplicate claim has financial activity; settle or reverse it before merging.")
            {
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["closeGuardErrors"] = JsonSerializer.Serialize(guards, SharedKernelJson.Options) },
                FieldErrors = [.. reasons.Select(r => new FieldError("duplicateClaimId", r, r))],
            };
        }

        var now = clock.Now;
        var outcome = Codes.Of(ClaimOutcomeCode.Duplicate);
        var open = await db.Exposures.Where(e => e.ClaimId == duplicate.ClaimId && e.Status == ClaimStates.Open).OrderBy(e => e.Sequence)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var exposure in open)
        {
            var exposureClosed = ExposureStateModel.Machine.Fire(ExposureStates.FromColumns(exposure.Status, exposure.SubStatus), ExposureTrigger.Close);
            if (exposureClosed.IsFailure)
            {
                return DomainError.Of(ModuleCode.CLM, "ILLEGAL-TRANSITION", $"Exposure {exposure.ExposureNumber} cannot close.");
            }

            (exposure.Status, exposure.SubStatus) = ExposureStates.ToColumns(exposureClosed.Value);
            exposure.Outcome = outcome;
            exposure.ClosedAt = now;
            exposure.UpdatedAt = now;
            exposure.RecordVersion++;
        }

        (duplicate.Status, duplicate.SubStatus) = ClaimStates.ToColumns(closed.Value);
        duplicate.Outcome = outcome;
        duplicate.CloseReasonCode = request.ReasonCode;
        duplicate.DuplicateOfClaimId = survivor.ClaimId;
        duplicate.DuplicateReasonCode = request.ReasonCode;
        duplicate.ClosedAt = now;
        duplicate.UpdatedAt = now;
        duplicate.RecordVersion++;
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ClaimSupport.Stale();
        }

        foreach (var exposure in open.Where(e => e.StatutoryClocks.StartsWith(ExposureClocks.Prefix, StringComparison.Ordinal)))
        {
            await exposureClocks.CancelLiveAsync(
                exposure.StatutoryClocks, $"Claim {duplicate.ClaimNumber.Value} merged into {survivor.ClaimNumber.Value} ({request.ReasonCode}).", cancellationToken).ConfigureAwait(false);
        }

        events.Publish(new OutgoingEvent(
            EventDescriptor.From(ClaimClosedV1.Descriptor), ClaimEvents.AggregateType, duplicate.ClaimId.Value.ToString(),
            new ClaimClosedV1 { Outcome = outcome, Totals = [] },
            BusinessKeys.Empty.With("claimId", duplicate.ClaimId.Value.ToString())));

        return new ClaimMergeResponse
        {
            Survivor = (await reader.GetSummaryAsync(legalEntity, survivor.ClaimId, cancellationToken).ConfigureAwait(false))!,
            Duplicate = (await reader.GetSummaryAsync(legalEntity, duplicate.ClaimId, cancellationToken).ConfigureAwait(false))!,
        };
    }
}

/// <summary>Audit facts of <c>clm.Claim.merge</c>: the duplicate is the object; survivor, reason and outcome are recorded.</summary>
internal sealed class MergeClaimsAuditor : ICommandAuditor<MergeClaims, ClaimMergeResponse>
{
    public CommandAuditFacts Describe(MergeClaims command, Result<ClaimMergeResponse>? result)
    {
        var request = command.Request;
        var keys = BusinessKeys.Empty.With("claimId", request.DuplicateClaimId.ToString()).With("survivorClaimId", request.SurvivorClaimId.ToString());
        if (result is not { IsSuccess: true } success)
        {
            return new CommandAuditFacts { ObjectRef = ObjectRef.For(ModuleCode.CLM, "Claim", new ClaimId(request.DuplicateClaimId)), BusinessKeys = keys };
        }

        return new CommandAuditFacts
        {
            ObjectRef = ObjectRef.For(ModuleCode.CLM, "Claim", success.Value.Duplicate.ClaimId),
            ObjectNumber = success.Value.Duplicate.ClaimNumber.Value,
            BusinessKeys = keys,
            Changes = AuditDiff.Compute(
                new { status = "OPEN" },
                new { status = "CLOSED", outcome = "DUPLICATE", mergedInto = success.Value.Survivor.ClaimNumber.Value, reasonCode = request.ReasonCode }),
        };
    }
}
