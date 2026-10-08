using CoreIns.Modules.Claims.Contracts.Api;
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

/// <summary><c>clm.Exposure.create</c> as a command of the platform pipeline.</summary>
internal sealed record CreateExposure(ExposureCreateRequest Request) : ICommand<ExposureCreateResponse>;

/// <summary>Shape rules.</summary>
internal sealed class CreateExposureValidator : AbstractValidator<CreateExposure>
{
    public CreateExposureValidator()
    {
        RuleFor(c => c.Request.ExpectedRecordVersion).GreaterThanOrEqualTo(1);
        RuleFor(c => c.Request.Kind).IsInEnum();
        RuleFor(c => c.Request.CoverageCode).NotEmpty().Matches("^[A-Z][A-Z0-9_]{0,63}$").WithErrorCode("CODE");
        RuleFor(c => c.Request.DuplicateReason).Matches("^[A-Z][A-Z0-9_]{0,63}$").When(c => c.Request.DuplicateReason is not null).WithErrorCode("CODE");
    }
}

/// <summary>
/// Creates an exposure on an open claim (REQ-CLM-062, -063, -071): one coverage × one claimant (default the insured; another
/// PTY party becomes a third-party claimant), numbered claim number + sequence (REQ-CLM-043); its coverage indication from
/// the snapshot copy taken at FNOL (REQ-CLM-048). A handler action: a New claim moves to InProgress and the actor becomes
/// the handler when none is set. A Draft or Closed claim → CLM-ERR-ILLEGAL-TRANSITION; a changed claim → CLM-ERR-STALE;
/// a second open exposure on the same coverage, claimant and incident without a reason → CLM-ERR-EXPOSURE-DUPLICATE.
/// Publishes <c>ExposureCreated</c> on the claim's event sequence.
/// </summary>
internal sealed class CreateExposureHandler(
    ClaimsDbContext db,
    RequestContext context,
    IClock clock,
    IEventPublisher events,
    ClaimProtection protection,
    ClaimReader reader,
    IOptions<ClaimsOptions> options) : ICommandHandler<CreateExposure, ExposureCreateResponse>
{
    private const string OpenUniqueIndex = "ux_exposure_open_coverage_claimant_incident";

    public async Task<Result<ExposureCreateResponse>> HandleAsync(CreateExposure command, CancellationToken cancellationToken)
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

        var next = ClaimStateModel.Fire(ClaimStates.FromColumns(claim.Status, claim.SubStatus), ClaimTrigger.HandlerAction);
        if (next.IsFailure)
        {
            return next.Error!;
        }

        if (request.IncidentId is { } incidentId
            && !await db.Incidents.AnyAsync(i => i.IncidentId == incidentId && i.ClaimId == claim.ClaimId, cancellationToken).ConfigureAwait(false))
        {
            return FnolAssessment.Invalid("incidentId", "INCIDENT", "The incident does not belong to the claim.");
        }

        var now = clock.Now;
        var actor = context.Actor.ToString();
        var partyId = request.ClaimantPartyId ?? claim.InsuredPartyId;
        var claimant = await db.Claimants.SingleOrDefaultAsync(c => c.ClaimId == claim.ClaimId && c.PartyId == partyId, cancellationToken).ConfigureAwait(false);
        if (claimant is null)
        {
            claimant = new ClaimantRow
            {
                ClaimantId = ClaimantId.New(), ClaimId = claim.ClaimId, LegalEntityId = legalEntity, Jurisdiction = claim.Jurisdiction, PartyId = partyId,
                ClaimantType = Codes.Of(partyId == claim.InsuredPartyId ? ClaimantType.Insured : ClaimantType.ThirdParty), CreatedAt = now, CreatedBy = actor,
            };
            db.Claimants.Add(claimant);
        }

        // REQ-CLM-063: one open exposure per (coverage, claimant, incident) unless the user records a reason.
        if (request.DuplicateReason is null
            && await db.Exposures.AnyAsync(
                e => e.ClaimId == claim.ClaimId && e.Status == ClaimStates.Open && e.CoverageCode == request.CoverageCode
                     && e.ClaimantId == claimant.ClaimantId && e.IncidentId == request.IncidentId, cancellationToken).ConfigureAwait(false))
        {
            return DomainError.Of(ModuleCode.CLM, "EXPOSURE-DUPLICATE", "An open exposure exists for this coverage, claimant and incident; give a reason to add another.");
        }

        var indication = !claim.PolicyInForceAtLoss ? CoverageIndicationCode.InQuestion
            : claim.SnapshotCoverageCodes.Contains(request.CoverageCode, StringComparer.Ordinal) ? CoverageIndicationCode.Covered
            : CoverageIndicationCode.NotCovered;
        claim.LastExposureSequence++;
        var exposure = SubmitFnolHandler.NewExposure(
            claim, claim.LastExposureSequence, Codes.Of(request.Kind), request.CoverageCode, claimant.ClaimantId, request.IncidentId, request.DuplicateReason,
            indication, now, actor);
        db.Exposures.Add(exposure);

        (claim.Status, claim.SubStatus) = ClaimStates.ToColumns(next.Value);
        claim.Handler ??= actor;
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
        catch (DbUpdateException ex) when (ClaimSupport.IsUniqueViolation(ex, OpenUniqueIndex))
        {
            return DomainError.Of(ModuleCode.CLM, "EXPOSURE-DUPLICATE", "Another open exposure for this coverage, claimant and incident was created meanwhile.");
        }

        events.Publish(new OutgoingEvent(
            EventDescriptor.From(Contracts.Events.ExposureCreatedV1.Descriptor),
            ClaimEvents.AggregateType,
            claim.ClaimId.Value.ToString(),
            SubmitFnolHandler.ExposureCreated(claim, exposure, claimant.PartyId, options.Value),
            BusinessKeys.Empty.With("claimId", claim.ClaimId.Value.ToString()).With("exposureId", exposure.ExposureId.Value.ToString())));

        var exposures = await reader.ExposuresAsync(claim.ClaimId, cancellationToken).ConfigureAwait(false);
        return new ExposureCreateResponse
        {
            Exposure = exposures.Single(e => e.ExposureId == exposure.ExposureId),
            Claim = (await reader.GetSummaryAsync(legalEntity, claim.ClaimId, cancellationToken).ConfigureAwait(false))!,
        };
    }
}

