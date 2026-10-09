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
using CoreIns.SharedKernel.Results;
using CoreIns.SharedKernel.Json;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Claims.Commands;

/// <summary><c>clm.Claim.close</c> as a command of the platform pipeline.</summary>
internal sealed record CloseClaim(ClaimCloseRequest Request) : ICommand<ClaimCloseResponse>;

/// <summary>Shape rules.</summary>
internal sealed class CloseClaimValidator : AbstractValidator<CloseClaim>
{
    public CloseClaimValidator()
    {
        RuleFor(c => c.Request.ExpectedRecordVersion).GreaterThanOrEqualTo(1);
        RuleFor(c => c.Request.Outcome).IsInEnum();
        RuleFor(c => c.Request.ReasonCode).Matches("^[A-Z][A-Z0-9_]{0,63}$").When(c => c.Request.ReasonCode is not null).WithErrorCode("CODE");
    }
}

/// <summary>
/// Closes a claim with an outcome (REQ-CLM-071, -072, -073): Open(any) → Closed; Draft or Closed → CLM-ERR-ILLEGAL-TRANSITION;
/// a claim changed since the caller read it → CLM-ERR-STALE (checked first, so a racing close loses with STALE, never
/// with ILLEGAL-TRANSITION). Its open exposures close with it, each under the close guard (REQ-CLM-072) read through
/// <see cref="IClaimFinancialGuard"/> ("open reserve is zero and no payment pending"; SL2-CLM-MONEY fills the seam):
/// any blocking exposure refuses the close with CLM-ERR-CLOSE-GUARD. Publishes <c>ClaimClosed</c> in the same transaction.
/// </summary>
internal sealed class CloseClaimHandler(
    ClaimsDbContext db,
    RequestContext context,
    IClock clock,
    IEventPublisher events,
    ClaimProtection protection,
    IClaimFinancialGuard financials,
    ClaimReader reader) : ICommandHandler<CloseClaim, ClaimCloseResponse>
{
    public async Task<Result<ClaimCloseResponse>> HandleAsync(CloseClaim command, CancellationToken cancellationToken)
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

        var closed = ClaimStateModel.Fire(ClaimStates.FromColumns(claim.Status, claim.SubStatus), ClaimTrigger.Close);
        if (closed.IsFailure)
        {
            return closed.Error!;
        }

        // Close guard (REQ-CLM-072/073) over every open exposure.
        var open = await db.Exposures.Where(e => e.ClaimId == claim.ClaimId && e.Status == ClaimStates.Open).OrderBy(e => e.Sequence)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var positions = await financials.PositionsAsync(claim.ClaimId, [.. open.Select(e => e.ExposureId)], cancellationToken).ConfigureAwait(false);
        var blocking = CloseGuard.Blocking(positions);
        var recoveries = await db.Recoveries.AsNoTracking().Where(r => r.ClaimId == claim.ClaimId && r.Status != "CLOSED" && r.Status != "WRITTEN_OFF").ToListAsync(cancellationToken).ConfigureAwait(false);
        if (blocking.Count > 0 || recoveries.Count > 0)
        {
            var numbers = open.ToDictionary(e => e.ExposureId, e => e.ExposureNumber.Value);
            var guards = blocking.SelectMany(b => b.Reasons.Select(r => (Code: r, Exposure: (ExposureId?)b.Exposure)))
                .Concat(recoveries.Select(r => (Code: "OPEN_RECOVERY", Exposure: r.ExposureId)))
                .GroupBy(g => g.Code).Select(g => new CloseGuardError
                {
                    Code = g.Key, ExposureIds = [.. g.Where(x => x.Exposure is not null).Select(x => x.Exposure!.Value).Distinct()],
                }).ToList();
            var metadata = blocking.ToDictionary(b => numbers[b.Exposure], b => string.Join(",", b.Reasons), StringComparer.Ordinal);
            metadata["closeGuardErrors"] = JsonSerializer.Serialize(guards, SharedKernelJson.Options);
            return new DomainError(ErrorCode.For(ModuleCode.CLM, "CLOSE-GUARD"), "An exposure has an open reserve or a pending payment; release or settle it first.")
            {
                Metadata = metadata,
                FieldErrors = [.. blocking.SelectMany(b => b.Reasons.Select(r => new FieldError($"exposures[{b.Exposure.Value:D}]", r, r))),
                    .. recoveries.Select(r => new FieldError(r.ExposureId is { } e ? $"exposures[{e.Value:D}]" : "claim", "OPEN_RECOVERY", "OPEN_RECOVERY"))],
            };
        }

        var now = clock.Now;
        var outcome = Codes.Of(Codes.Map<ClaimCloseRequest.OutcomeValue, ClaimOutcomeCode>(request.Outcome));
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

        (claim.Status, claim.SubStatus) = ClaimStates.ToColumns(closed.Value);
        claim.Outcome = outcome;
        claim.CloseReasonCode = request.ReasonCode;
        claim.ClosedAt = now;
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
            EventDescriptor.From(ClaimClosedV1.Descriptor),
            ClaimEvents.AggregateType,
            claim.ClaimId.Value.ToString(),
            new ClaimClosedV1 { Outcome = outcome, Totals = [] },
            BusinessKeys.Empty.With("claimId", claim.ClaimId.Value.ToString())));

        return new ClaimCloseResponse { Claim = (await reader.GetSummaryAsync(legalEntity, claim.ClaimId, cancellationToken).ConfigureAwait(false))! };
    }
}

/// <summary>Audit facts of <c>clm.Claim.close</c>.</summary>
internal sealed class CloseClaimAuditor : ICommandAuditor<CloseClaim, ClaimCloseResponse>
{
    public CommandAuditFacts Describe(CloseClaim command, Result<ClaimCloseResponse>? result)
    {
        if (result is not { IsSuccess: true } success)
        {
            return new CommandAuditFacts
            {
                ObjectRef = ObjectRef.For(ModuleCode.CLM, "Claim", command.Request.ClaimId),
                BusinessKeys = BusinessKeys.Empty.With("claimId", command.Request.ClaimId.Value.ToString()),
            };
        }

        var claim = success.Value.Claim;
        return new CommandAuditFacts
        {
            ObjectRef = ObjectRef.For(ModuleCode.CLM, "Claim", claim.ClaimId),
            ObjectNumber = claim.ClaimNumber.Value,
            BusinessKeys = BusinessKeys.Empty.With("claimId", claim.ClaimId.Value.ToString()),
            Changes = AuditDiff.Compute(new { status = "OPEN" }, new { status = "CLOSED", outcome = claim.Outcome?.ToString(), reasonCode = command.Request.ReasonCode }),
        };
    }
}
