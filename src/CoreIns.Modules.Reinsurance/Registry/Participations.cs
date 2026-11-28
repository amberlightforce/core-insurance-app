using CoreIns.Modules.Reinsurance.Contracts.Api;
using CoreIns.Modules.Reinsurance.Persistence;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Rules;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;

namespace CoreIns.Modules.Reinsurance.Registry;

/// <summary>
/// The shared part of <c>ri.Participation.add</c> and <c>.replace</c> (BO-RI-05): the new panel is built from the version in
/// force, checked against the security rules in force on the effective date, and then filed as an endorsement of the
/// contract (<see cref="EndorseContractHandler"/>: a further version valid from the effective date, PENDING until the
/// checker decides). The earlier version keeps its panel, so a replacement is effective-dated and the history stays.
/// </summary>
internal sealed class PanelEndorsement(
    ReinsuranceDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, SecurityReader security, EndorseContractHandler endorse)
{
    /// <summary>The lines of the version in force (the latest approved one), lead first.</summary>
    public async Task<(IReadOnlyList<ContentLine>? Lines, decimal PlacedPct, DomainError? Error)> CurrentPanelAsync(RiContractId contractId, CancellationToken cancellationToken)
    {
        var entity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var contract = await ContractSupport.LockAsync(db, entity, contractId, cancellationToken).ConfigureAwait(false);
        if (contract is null)
        {
            return (null, 0m, RiErrors.NotFound());
        }

        var latest = await ContractVersions.LatestAsync(db, contractId, cancellationToken).ConfigureAwait(false);
        var content = await ContractSupport.ReadContentAsync(db, contract, latest, cancellationToken).ConfigureAwait(false);
        return (content.Lines, content.PlacedPct, null);
    }

    /// <summary>Checks the lines that are new or grew against the rule set in force on the effective date, and files the endorsement.</summary>
    public async Task<Result<(ContractEndorseResponse Endorsed, IReadOnlyList<ParticipationCheck> Checks)>> FileAsync(
        RiContractId contractId, int expectedRecordVersion, BusinessDate effectiveFrom, string reason, IReadOnlyList<ContentLine> before, IReadOnlyList<ContentLine> after,
        decimal placedPct, CancellationToken cancellationToken)
    {
        var rules = await security.InForceAsync(effectiveFrom, cancellationToken).ConfigureAwait(false);
        var checks = new List<ParticipationCheck>();
        if (rules is not null)
        {
            var errors = new List<FieldError>();
            foreach (var line in after)
            {
                var old = before.FirstOrDefault(b => b.ReinsurerPartyId == line.ReinsurerPartyId);
                if (old is not null && line.SignedLinePct <= old.SignedLinePct)
                {
                    continue;
                }

                var breaches = rules.Breaches(line.ReinsurerPartyId, line.SignedLinePct);
                foreach (var rule in new[] { "RATING_FLOOR", "MAX_SHARE" })
                {
                    var applies = rule == "RATING_FLOOR" ? rules.RatingFloor is not null : rules.CapOf(line.ReinsurerPartyId) is not null;
                    if (!applies)
                    {
                        continue;
                    }

                    var ok = !breaches.Contains(rule);
                    checks.Add(new ParticipationCheck
                    {
                        ReinsurerPartyId = new PartyId(line.ReinsurerPartyId), Rule = rule, Ok = ok,
                        Detail = ok ? null : rule == "RATING_FLOOR"
                            ? $"Rating {rules.RatingOf(line.ReinsurerPartyId) ?? "none"} is below the floor {rules.RatingFloor}."
                            : $"A line of {ContractSupport.Percent(line.SignedLinePct)} is over the cap of {ContractSupport.Percent(rules.CapOf(line.ReinsurerPartyId)!.Value)}.",
                    });
                    if (!ok)
                    {
                        errors.Add(new FieldError($"participations.{line.ReinsurerPartyId:D}", rule, "ri." + rule.ToLowerInvariant()));
                    }
                }
            }

            // TODO(hardening): RI.SECURITY_EXCEPTION authority to override a breach with a reason (PRD-08 l.489); for now a breach is refused.
            if (errors.Count > 0)
            {
                return new DomainError(ErrorCode.For(ModuleCode.RI, "SECURITY"), "The panel breaches the security rules in force on the effective date.") { FieldErrors = errors };
            }
        }

        var endorsed = await endorse.HandleAsync(
            new EndorseContract(new ContractEndorseRequest
            {
                ContractId = contractId, ExpectedRecordVersion = expectedRecordVersion, EffectiveFrom = effectiveFrom, Reason = reason,
                Participations = [.. after.Select(l => new RiParticipationInput { ReinsurerPartyId = new PartyId(l.ReinsurerPartyId), BrokerPartyId = l.BrokerPartyId, SignedLinePct = l.SignedLinePct, Lead = l.Lead })],
                PlacedPct = placedPct,
            }),
            cancellationToken).ConfigureAwait(false);
        return endorsed.IsFailure ? endorsed.Error! : (endorsed.Value, checks);
    }
}