/// <summary>Audit facts of <c>clm.Exposure.create</c>.</summary>
internal sealed class CreateExposureAuditor : ICommandAuditor<CreateExposure, ExposureCreateResponse>
{
    public CommandAuditFacts Describe(CreateExposure command, Result<ExposureCreateResponse>? result)
    {
        if (result is not { IsSuccess: true } success)
        {
            return new CommandAuditFacts
            {
                ObjectRef = ObjectRef.For(ModuleCode.CLM, "Claim", command.Request.ClaimId),
                BusinessKeys = BusinessKeys.Empty.With("claimId", command.Request.ClaimId.Value.ToString()),
            };
        }

        var exposure = success.Value.Exposure;
        return new CommandAuditFacts
        {
            ObjectRef = ObjectRef.For(ModuleCode.CLM, "Exposure", exposure.ExposureId),
            ObjectNumber = exposure.ExposureNumber.Value,
            BusinessKeys = BusinessKeys.Empty.With("claimId", success.Value.Claim.ClaimId.Value.ToString()).With("exposureId", exposure.ExposureId.Value.ToString()),
            Changes = AuditDiff.Compute(null, new
            {
                kind = exposure.Kind.ToString(),
                coverageCode = exposure.CoverageCode,
                coverageIndication = exposure.CoverageIndication.ToString(),
                duplicateReason = exposure.DuplicateReason,
                claimSubStatus = success.Value.Claim.SubStatus?.ToString(),
            }),
        };
    }
}
