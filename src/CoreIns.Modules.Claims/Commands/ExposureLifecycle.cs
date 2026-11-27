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
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Claims.Commands;

/// <summary><c>clm.Exposure.update</c> (BO-CLM-01).</summary>
internal sealed record UpdateExposure(ExposureId ExposureId, ExposureUpdateRequest Request) : ICommand<ExposureUpdateResponse>;

/// <summary><c>clm.Exposure.close</c> (BO-CLM-01, D-CON-04).</summary>
internal sealed record CloseExposure(ExposureCloseRequest Request) : ICommand<ExposureCloseResponse>;

/// <summary><c>clm.Exposure.reopen</c> (BO-CLM-01).</summary>
internal sealed record ReopenExposure(ExposureReopenRequest Request) : ICommand<ExposureReopenResponse>;

/// <summary>Shape rules.</summary>
internal sealed class UpdateExposureValidator : AbstractValidator<UpdateExposure>
{
    public UpdateExposureValidator() => RuleFor(c => c.Request.ExpectedRecordVersion).GreaterThanOrEqualTo(1);
}

/// <summary>Shape rules.</summary>
internal sealed class CloseExposureValidator : AbstractValidator<CloseExposure>
{
    public CloseExposureValidator()
    {
        RuleFor(c => c.Request.ExpectedRecordVersion).GreaterThanOrEqualTo(1);
        RuleFor(c => c.Request.Outcome).IsInEnum();
    }
}

/// <summary>Shape rules.</summary>
internal sealed class ReopenExposureValidator : AbstractValidator<ReopenExposure>
{
    public ReopenExposureValidator()
    {
        RuleFor(c => c.Request.ExpectedRecordVersion).GreaterThanOrEqualTo(1);
        RuleFor(c => c.Request.ReasonCode).NotEmpty().Matches("^[A-Z][A-Z0-9_]{0,63}$").WithErrorCode("CODE");
    }
}

/// <summary>
/// The statutory-clock part of the exposure close rule (D-CON-04): clocks must be Met, Cancelled or Breached-and-acknowledged.
/// Exposures without clocks (<c>NOT_TRACKED</c>) pass. BO-CLM-05 stores the clock state on the exposure and keeps this the one place that reads it.
/// </summary>
internal static class ExposureClockRule
{
    private static readonly string[] Settled = ["NOT_TRACKED", "MET", "CANCELLED", "BREACHED_ACKNOWLEDGED"];

    /// <summary>True when a running (or unacknowledged breached) clock blocks the close.</summary>
    public static bool Blocks(string statutoryClocks) => !Settled.Contains(statutoryClocks, StringComparer.Ordinal);
}

/// <summary>Loads and guards the exposure and its claim for the three lifecycle commands.</summary>
internal sealed class ExposureLifecycleSupport(ClaimsDbContext db, RequestContext context, ClaimProtection protection, ClaimReader reader)
{
    public LegalEntityId LegalEntity => protection.Current(context);

    /// <summary>The claim (row-locked) and its exposure, after the stale check and the claim-open check; or the error.</summary>
    public async Task<Result<(ClaimRow Claim, ExposureRow Exposure)>> LoadAsync(ExposureId exposureId, int expectedVersion, CancellationToken cancellationToken)
    {
        var legalEntity = LegalEntity;
        var claimId = await db.Exposures.AsNoTracking().Where(e => e.ExposureId == exposureId && e.LegalEntityId == legalEntity)
            .Select(e => (ClaimId?)e.ClaimId).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (claimId is null)
        {
            return ClaimSupport.NotFound("exposure");
        }

        var claim = await ClaimSupport.LoadAsync(db, legalEntity, claimId.Value, cancellationToken).ConfigureAwait(false);
        if (claim is null)
        {
            return ClaimSupport.NotFound("claim");
        }

        if (claim.RecordVersion != expectedVersion)
        {
            return ClaimSupport.Stale(claim.RecordVersion);
        }

        // Exposure commands are handler actions: they need an open claim (REQ-CLM-071).
        var next = ClaimStateModel.Fire(ClaimStates.FromColumns(claim.Status, claim.SubStatus), ClaimTrigger.HandlerAction);
        if (next.IsFailure)
        {
            return next.Error!;
        }

        (claim.Status, claim.SubStatus) = ClaimStates.ToColumns(next.Value);
        var exposure = await db.Exposures.SingleAsync(e => e.ExposureId == exposureId, cancellationToken).ConfigureAwait(false);
        return (claim, exposure);
    }