/// <summary><c>ri.Participation.add</c> as a command of the platform pipeline.</summary>
internal sealed record AddParticipations(ParticipationAddRequest Request) : ICommand<ParticipationAddResponse>;

/// <summary>Shape rules: a reason, at least one line, lines in (0, 100] with at most six decimals.</summary>
internal sealed class AddParticipationsValidator : AbstractValidator<AddParticipations>
{
    public AddParticipationsValidator()
    {
        RuleFor(c => c.Request).NotNull();
        RuleFor(c => c.Request.ExpectedRecordVersion).GreaterThanOrEqualTo(1).When(c => c.Request is not null);
        RuleFor(c => c.Request.Reason).NotEmpty().MaximumLength(1024).When(c => c.Request is not null);
        RuleFor(c => c.Request.Lines).NotEmpty().When(c => c.Request is not null);
        RuleForEach(c => c.Request.Lines).ChildRules(l =>
        {
            l.RuleFor(x => x.SignedLinePct).GreaterThan(0m).LessThanOrEqualTo(100m).WithErrorCode("PERCENT_RANGE");
            l.RuleFor(x => x.SignedLinePct).Must(v => decimal.Round(v, ContractContent.PercentScale, MidpointRounding.ToZero) == v).WithErrorCode("PERCENT_SCALE");
        }).When(c => c.Request?.Lines is not null);
    }
}

