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
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Claims.Commands;

/// <summary><c>clm.Claim.reopen</c> (BO-CLM-02, REQ-CLM-074).</summary>
internal sealed record ReopenClaim(ClaimReopenRequest Request) : ICommand<ClaimReopenResponse>;

/// <summary>Shape rules.</summary>
internal sealed class ReopenClaimValidator : AbstractValidator<ReopenClaim>
{
    public ReopenClaimValidator()
    {
        RuleFor(c => c.Request.ExpectedRecordVersion).GreaterThanOrEqualTo(1);
        RuleFor(c => c.Request.ReasonCode).NotEmpty().Matches("^[A-Z][A-Z0-9_]{0,63}$").WithErrorCode("CODE");
    }
}

/// <summary>
/// Reopens a closed claim: Closed to Open(InProgress) with a coded reason (REQ-CLM-074). The exposures that closed together with the claim
/// (same close instant) reopen with it, their statutory clocks through <see cref="ExposureClocks"/> (paused clocks resume, cancelled ones start
/// afresh); exposures closed earlier on their own stay closed and reopen through <c>clm.Exposure.reopen</c>. The claim's outcome and close time
/// clear, <c>reopen_count</c> grows, and the reserve view is the derived balance, which the reopen does not touch. A claim closed as DUPLICATE
/// (merged into another) refuses with CLM-ERR-ILLEGAL-TRANSITION. The close guard stays in force: reopening does not weaken it, and the next
/// close checks it again. Publishes <c>ClaimReopened</c> and one <c>ExposureReopened</c> per reopened exposure in the same transaction.
/// Role-based (<c>clm.Claim.reopen</c> is granted to the claims manager); TODO(hardening): the CLM.REOPEN authority type with the authority engine.
/// </summary>
internal sealed class ReopenClaimHandler(
    ClaimsDbContext db,
    RequestContext context,
    IClock clock,
    IEventPublisher events,
    ClaimProtection protection,
    ExposureClocks exposureClocks,
    IOptions<ClaimsOptions> options,
    Microsoft.Extensions.Hosting.IHostEnvironment environment,
    ClaimReader reader) : ICommandHandler<ReopenClaim, ClaimReopenResponse>
{
    public async Task<Result<ClaimReopenResponse>> HandleAsync(ReopenClaim command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = protection.Current(context);
        var claim = await ClaimSupport.LoadAsync(db, legalEntity, request.ClaimId, cancellationToken).ConfigureAwait(false);
        if (claim is null)
        {
            return ClaimSupport.NotFound("claim");
        }

        if (claim.RecordVersion != request.ExpectedRecordVersion)
        {
            return ClaimSupport.Stale(claim.RecordVersion);
        }

        if (claim.DuplicateOfClaimId is not null && claim.Outcome == Codes.Of(ClaimOutcomeCode.Duplicate))
        {
            return DomainError.Of(ModuleCode.CLM, "ILLEGAL-TRANSITION", "A claim merged into another as a duplicate cannot be reopened.");
        }

        var reopened = ClaimStateModel.Fire(ClaimStates.FromColumns(claim.Status, claim.SubStatus), ClaimTrigger.Reopen);
        if (reopened.IsFailure)
        {
            return reopened.Error!;
        }

        var now = clock.Now;
        var reason = $"Claim {claim.ClaimNumber.Value} reopened ({request.ReasonCode}).";
        var closedWithClaim = await db.Exposures
            .Where(e => e.ClaimId == claim.ClaimId && e.Status == ClaimStates.Closed && e.ClosedAt == claim.ClosedAt)
            .OrderBy(e => e.Sequence).ToListAsync(cancellationToken).ConfigureAwait(false);
        var production = Microsoft.Extensions.Hosting.HostEnvironmentEnvExtensions.IsProduction(environment);
        var nextVersion = claim.RecordVersion + 1;
        foreach (var exposure in closedWithClaim)
        {
            var exposureReopened = ExposureStateModel.Machine.Fire(ExposureStates.FromColumns(exposure.Status, exposure.SubStatus), ExposureTrigger.Reopen);
            if (exposureReopened.IsFailure)
            {
                return DomainError.Of(ModuleCode.CLM, "ILLEGAL-TRANSITION", $"Exposure {exposure.ExposureNumber} cannot reopen.");
            }

            var clocks = await exposureClocks.ReopenAsync(
                exposure.ExposureId, exposure.StatutoryClocks, claim.NoticeOn.StartOfDayIn(options.Value.Zone), $"reopen-claim-{nextVersion}-{exposure.Sequence}",
                reason, production, cancellationToken).ConfigureAwait(false);
            if (clocks.IsFailure)
            {
                return clocks.Error!;
            }

            exposure.StatutoryClocks = clocks.Value;
            (exposure.Status, exposure.SubStatus) = ExposureStates.ToColumns(exposureReopened.Value);
            exposure.Outcome = null;
            exposure.ClosedAt = null;
            exposure.UpdatedAt = now;
            exposure.RecordVersion++;
        }

        (claim.Status, claim.SubStatus) = ClaimStates.ToColumns(reopened.Value);
        claim.Outcome = null;
        claim.CloseReasonCode = null;
        claim.ClosedAt = null;
        claim.ReopenCount++;
        claim.UpdatedAt = now;
        claim.RecordVersion = nextVersion;
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ClaimSupport.Stale();
        }
        catch (DbUpdateException ex) when (ClaimSupport.IsUniqueViolation(ex, "ux_exposure_open_coverage_claimant_incident"))
        {
            return DomainError.Of(ModuleCode.CLM, "EXPOSURE-DUPLICATE", "An open exposure exists for the coverage, claimant and incident of a closed exposure; close it first.");
        }

        var keys = BusinessKeys.Empty.With("claimId", claim.ClaimId.Value.ToString());
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(ClaimReopenedV1.Descriptor), ClaimEvents.AggregateType, claim.ClaimId.Value.ToString(),
            new ClaimReopenedV1 { ReopenReason = request.ReasonCode, Totals = [] }, keys));
        foreach (var exposure in closedWithClaim)
        {
            events.Publish(new OutgoingEvent(
                EventDescriptor.From(ExposureReopenedV1.Descriptor), ClaimEvents.AggregateType, claim.ClaimId.Value.ToString(),
                new ExposureReopenedV1 { ExposureId = exposure.ExposureId, ExposureNumber = exposure.ExposureNumber, ReasonCode = request.ReasonCode },
                keys.With("exposureId", exposure.ExposureId.Value.ToString())));
        }

        return new ClaimReopenResponse { Claim = (await reader.GetSummaryAsync(legalEntity, claim.ClaimId, cancellationToken).ConfigureAwait(false))! };
    }
}

/// <summary>Audit facts of <c>clm.Claim.reopen</c>: the reason code is part of the record.</summary>
internal sealed class ReopenClaimAuditor : ICommandAuditor<ReopenClaim, ClaimReopenResponse>
{
    public CommandAuditFacts Describe(ReopenClaim command, Result<ClaimReopenResponse>? result)
    {
        var claimId = command.Request.ClaimId.Value.ToString();
        if (result is not { IsSuccess: true } success)
        {
            return new CommandAuditFacts
            {
                ObjectRef = ObjectRef.For(ModuleCode.CLM, "Claim", command.Request.ClaimId),
                BusinessKeys = BusinessKeys.Empty.With("claimId", claimId),
            };
        }

        var claim = success.Value.Claim;
        return new CommandAuditFacts
        {
            ObjectRef = ObjectRef.For(ModuleCode.CLM, "Claim", claim.ClaimId),
            ObjectNumber = claim.ClaimNumber.Value,
            BusinessKeys = BusinessKeys.Empty.With("claimId", claimId),
            Changes = AuditDiff.Compute(new { status = "CLOSED" }, new { status = "OPEN", subStatus = "IN_PROGRESS", reasonCode = command.Request.ReasonCode }),
        };
    }
}