    /// <summary>Stamps the claim and saves; a lost race is CLM-ERR-STALE, a reopen into an occupied slot CLM-ERR-EXPOSURE-DUPLICATE.</summary>
    public async Task<DomainError?> SaveAsync(ClaimRow claim, string actor, Instant now, CancellationToken cancellationToken)
    {
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
        catch (DbUpdateException ex) when (ClaimSupport.IsUniqueViolation(ex, "ux_exposure_open_coverage_claimant_incident"))
        {
            return DomainError.Of(ModuleCode.CLM, "EXPOSURE-DUPLICATE", "An open exposure exists for this coverage, claimant and incident.");
        }

        return null;
    }

    public async Task<(ExposureView Exposure, ClaimSummary Claim)> ViewAsync(ClaimRow claim, ExposureId exposureId, CancellationToken cancellationToken)
    {
        var exposures = await reader.ExposuresAsync(claim.ClaimId, cancellationToken).ConfigureAwait(false);
        return (exposures.Single(e => e.ExposureId == exposureId),
            (await reader.GetSummaryAsync(LegalEntity, claim.ClaimId, cancellationToken).ConfigureAwait(false))!);
    }

    public static BusinessKeys Keys(ClaimRow claim, ExposureRow exposure) =>
        BusinessKeys.Empty.With("claimId", claim.ClaimId.Value.ToString()).With("exposureId", exposure.ExposureId.Value.ToString());
}

