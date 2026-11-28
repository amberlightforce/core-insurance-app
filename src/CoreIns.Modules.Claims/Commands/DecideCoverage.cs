using CoreIns.Modules.Claims.Authority;
using CoreIns.Modules.Claims.Contracts.Api;
using CoreIns.Modules.Claims.Contracts.Events;
using CoreIns.Modules.Claims.Domain;
using CoreIns.Modules.Claims.Persistence;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Authority;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using AuthorityCheckRequest = CoreIns.Platform.Authority.AuthorityCheckRequest;

namespace CoreIns.Modules.Claims.Commands;

/// <summary><c>clm.Coverage.decide</c> (BO-CLM-03, REQ-CLM-002, -051, -058).</summary>
internal sealed record DecideCoverage(CoverageDecideRequest Request) : ICommand<CoverageDecideResponse>;

/// <summary>Shape rules.</summary>
internal sealed class DecideCoverageValidator : AbstractValidator<DecideCoverage>
{
    public DecideCoverageValidator()
    {
        RuleFor(c => c.Request.Outcome).IsInEnum();
        RuleFor(c => c.Request.ReasonCode).NotEmpty().Matches("^[A-Z][A-Z0-9_]{0,63}$").WithErrorCode("CODE");
        RuleFor(c => c.Request.Comment).MaximumLength(1024).When(c => c.Request.Comment is not null);
    }
}

