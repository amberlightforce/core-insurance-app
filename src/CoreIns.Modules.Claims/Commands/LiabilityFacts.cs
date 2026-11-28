using CoreIns.Modules.Claims.Contracts.Api;
using CoreIns.Modules.Claims.Contracts.Events;
using CoreIns.Modules.Claims.Domain;
using CoreIns.Modules.Claims.Persistence;
using CoreIns.Modules.Claims.Queries;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts.Events;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Claims.Commands;

/// <summary>Liability facts of a motor claim (D-SL4-17): shape rules and the claim-row mapping, shared by FNOL and update.</summary>
internal static class LiabilityMapping
{
    public static bool Valid(Contracts.Api.LiabilityFacts facts) =>
        (facts.InsuredFaultPct is null || (facts.InsuredFaultPct is >= 0m and <= 100m && decimal.Round(facts.InsuredFaultPct.Value, 2, MidpointRounding.ToZero) == facts.InsuredFaultPct))
        && (facts.VehicleCount is null or >= 1)
        && (facts.FaultSource is null || System.Text.RegularExpressions.Regex.IsMatch(facts.FaultSource, "^[A-Z][A-Z0-9_]{0,63}$"));

    /// <summary>Members present replace the stored facts; absent members are unchanged.</summary>
    public static void Apply(ClaimRow claim, Contracts.Api.LiabilityFacts facts)
    {
        if (facts.InsuredFaultPct is { } pct)
        {
            claim.FaultInsuredPct = pct;
        }

        if (facts.FaultSource is { } source)
        {
            claim.FaultSource = source;
        }

        if (facts.CounterpartyInsurerPartyId is { } insurer)
        {
            claim.CounterpartyInsurerPartyId = new PartyId(insurer);
        }

        if (facts.JointReportFlag is { } joint)
        {
            claim.JointAccidentReport = joint;
        }

        if (facts.VehicleCount is { } vehicles)
        {
            claim.VehicleCount = vehicles;
        }

        if (facts.AccidentInGreece is { } greece)
        {
            claim.AccidentInGreece = greece;
        }
    }

    public static Contracts.Api.LiabilityFacts? Of(ClaimRow claim) =>
        claim.FaultInsuredPct is null && claim.FaultSource is null && claim.CounterpartyInsurerPartyId is null && claim.JointAccidentReport is null
            && claim.VehicleCount is null && claim.AccidentInGreece is null
            ? null
            : new Contracts.Api.LiabilityFacts
            {
                InsuredFaultPct = claim.FaultInsuredPct, FaultSource = claim.FaultSource, CounterpartyInsurerPartyId = claim.CounterpartyInsurerPartyId?.Value,
                JointReportFlag = claim.JointAccidentReport, VehicleCount = claim.VehicleCount, AccidentInGreece = claim.AccidentInGreece,
            };
}

/// <summary><c>clm.Claim.update</c> as a command of the platform pipeline (slice 4 types the liability facts only).</summary>
internal sealed record UpdateClaim(ClaimId ClaimId, ClaimUpdateRequest Request) : ICommand<ClaimUpdateResponse>;

internal sealed class UpdateClaimValidator : AbstractValidator<UpdateClaim>
{
    public UpdateClaimValidator()
    {
        RuleFor(c => c.Request.ExpectedRecordVersion).NotNull().GreaterThanOrEqualTo(1);
        RuleFor(c => c.Request.Changes).NotNull();
        RuleFor(c => c.Request.Changes!).Must(ch => ch.LiabilityFacts is not null || ch.Milestone is not null || ch.CatEventCode is not null || ch.UntagCatEvent == true)
            .WithErrorCode("CHANGES").WithMessage("Give at least one change: liabilityFacts, milestone, catEventCode or untagCatEvent.")
            .When(c => c.Request.Changes is not null);
        RuleFor(c => c.Request.Changes!.LiabilityFacts!).Must(LiabilityMapping.Valid).WithErrorCode("LIABILITY")
            .WithMessage("Fault is 0 to 100 with at most two decimals, the vehicle count at least 1 and the fault source a code.")
            .When(c => c.Request.Changes?.LiabilityFacts is not null);
        RuleFor(c => c.Request.Changes!.Milestone!).Matches(RecoverySupportCodes.CodePattern).WithErrorCode("CODE").When(c => c.Request.Changes?.Milestone is not null);
        RuleFor(c => c.Request.Changes!.CatEventCode!).Matches(RecoverySupportCodes.CodePattern).WithErrorCode("CODE").When(c => c.Request.Changes?.CatEventCode is not null);
        RuleFor(c => c.Request.Changes!).Must(ch => !(ch.CatEventCode is not null && ch.UntagCatEvent == true))
            .WithErrorCode("CAT-EVENT").WithMessage("Tag or untag a CAT event, not both.").When(c => c.Request.Changes is not null);
        RuleFor(c => c.Request.Reason).Matches(RecoverySupportCodes.CodePattern).When(c => c.Request.Reason is not null).WithErrorCode("CODE");
    }
}

internal static class RecoverySupportCodes
{
    public const string CodePattern = "^[A-Z][A-Z0-9_]{0,63}$";
}