/// <summary>
/// <c>clm.Exposure.update</c>: moves the exposure to another incident of the same claim (or none). A closed exposure is
/// not editable (CLM-ERR-ILLEGAL-TRANSITION); a move onto an occupied (coverage, claimant, incident) slot is CLM-ERR-EXPOSURE-DUPLICATE.
/// </summary>
internal sealed class UpdateExposureHandler(ClaimsDbContext db, RequestContext context, IClock clock, ExposureLifecycleSupport support)
    : ICommandHandler<UpdateExposure, ExposureUpdateResponse>
{
    public async Task<Result<ExposureUpdateResponse>> HandleAsync(UpdateExposure command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var loaded = await support.LoadAsync(command.ExposureId, request.ExpectedRecordVersion, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var (claim, exposure) = loaded.Value;
        if (exposure.Status == ClaimStates.Closed)
        {
            return DomainError.Of(ModuleCode.CLM, "ILLEGAL-TRANSITION", "A closed exposure cannot be changed; reopen it first.");
        }

        if (request.IncidentId is { } incidentId
            && !await db.Incidents.AnyAsync(i => i.IncidentId == incidentId && i.ClaimId == claim.ClaimId, cancellationToken).ConfigureAwait(false))
        {
            return FnolAssessment.Invalid("incidentId", "INCIDENT", "The incident does not belong to the claim.");
        }

        var now = clock.Now;
        if (exposure.IncidentId != request.IncidentId)
        {
            exposure.IncidentId = request.IncidentId;
            (exposure.Status, exposure.SubStatus) = ExposureStates.ToColumns(ExposureState.InProgress);
            exposure.UpdatedAt = now;
            exposure.RecordVersion++;
        }

        var saved = await support.SaveAsync(claim, context.Actor.ToString(), now, cancellationToken).ConfigureAwait(false);
        if (saved is not null)
        {
            return saved;
        }

        var (view, summary) = await support.ViewAsync(claim, exposure.ExposureId, cancellationToken).ConfigureAwait(false);
        return new ExposureUpdateResponse { Exposure = view, Claim = summary };
    }
}

/// <summary>
/// <c>clm.Exposure.close</c> (D-CON-04, REQ-CLM-072): refused with CLM-ERR-CLOSE-GUARD (<c>errors[]</c> items { code, exposureIds[] })
/// while the exposure has an open reserve, a pending payment, an open recovery or a statutory clock that is not Met, Cancelled
/// or Breached-and-acknowledged. A closed exposure refuses a second close (CLM-ERR-ILLEGAL-TRANSITION); a retry with the same
/// idempotency key replays the first response. Publishes <c>ExposureClosed</c>.
/// </summary>
internal sealed class CloseExposureHandler(
    ClaimsDbContext db, RequestContext context, IClock clock, IEventPublisher events, IClaimFinancialGuard financials, ExposureLifecycleSupport support)
    : ICommandHandler<CloseExposure, ExposureCloseResponse>
{
    public async Task<Result<ExposureCloseResponse>> HandleAsync(CloseExposure command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var loaded = await support.LoadAsync(request.ExposureId, request.ExpectedRecordVersion, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var (claim, exposure) = loaded.Value;
        var closed = ExposureStateModel.Machine.Fire(ExposureStates.FromColumns(exposure.Status, exposure.SubStatus), ExposureTrigger.Close);
        if (closed.IsFailure)
        {
            return DomainError.Of(ModuleCode.CLM, "ILLEGAL-TRANSITION", "The exposure is already closed.");
        }

        var positions = await financials.PositionsAsync(claim.ClaimId, [exposure.ExposureId], cancellationToken).ConfigureAwait(false);
        var reasons = CloseGuard.Blocking(positions).SelectMany(b => b.Reasons).ToList();
        if (await db.Recoveries.AsNoTracking().AnyAsync(
                r => r.ExposureId == exposure.ExposureId && r.Status != "CLOSED" && r.Status != "WRITTEN_OFF", cancellationToken).ConfigureAwait(false))
        {
            reasons.Add("OPEN_RECOVERY");
        }

        if (ExposureClockRule.Blocks(exposure.StatutoryClocks))
        {
            reasons.Add("CLOCK_RUNNING");
        }

        if (reasons.Count > 0)
        {
            var guards = reasons.Select(r => new CloseGuardError { Code = r, ExposureIds = [exposure.ExposureId] }).ToList();
            return new DomainError(ErrorCode.For(ModuleCode.CLM, "CLOSE-GUARD"), "The exposure has an open reserve, a pending payment, an open recovery or a running statutory clock.")
            {
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["closeGuardErrors"] = JsonSerializer.Serialize(guards, SharedKernelJson.Options) },
                FieldErrors = [.. reasons.Select(r => new FieldError($"exposures[{exposure.ExposureId.Value:D}]", r, r))],
            };
        }

        var now = clock.Now;
        var outcome = Codes.Of(Codes.Map<ExposureCloseRequest.OutcomeValue, ClaimOutcomeCode>(request.Outcome));
        (exposure.Status, exposure.SubStatus) = ExposureStates.ToColumns(closed.Value);
        exposure.Outcome = outcome;
        exposure.ClosedAt = now;
        exposure.UpdatedAt = now;
        exposure.RecordVersion++;
        var saved = await support.SaveAsync(claim, context.Actor.ToString(), now, cancellationToken).ConfigureAwait(false);
        if (saved is not null)
        {
            return saved;
        }

        events.Publish(new OutgoingEvent(
            EventDescriptor.From(ExposureClosedV1.Descriptor), ClaimEvents.AggregateType, claim.ClaimId.Value.ToString(),
            new ExposureClosedV1 { ExposureId = exposure.ExposureId, ExposureNumber = exposure.ExposureNumber, Outcome = outcome },
            ExposureLifecycleSupport.Keys(claim, exposure)));

        var (view, summary) = await support.ViewAsync(claim, exposure.ExposureId, cancellationToken).ConfigureAwait(false);
        return new ExposureCloseResponse { Exposure = view, Claim = summary };
    }
}

/// <summary>
/// <c>clm.Exposure.reopen</c>: Closed to Open(InProgress) with a coded reason on an open claim; the exposure's outcome and
/// close time are cleared. Publishes <c>ExposureReopened</c>.
/// </summary>
internal sealed class ReopenExposureHandler(RequestContext context, IClock clock, IEventPublisher events, ExposureLifecycleSupport support)
    : ICommandHandler<ReopenExposure, ExposureReopenResponse>
{
    public async Task<Result<ExposureReopenResponse>> HandleAsync(ReopenExposure command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var loaded = await support.LoadAsync(request.ExposureId, request.ExpectedRecordVersion, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var (claim, exposure) = loaded.Value;
        var reopened = ExposureStateModel.Machine.Fire(ExposureStates.FromColumns(exposure.Status, exposure.SubStatus), ExposureTrigger.Reopen);
        if (reopened.IsFailure)
        {
            return DomainError.Of(ModuleCode.CLM, "ILLEGAL-TRANSITION", "Only a closed exposure can be reopened.");
        }

        var now = clock.Now;
        (exposure.Status, exposure.SubStatus) = ExposureStates.ToColumns(reopened.Value);
        exposure.Outcome = null;
        exposure.ClosedAt = null;
        exposure.UpdatedAt = now;
        exposure.RecordVersion++;
        var saved = await support.SaveAsync(claim, context.Actor.ToString(), now, cancellationToken).ConfigureAwait(false);
        if (saved is not null)
        {
            return saved;
        }

        events.Publish(new OutgoingEvent(
            EventDescriptor.From(ExposureReopenedV1.Descriptor), ClaimEvents.AggregateType, claim.ClaimId.Value.ToString(),
            new ExposureReopenedV1 { ExposureId = exposure.ExposureId, ExposureNumber = exposure.ExposureNumber, ReasonCode = request.ReasonCode },
            ExposureLifecycleSupport.Keys(claim, exposure)));

        var (view, summary) = await support.ViewAsync(claim, exposure.ExposureId, cancellationToken).ConfigureAwait(false);
        return new ExposureReopenResponse { Exposure = view, Claim = summary };
    }
}

/// <summary>Audit facts shared by the exposure lifecycle commands.</summary>
internal static class ExposureAudit
{
    public static CommandAuditFacts Facts(ExposureId exposureId, ExposureView? view, ClaimSummary? claim, object? after) => new()
    {
        ObjectRef = ObjectRef.For(ModuleCode.CLM, "Exposure", exposureId),
        ObjectNumber = view?.ExposureNumber.Value,
        BusinessKeys = claim is null
            ? BusinessKeys.Empty.With("exposureId", exposureId.Value.ToString())
            : BusinessKeys.Empty.With("claimId", claim.ClaimId.Value.ToString()).With("exposureId", exposureId.Value.ToString()),
        Changes = AuditDiff.Compute(null, after ?? new { }),
    };
}

/// <summary>Audit facts of <c>clm.Exposure.update</c>.</summary>
internal sealed class UpdateExposureAuditor : ICommandAuditor<UpdateExposure, ExposureUpdateResponse>
{
    public CommandAuditFacts Describe(UpdateExposure command, Result<ExposureUpdateResponse>? result) =>
        result is { IsSuccess: true } ok
            ? ExposureAudit.Facts(command.ExposureId, ok.Value.Exposure, ok.Value.Claim, new { incidentId = ok.Value.Exposure.IncidentId })
            : ExposureAudit.Facts(command.ExposureId, null, null, null);
}

/// <summary>Audit facts of <c>clm.Exposure.close</c>.</summary>
internal sealed class CloseExposureAuditor : ICommandAuditor<CloseExposure, ExposureCloseResponse>
{
    public CommandAuditFacts Describe(CloseExposure command, Result<ExposureCloseResponse>? result) =>
        result is { IsSuccess: true } ok
            ? ExposureAudit.Facts(command.Request.ExposureId, ok.Value.Exposure, ok.Value.Claim, new { status = "CLOSED", outcome = ok.Value.Exposure.Outcome })
            : ExposureAudit.Facts(command.Request.ExposureId, null, null, null);
}

/// <summary>Audit facts of <c>clm.Exposure.reopen</c>.</summary>
internal sealed class ReopenExposureAuditor : ICommandAuditor<ReopenExposure, ExposureReopenResponse>
{
    public CommandAuditFacts Describe(ReopenExposure command, Result<ExposureReopenResponse>? result) =>
        result is { IsSuccess: true } ok
            ? ExposureAudit.Facts(command.Request.ExposureId, ok.Value.Exposure, ok.Value.Claim, new { status = "OPEN", reasonCode = command.Request.ReasonCode })
            : ExposureAudit.Facts(command.Request.ExposureId, null, null, null);
}