/// <summary>
/// A human decides the cover of one exposure (REQ-CLM-051): COVERED, DENIED or RESERVATION_OF_RIGHTS, each with a reason code.
/// <list type="bullet">
/// <item>Refused while a re-verification is pending on the claim, or when POL has superseded the claim's snapshot since (the same
/// snapshot compare as <c>clm.Coverage.reverify</c>): CLM-ERR-SNAPSHOT-MISMATCH; POL down is CLM-ERR-DEPENDENCY-UNAVAILABLE.</item>
/// <item>Authority <c>CLM.COVERAGE_DECISION</c> is checked against the exposure's open reserve. Within the actor's limit and up to
/// 50 000 EUR the decision stands; above either, four eyes are required and the command is refused (CLM-ERR-AUTHORITY).
/// TODO(hardening): route the referral through a PLT approval request instead of refusing.</item>
/// <item>COVERED sets indication and decision COVERED; DENIED sets NOT_COVERED, after which <see cref="CoverageDenialGuard.IsDenied"/> stops every
/// new payment on the exposure (CLM-ERR-COVERAGE-DENIED, reserves stay possible); RESERVATION_OF_RIGHTS stores COVERED_WITH_RESERVATION
/// and moves the claim to UnderInvestigation. Each decision ends the exposure's pending state, and the claim's coverage-in-question
/// flag is recomputed from the open exposures that still wait for a decision.</item>
/// </list>
/// The comment is not stored; the audit record carries the reason code only (no free text, P2).
/// TODO(hardening): a decision history table (the current decision is the exposure's column; the audit trail and event keep the rest).
/// </summary>
internal sealed class DecideCoverageHandler(
    ClaimsDbContext db,
    RequestContext context,
    IClock clock,
    IEventPublisher events,
    ClaimProtection protection,
    ICoverageSource coverage,
    IClaimFinancialGuard financials,
    IAuthorityService authority) : ICommandHandler<DecideCoverage, CoverageDecideResponse>
{
    private const decimal FourEyesAbove = 50000m;

    public async Task<Result<CoverageDecideResponse>> HandleAsync(DecideCoverage command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = protection.Current(context);
        var claim = await ClaimSupport.LoadAsync(db, legalEntity, request.ClaimId, cancellationToken).ConfigureAwait(false);
        if (claim is null)
        {
            return ClaimSupport.NotFound("claim");
        }

        var exposure = await db.Exposures.FirstOrDefaultAsync(e => e.ClaimId == claim.ClaimId && e.ExposureId == request.ExposureId, cancellationToken).ConfigureAwait(false);
        if (exposure is null)
        {
            return ClaimSupport.NotFound("exposure");
        }

        if (claim.Status != ClaimStates.Open || exposure.Status != ClaimStates.Open)
        {
            return DomainError.Of(ModuleCode.CLM, "ILLEGAL-TRANSITION", "Cover can be decided only on an open exposure of an open claim.");
        }

        // The snapshot compare of reverify: nothing is decided against a snapshot that is about to be replaced.
        if (await db.Reverifications.AnyAsync(r => r.ClaimId == claim.ClaimId && r.Status == ReverificationRow.Open, cancellationToken).ConfigureAwait(false))
        {
            return DomainError.Of(ModuleCode.CLM, "SNAPSHOT-MISMATCH", "A re-verification is pending on the claim; keep or adopt the new snapshot first.");
        }

        var read = await coverage.ReadByRefAsync(claim.SnapshotRef, cancellationToken).ConfigureAwait(false);
        if (read.Outcome == SnapshotReadOutcome.Unavailable)
        {
            return DomainError.Of(ModuleCode.CLM, "DEPENDENCY-UNAVAILABLE", "The policy service did not answer. Try again shortly.");
        }

        if (read.Outcome == SnapshotReadOutcome.Found && read.Facts!.Supersession is { Superseded: true })
        {
            return DomainError.Of(ModuleCode.CLM, "SNAPSHOT-MISMATCH", "POL superseded the claim's snapshot; reload the claim and re-verify first.");
        }

        var position = (await financials.PositionsAsync(claim.ClaimId, [exposure.ExposureId], cancellationToken).ConfigureAwait(false)).Single();
        var amount = position.OpenReserve;
        var now = clock.Now;
        var check = await authority.CheckAsync(new AuthorityCheckRequest(
            context.Actor, context.Roles, ClaimsAuthorityTypes.CoverageDecision,
            new Dictionary<string, DimensionValue>(StringComparer.Ordinal) { [ClaimsAuthorityTypes.AmountDimension] = DimensionValue.Of(ClaimMoney.Eur(amount)) },
            ObjectRef.For(ModuleCode.CLM, "Exposure", exposure.ExposureId), now), cancellationToken).ConfigureAwait(false);
        context.AuthorityChecks.Add(check);
        if (check.Decision == AuthorityDecision.Allow && amount > FourEyesAbove)
        {
            check = check with { Decision = AuthorityDecision.Refer, ReasonCode = "FOUR_EYES_REQUIRED" };
        }

        if (check.Decision != AuthorityDecision.Allow)
        {
            return DomainError.Of(ModuleCode.CLM, "AUTHORITY", $"{check.Type} on {amount:0.00}: {check.ReasonCode}.");
        }

        var outcome = request.Outcome;
        var (indication, decision) = outcome switch
        {
            CoverageDecideRequest.OutcomeValue.Covered => (CoverageIndicationCode.Covered, CoverageDecisionCode.Covered),
            CoverageDecideRequest.OutcomeValue.Denied => (CoverageIndicationCode.NotCovered, CoverageDecisionCode.NotCovered),
            _ => (CoverageIndicationCode.Covered, CoverageDecisionCode.CoveredWithReservation),
        };

        if (outcome == CoverageDecideRequest.OutcomeValue.ReservationOfRights)
        {
            var moved = ClaimStateModel.Fire(ClaimStates.FromColumns(claim.Status, claim.SubStatus), ClaimTrigger.Investigate);
            if (moved.IsFailure)
            {
                return moved.Error!;
            }

            (claim.Status, claim.SubStatus) = ClaimStates.ToColumns(moved.Value);
        }

        exposure.CoverageIndication = Codes.Of(indication);
        exposure.CoverageDecision = Codes.Of(decision);
        exposure.UpdatedAt = now;
        exposure.RecordVersion++;
        var waiting = Codes.Of(CoverageIndicationCode.InQuestion);
        var pending = Codes.Of(CoverageDecisionCode.Pending);
        claim.CoverageInQuestion = await db.Exposures.AnyAsync(
            e => e.ClaimId == claim.ClaimId && e.Status == ClaimStates.Open && e.ExposureId != exposure.ExposureId
                && e.CoverageIndication == waiting && e.CoverageDecision == pending, cancellationToken).ConfigureAwait(false);
        claim.UpdatedAt = now;
        claim.RecordVersion++;
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ClaimSupport.Stale();
        }

        events.Publish(new OutgoingEvent(
            EventDescriptor.From(CoverageDecisionRecordedV1.Descriptor), ClaimEvents.AggregateType, claim.ClaimId.Value.ToString(),
            new CoverageDecisionRecordedV1
            {
                ExposureId = exposure.ExposureId,
                Decision = Codes.Of(outcome),
                ReasonCodes = [request.ReasonCode],
                DecisionMaker = context.Actor.ToString(),
            },
            BusinessKeys.Empty.With("claimId", claim.ClaimId.Value.ToString()).With("exposureId", exposure.ExposureId.Value.ToString())));

        return new CoverageDecideResponse
        {
            ClaimId = claim.ClaimId,
            ExposureId = exposure.ExposureId,
            Outcome = Codes.Map<CoverageDecideRequest.OutcomeValue, CoverageDecideResponse.OutcomeValue>(outcome),
            CoverageIndication = Codes.Map<CoverageIndicationCode, CoverageDecideResponse.CoverageIndicationValue>(indication),
            CoverageDecision = Codes.Map<CoverageDecisionCode, CoverageDecideResponse.CoverageDecisionValue>(decision),
            ClaimStatus = claim.Status,
            ClaimSubStatus = claim.SubStatus,
            CoverageInQuestion = claim.CoverageInQuestion,
            DecidedAt = now,
        };
    }
}

/// <summary>Audit facts of <c>clm.Coverage.decide</c>: outcome and reason code; never the comment (P2).</summary>
internal sealed class DecideCoverageAuditor : ICommandAuditor<DecideCoverage, CoverageDecideResponse>
{
    public CommandAuditFacts Describe(DecideCoverage command, Result<CoverageDecideResponse>? result)
    {
        var facts = new CommandAuditFacts
        {
            ObjectRef = ObjectRef.For(ModuleCode.CLM, "Claim", command.Request.ClaimId),
            BusinessKeys = BusinessKeys.Empty.With("claimId", command.Request.ClaimId.Value.ToString()).With("exposureId", command.Request.ExposureId.Value.ToString()),
        };
        return result is not { IsSuccess: true } success
            ? facts
            : facts with
            {
                Changes = AuditDiff.Compute(
                    new { coverageDecision = "PENDING" },
                    new { coverageDecision = Codes.Of(success.Value.CoverageDecision), outcome = Codes.Of(command.Request.Outcome), reasonCode = command.Request.ReasonCode }),
            };
    }
}