/// <summary>
/// Adds reinsurers to the panel from an effective date. With <c>rebalance</c> (the default) the existing lines make room: they
/// are scaled by the shared largest-remainder allocator (NX-EC-06, at six decimals) so the lines still sum to the placed share.
/// A new lead takes over the lead role.
/// </summary>
internal sealed class AddParticipationsHandler(PanelEndorsement panel) : ICommandHandler<AddParticipations, ParticipationAddResponse>
{
    public async Task<Result<ParticipationAddResponse>> HandleAsync(AddParticipations command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var (current, placed, error) = await panel.CurrentPanelAsync(request.ContractId, cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        var added = request.Lines.Select(l => new ContentLine(l.ReinsurerPartyId.Value, l.BrokerPartyId, l.SignedLinePct, l.Lead)).ToList();
        if (added.Any(a => current!.Any(c => c.ReinsurerPartyId == a.ReinsurerPartyId)) || added.GroupBy(a => a.ReinsurerPartyId).Any(g => g.Count() > 1))
        {
            return new DomainError(ErrorCode.For(ModuleCode.RI, "SIGNED-LINES"), "A reinsurer can appear once in the panel.")
            {
                FieldErrors = [new FieldError("lines", "REINSURER_REPEATED", "ri.reinsurer_repeated")],
            };
        }

        var existing = current!.ToList();
        if (added.Any(a => a.Lead))
        {
            existing = [.. existing.Select(e => e with { Lead = false })];
        }

        var lines = new List<ContentLine>();
        if (request.Rebalance ?? true)
        {
            var room = placed - added.Sum(a => a.SignedLinePct);
            if (room <= 0m)
            {
                return SignedLines("The new lines leave nothing for the existing reinsurers.");
            }

            // Percent at 6 decimals scaled by 10^4 is whole cents for the allocator; ties go to the lead, then the lower id.
            var ordered = existing.OrderByDescending(e => e.Lead).ThenBy(e => e.ReinsurerPartyId).ToList();
            var shares = CentAllocation.Allocate(room * 10000m, [.. ordered.Select(e => e.SignedLinePct)]);
            lines.AddRange(ordered.Select((e, i) => e with { SignedLinePct = shares[i] / 10000m }));
        }
        else
        {
            lines.AddRange(existing);
        }

        lines.AddRange(added);
        var filed = await panel.FileAsync(request.ContractId, request.ExpectedRecordVersion, request.EffectiveFrom, request.Reason, current!, lines, placed, cancellationToken).ConfigureAwait(false);
        return filed.IsFailure
            ? filed.Error!
            : new ParticipationAddResponse { Contract = filed.Value.Endorsed.Contract, ApprovalRequestId = filed.Value.Endorsed.ApprovalRequestId, Checks = filed.Value.Checks };
    }

    private static DomainError SignedLines(string detail) =>
        new(ErrorCode.For(ModuleCode.RI, "SIGNED-LINES"), detail) { FieldErrors = [new FieldError("lines", "SIGNED_LINES_DIFFER_FROM_PLACED", "ri.signed_lines_differ_from_placed")] };
}

/// <summary><c>ri.Participation.replace</c> as a command of the platform pipeline.</summary>
internal sealed record ReplaceParticipation(ParticipationReplaceRequest Request) : ICommand<ParticipationReplaceResponse>;

/// <summary>Shape rules: a reason.</summary>
internal sealed class ReplaceParticipationValidator : AbstractValidator<ReplaceParticipation>
{
    public ReplaceParticipationValidator()
    {
        RuleFor(c => c.Request).NotNull();
        RuleFor(c => c.Request.ExpectedRecordVersion).GreaterThanOrEqualTo(1).When(c => c.Request is not null);
        RuleFor(c => c.Request.Reason).NotEmpty().MaximumLength(1024).When(c => c.Request is not null);
    }
}

/// <summary>Replaces one reinsurer by another from an effective date; the incoming reinsurer takes the outgoing line and lead role, so the shares sum as before.</summary>
internal sealed class ReplaceParticipationHandler(PanelEndorsement panel) : ICommandHandler<ReplaceParticipation, ParticipationReplaceResponse>
{
    public async Task<Result<ParticipationReplaceResponse>> HandleAsync(ReplaceParticipation command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var (current, placed, error) = await panel.CurrentPanelAsync(request.ContractId, cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        var outgoing = current!.FirstOrDefault(l => l.ReinsurerPartyId == request.OutgoingReinsurerPartyId);
        if (outgoing is null)
        {
            return RiErrors.Validation("outgoingReinsurerPartyId", "REINSURER_NOT_ON_PANEL", "The outgoing reinsurer is not on the panel.");
        }

        if (current!.Any(l => l.ReinsurerPartyId == request.IncomingReinsurerPartyId))
        {
            return RiErrors.Validation("incomingReinsurerPartyId", "REINSURER_REPEATED", "The incoming reinsurer is already on the panel.");
        }

        var lines = current!.Select(l => l.ReinsurerPartyId == outgoing.ReinsurerPartyId
            ? new ContentLine(request.IncomingReinsurerPartyId, request.BrokerPartyId ?? outgoing.BrokerPartyId, outgoing.SignedLinePct, outgoing.Lead)
            : l).ToList();
        var filed = await panel.FileAsync(request.ContractId, request.ExpectedRecordVersion, request.EffectiveFrom, request.Reason, current!, lines, placed, cancellationToken).ConfigureAwait(false);
        return filed.IsFailure
            ? filed.Error!
            : new ParticipationReplaceResponse { Contract = filed.Value.Endorsed.Contract, ApprovalRequestId = filed.Value.Endorsed.ApprovalRequestId, Checks = filed.Value.Checks };
    }
}

/// <summary>Audit facts of <c>ri.Participation.add</c>.</summary>
internal sealed class AddParticipationsAuditor : ICommandAuditor<AddParticipations, ParticipationAddResponse>
{
    public CommandAuditFacts Describe(AddParticipations command, Result<ParticipationAddResponse>? result) =>
        PanelAudit.Describe(command.Request.ContractId, command.Request.EffectiveFrom, command.Request.Reason, result is { IsSuccess: true } ok ? ok.Value.Contract : null, result is { IsSuccess: true } ok2 ? ok2.Value.ApprovalRequestId : null);
}

/// <summary>Audit facts of <c>ri.Participation.replace</c>.</summary>
internal sealed class ReplaceParticipationAuditor : ICommandAuditor<ReplaceParticipation, ParticipationReplaceResponse>
{
    public CommandAuditFacts Describe(ReplaceParticipation command, Result<ParticipationReplaceResponse>? result) =>
        PanelAudit.Describe(command.Request.ContractId, command.Request.EffectiveFrom, command.Request.Reason, result is { IsSuccess: true } ok ? ok.Value.Contract : null, result is { IsSuccess: true } ok2 ? ok2.Value.ApprovalRequestId : null);
}

internal static class PanelAudit
{
    public static CommandAuditFacts Describe(RiContractId contractId, BusinessDate effectiveFrom, string reason, RiContractView? view, ApprovalRequestId? approval)
    {
        var subject = ObjectRef.For(ModuleCode.RI, "Contract", contractId);
        var keys = BusinessKeys.Empty.With("riContractId", contractId.Value.ToString("D"));
        if (view is null)
        {
            return new CommandAuditFacts { ObjectRef = subject, BusinessKeys = keys };
        }

        return new CommandAuditFacts
        {
            ObjectRef = subject,
            ObjectNumber = view.ContractNumber,
            BusinessKeys = approval is { } a ? keys.With("approvalRequestId", a.Value.ToString("D")) : keys,
            Changes = AuditDiff.Compute(
                new { versionNo = view.VersionNo - 1 },
                new { versionNo = view.VersionNo, effectiveFrom = effectiveFrom.ToString(), status = "PENDING", reason, panel = view.Participations.Count }),
        };
    }
}