/// <summary>
/// Sets the liability facts, tracking milestone and CAT event tag of an open claim (REQ-CLM-076 subset, REQ-CLM-144, BO-CLM-08): members present replace the stored ones, the claim
/// record version moves, the change is audited and <c>ClaimUpdated</c> says that the LIABILITY_FACTS group changed. Subrogation is
/// proposed to the handler by the UI when the insured fault is below 100 (a case is never opened implicitly).
/// </summary>
internal sealed class UpdateClaimHandler(
    ClaimsDbContext db, RequestContext context, IClock clock, IEventPublisher events, ClaimProtection protection, ClaimReader reader)
    : ICommandHandler<UpdateClaim, ClaimUpdateResponse>
{
    public async Task<Result<ClaimUpdateResponse>> HandleAsync(UpdateClaim command, CancellationToken cancellationToken)
    {
        var legalEntity = protection.Current(context);
        var claim = await ClaimSupport.LoadAsync(db, legalEntity, command.ClaimId, cancellationToken).ConfigureAwait(false);
        if (claim is null)
        {
            return ClaimSupport.NotFound("claim");
        }

        if (command.Request.ExpectedRecordVersion != claim.RecordVersion)
        {
            return ClaimSupport.Stale(claim.RecordVersion);
        }

        if (claim.Status != ClaimStates.Open)
        {
            return DomainError.Of(ModuleCode.CLM, "ILLEGAL-TRANSITION", "Claim changes are accepted on an open claim only.");
        }

        var changes = command.Request.Changes!;
        var now = clock.Now;
        var groups = new List<string>();
        OutgoingEvent? assigned = null;
        if (changes.LiabilityFacts is { } facts)
        {
            LiabilityMapping.Apply(claim, facts);
            groups.Add("LIABILITY_FACTS");
        }

        if (changes.Milestone is { } milestone && milestone != claim.Milestone)
        {
            claim.Milestone = milestone;
            claim.MilestoneAt = now;
            groups.Add("TRACKING");
        }

        if (changes.CatEventCode is { } catCode || changes.UntagCatEvent == true)
        {
            var previous = claim.CatEventCode;
            if (changes.CatEventCode is { } tag)
            {
                var rejected = await ValidateTagAsync(legalEntity, claim, tag, cancellationToken).ConfigureAwait(false);
                if (rejected is not null)
                {
                    return rejected;
                }
            }
            else if (previous is null)
            {
                return CatEventSupport.Error("The claim is not tagged to a CAT event.");
            }

            var next = changes.CatEventCode;
            if (next != previous)
            {
                claim.CatEventCode = next;
                groups.Add("CAT_EVENT");
                assigned = new OutgoingEvent(
                    EventDescriptor.From(CatEventAssignedV1.Descriptor), ClaimEvents.AggregateType, claim.ClaimId.Value.ToString(),
                    new CatEventAssignedV1 { CatCode = next, PreviousCatCode = previous, AssignmentMethod = CatEventAssignedV1.AssignmentMethodValue.Manual },
                    BusinessKeys.Empty.With("claimId", claim.ClaimId.Value.ToString()));
            }
        }

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
            EventDescriptor.From(ClaimUpdatedV1.Descriptor), ClaimEvents.AggregateType, claim.ClaimId.Value.ToString(),
            new ClaimUpdatedV1 { ChangedAttributeGroups = groups, NewMilestone = groups.Contains("TRACKING") ? claim.Milestone : null, SubStatus = claim.SubStatus },
            BusinessKeys.Empty.With("claimId", claim.ClaimId.Value.ToString())));
        if (assigned is not null)
        {
            events.Publish(assigned);
        }

        return new ClaimUpdateResponse
        {
            Claim = (await reader.GetSummaryAsync(legalEntity, claim.ClaimId, cancellationToken).ConfigureAwait(false))!,
            LiabilityFacts = LiabilityMapping.Of(claim),
        };
    }

    /// <summary>The CAT event must exist in the caller's entity, be open, and its window must contain the loss instant (REQ-CLM-210).</summary>
    private async Task<DomainError?> ValidateTagAsync(LegalEntityId legalEntity, ClaimRow claim, string catCode, CancellationToken cancellationToken)
    {
        var cat = await db.CatEvents.AsNoTracking().SingleOrDefaultAsync(e => e.LegalEntityId == legalEntity && e.CatCode == catCode, cancellationToken).ConfigureAwait(false);
        if (cat is null)
        {
            return ClaimSupport.NotFound("CAT event");
        }

        if (cat.Status != CatEventRow.Open)
        {
            return CatEventSupport.Error($"CAT event {catCode} is closed.");
        }

        if (claim.LossAt < cat.WindowFrom || (cat.WindowTo is { } to && claim.LossAt >= to))
        {
            return CatEventSupport.Error($"The loss date of the claim is outside the window of CAT event {catCode}.");
        }

        return null;
    }
}

internal sealed class UpdateClaimAuditor : ICommandAuditor<UpdateClaim, ClaimUpdateResponse>
{
    public CommandAuditFacts Describe(UpdateClaim command, Result<ClaimUpdateResponse>? result) => result is { IsSuccess: true } ok
        ? new CommandAuditFacts
        {
            ObjectRef = ObjectRef.For(ModuleCode.CLM, "Claim", command.ClaimId),
            BusinessKeys = BusinessKeys.Empty.With("claimId", command.ClaimId.Value.ToString()),
            Changes = AuditDiff.Compute(null, new { liabilityFacts = command.Request.Changes?.LiabilityFacts is null ? null : ok.Value.LiabilityFacts, milestone = command.Request.Changes?.Milestone, catEventCode = command.Request.Changes?.CatEventCode, untagCatEvent = command.Request.Changes?.UntagCatEvent, reason = command.Request.Reason }),
        }
        : new CommandAuditFacts
        {
            ObjectRef = ObjectRef.For(ModuleCode.CLM, "Claim", command.ClaimId),
            BusinessKeys = BusinessKeys.Empty.With("claimId", command.ClaimId.Value.ToString()),
        };
}
