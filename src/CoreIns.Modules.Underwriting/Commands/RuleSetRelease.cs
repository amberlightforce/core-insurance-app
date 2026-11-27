using System.Globalization;
using CoreIns.Modules.Underwriting.Authority;
using CoreIns.Modules.Underwriting.Contracts.Api;
using CoreIns.Modules.Underwriting.Domain;
using CoreIns.Modules.Underwriting.Queries;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Contracts.Api;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;

namespace CoreIns.Modules.Underwriting.Commands;

/// <summary><c>uw.RuleSet.submit</c>: a valid Draft goes to maker-checker approval (BO-UW-02).</summary>
internal sealed record SubmitRuleSet(RuleSetSubmitRequest Request) : ICommand<RuleSetReleaseResponse>;

/// <summary><c>uw.RuleSet.approve</c>: the checker approves or rejects a Submitted version.</summary>
internal sealed record ApproveRuleSet(RuleSetApproveRequest Request) : ICommand<RuleSetReleaseResponse>;

/// <summary><c>uw.RuleSet.schedule</c>: an Approved version becomes Active from a date (never in the past).</summary>
internal sealed record ScheduleRuleSet(RuleSetScheduleRequest Request) : ICommand<RuleSetReleaseResponse>;

internal sealed class SubmitRuleSetValidator : AbstractValidator<SubmitRuleSet>
{
    public SubmitRuleSetValidator()
    {
        RuleFor(c => c.Request.RuleSetCode).NotEmpty().MaximumLength(64);
        RuleFor(c => c.Request.Version).NotEmpty().MaximumLength(16);
        RuleFor(c => c.Request.Reason).MaximumLength(512);
    }
}

internal sealed class ApproveRuleSetValidator : AbstractValidator<ApproveRuleSet>
{
    public ApproveRuleSetValidator()
    {
        RuleFor(c => c.Request.RuleSetCode).NotEmpty().MaximumLength(64);
        RuleFor(c => c.Request.Version).NotEmpty().MaximumLength(16);
        RuleFor(c => c.Request.Reason).MaximumLength(512);
        RuleFor(c => c.Request.Reason).NotEmpty().When(c => c.Request.Decision == RuleSetApproveRequest.DecisionValue.Reject).WithMessage("A reason is required to reject.");
    }
}

internal sealed class ScheduleRuleSetValidator : AbstractValidator<ScheduleRuleSet>
{
    public ScheduleRuleSetValidator()
    {
        RuleFor(c => c.Request.RuleSetCode).NotEmpty().MaximumLength(64);
        RuleFor(c => c.Request.Version).NotEmpty().MaximumLength(16);
        RuleFor(c => c.Request.EffectiveFrom)
            .Must(d => DateOnly.TryParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            .When(c => c.Request.EffectiveFrom is not null).WithMessage("effectiveFrom must be a date, yyyy-MM-dd.");
    }
}

/// <summary>Shared pieces of the release handlers: the frozen hash the approval binds, the PLT subject, the loaded version.</summary>
internal sealed class RuleSetReleaseSupport(RuleSetAuthoringStore store, RuleSetAuthoringSupport authoring, IPlatformApprovalService approvals)
{
    public IPlatformApprovalService Approvals => approvals;

    public RuleSetAuthoringSupport Authoring => authoring;

    /// <summary>
    /// The hash the PLT approval is bound to: the definition without its version number and its effective date, so the approval
    /// still covers the same rules when the date is chosen at schedule time and a changed rule cannot ride on an old approval.
    /// </summary>
    public static Sha256Hash ApprovalHash(RuleSetDto dto) => Sha256Hash.Parse(RuleSetAuthoring.DefinitionHash(dto with { EffectiveFrom = string.Empty }));

    public static ObjectRef Subject(string code, string version) => new(ModuleCode.UW, "RuleSet", code + "@" + version);

    public static DomainError NotFound(string code, string version) => DomainError.Of(ModuleCode.UW, "RULESET-NOT-FOUND", $"Rule set {code} {version} does not exist.");

    public static DomainError State(string code, string version, string status, string need) =>
        DomainError.Of(ModuleCode.UW, "RULESET-STATE", $"Rule set {code} {version} is {status}; it must be {need} for this step.");

    public async Task<RuleSetVersionRecord?> LoadAsync(string code, string version, CancellationToken cancellationToken) =>
        await store.GetAsync(code, version, cancellationToken).ConfigureAwait(false);

    public async Task<RuleSetReleaseResponse> ResponseAsync(string code, string version, Guid? approvalRequestId, bool? inEffect, CancellationToken cancellationToken)
    {
        var stored = await store.GetAsync(code, version, cancellationToken).ConfigureAwait(false) ?? throw new DomainException(NotFound(code, version));
        var dto = RuleSetJson.Deserialize(stored.Definition);
        return new RuleSetReleaseResponse
        {
            Version = RuleSetAuthoringSupport.ToDetail(stored, dto),
            ApprovalRequestId = (approvalRequestId ?? stored.ApprovalRequestId)?.ToString("D"),
            InEffect = inEffect,
        };
    }
}

internal sealed class SubmitRuleSetHandler(RuleSetAuthoringStore store, RuleSetReleaseSupport release, RequestContext context) : ICommandHandler<SubmitRuleSet, RuleSetReleaseResponse>
{
    public async Task<Result<RuleSetReleaseResponse>> HandleAsync(SubmitRuleSet command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        try
        {
            var record = await release.LoadAsync(request.RuleSetCode, request.Version, cancellationToken).ConfigureAwait(false);
            if (record is null)
            {
                return RuleSetReleaseSupport.NotFound(request.RuleSetCode, request.Version);
            }

            if (record.Status != "Draft")
            {
                return RuleSetReleaseSupport.State(record.RuleSetCode, record.VersionNo, record.Status, "a Draft");
            }

            // The submit gate: no findings, every rule tested (REQ-UW-036, -038, -040). The stored hash may be the draft's definition hash.
            var dto = RuleSetJson.Deserialize(record.Definition);
            var analysis = RuleSetAnalysis.Analyse(dto);
            if (!analysis.Valid || analysis.ContentHash is null)
            {
                var detail = string.Join(" ", analysis.Issues.Take(5).Select(i => i.Message));
                var code = analysis.Issues.Any(i => i.Code == RuleSetAnalysis.CodeUntested) ? "RULE-UNTESTED" : "VALIDATION";
                return DomainError.Of(ModuleCode.UW, code, $"Rule set {record.RuleSetCode} {record.VersionNo} cannot be submitted: {detail}");
            }

            var approval = await release.Approvals.RequestAsync(
                new ApprovalRequestRequest
                {
                    Type = UnderwritingAuthorityTypes.RuleSetApprovalType,
                    ObjectRef = RuleSetReleaseSupport.Subject(record.RuleSetCode, record.VersionNo),
                    PayloadHash = RuleSetReleaseSupport.ApprovalHash(dto),
                    Editors = context.OnBehalfOf is { } principal ? [context.Actor.ToString(), principal.ToString()] : [context.Actor.ToString()],
                    Authority = new ApprovalAuthority
                    {
                        Type = UnderwritingAuthorityTypes.RuleSetApproval.Value,
                        Codes = new Dictionary<string, string>(StringComparer.Ordinal) { [UnderwritingAuthorityTypes.ProductDimension] = record.ProductCode },
                    },
                    ReferralRole = UnderwritingAuthorityTypes.RuleSetCheckerRole,
                    Reason = $"Release of rule set {record.RuleSetCode} {record.VersionNo} for {record.ProductCode} {record.Checkpoint}: {request.Reason ?? "no reason given"}",
                },
                CommandOptions.New(),
                cancellationToken).ConfigureAwait(false);

            if (!await store.MarkSubmittedAsync(record.RuleSetCode, record.VersionNo, analysis.ContentHash, approval.Request.RequestId, cancellationToken).ConfigureAwait(false))
            {
                return DomainError.Of(ModuleCode.UW, "STALE", "The version was changed by someone else meanwhile; reload.");
            }

            return await release.ResponseAsync(record.RuleSetCode, record.VersionNo, approval.Request.RequestId, null, cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }
    }
}

internal sealed class ApproveRuleSetHandler(RuleSetAuthoringStore store, RuleSetReleaseSupport release) : ICommandHandler<ApproveRuleSet, RuleSetReleaseResponse>
{
    public async Task<Result<RuleSetReleaseResponse>> HandleAsync(ApproveRuleSet command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        try
        {
            var record = await release.LoadAsync(request.RuleSetCode, request.Version, cancellationToken).ConfigureAwait(false);
            if (record is null)
            {
                return RuleSetReleaseSupport.NotFound(request.RuleSetCode, request.Version);
            }

            if (record.Status != "Submitted" || record.ApprovalRequestId is not { } approvalId)
            {
                return RuleSetReleaseSupport.State(record.RuleSetCode, record.VersionNo, record.Status, "Submitted");
            }

            var dto = RuleSetJson.Deserialize(record.Definition);
            var hash = RuleSetReleaseSupport.ApprovalHash(dto);
            var subject = RuleSetReleaseSupport.Subject(record.RuleSetCode, record.VersionNo);
            var approve = request.Decision != RuleSetApproveRequest.DecisionValue.Reject;

            // plt.Approval.decide: a human checker who is not the maker or an editor, holding UW.RULESET_APPROVAL for the product (PLT enforces it).
            await release.Approvals.DecideAsync(
                new ApprovalDecideRequest
                {
                    RequestId = approvalId,
                    PayloadHash = hash,
                    Decision = approve ? ApprovalDecideRequest.DecisionValue.Approve : ApprovalDecideRequest.DecisionValue.Reject,
                    Comment = request.Reason,
                },
                CommandOptions.New(),
                cancellationToken).ConfigureAwait(false);

            if (approve)
            {
                var verified = await release.Approvals.VerifyForExecutionAsync(
                    new ApprovalVerifyForExecutionRequest { RequestId = approvalId, Hash = hash, Type = UnderwritingAuthorityTypes.RuleSetApprovalType, ObjectRef = subject },
                    cancellationToken).ConfigureAwait(false);
                if (!verified.Ok || verified.Authority.Type != UnderwritingAuthorityTypes.RuleSetApproval.Value
                    || verified.Authority.Codes?.GetValueOrDefault(UnderwritingAuthorityTypes.ProductDimension) != record.ProductCode)
                {
                    return DomainError.Of(ModuleCode.UW, "SOD", "The approval does not cover the release of this rule set.");
                }
            }

            if (!await store.MarkDecidedAsync(record.RuleSetCode, record.VersionNo, approve ? "Approved" : "Draft", request.Reason, cancellationToken).ConfigureAwait(false))
            {
                return DomainError.Of(ModuleCode.UW, "STALE", "The version was decided meanwhile; reload.");
            }

            return await release.ResponseAsync(record.RuleSetCode, record.VersionNo, approvalId, null, cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }
    }
}

internal sealed class ScheduleRuleSetHandler(RuleSetAuthoringStore store, RuleSetReleaseSupport release) : ICommandHandler<ScheduleRuleSet, RuleSetReleaseResponse>
{
    public async Task<Result<RuleSetReleaseResponse>> HandleAsync(ScheduleRuleSet command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        try
        {
            var record = await release.LoadAsync(request.RuleSetCode, request.Version, cancellationToken).ConfigureAwait(false);
            if (record is null)
            {
                return RuleSetReleaseSupport.NotFound(request.RuleSetCode, request.Version);
            }

            if (record.Status != "Approved" || record.ApprovalRequestId is not { } approvalId)
            {
                var why = record.Status is "Active" ? " An active version is never changed; draft a new version." : string.Empty;
                return DomainError.Of(ModuleCode.UW, "RULESET-STATE", $"Rule set {record.RuleSetCode} {record.VersionNo} is {record.Status}; only an Approved version can be scheduled.{why}");
            }

            var today = RuleSetAuthoringSupport.Date(release.Authoring.Today());
            var from = request.EffectiveFrom is null ? today : RuleSetAuthoringSupport.Date(request.EffectiveFrom);
            if (from < today)
            {
                return DomainError.Of(ModuleCode.UW, "VALIDATION", $"effectiveFrom {from:yyyy-MM-dd} is in the past (today is {today:yyyy-MM-dd}); activation is never retroactive.");
            }

            // The approval must still cover exactly these rules; the effective date is chosen here and is not part of the approval.
            var dto = RuleSetJson.Deserialize(record.Definition) with { EffectiveFrom = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
            var verified = await release.Approvals.VerifyForExecutionAsync(
                new ApprovalVerifyForExecutionRequest
                {
                    RequestId = approvalId, Hash = RuleSetReleaseSupport.ApprovalHash(dto), Type = UnderwritingAuthorityTypes.RuleSetApprovalType,
                    ObjectRef = RuleSetReleaseSupport.Subject(record.RuleSetCode, record.VersionNo),
                },
                cancellationToken).ConfigureAwait(false);
            if (!verified.Ok)
            {
                return DomainError.Of(ModuleCode.UW, "SOD", "The version does not match its approval; submit it again.");
            }

            var compiled = CompiledRuleSet.Compile(dto); // the activation gate: compiles, test cases pass, every rule covered

            await store.LockProductAsync(record.ProductCode, record.Checkpoint, cancellationToken).ConfigureAwait(false);
            if (await store.ActiveOnAsync(record.ProductCode, record.Checkpoint, from, cancellationToken).ConfigureAwait(false) is { } clash)
            {
                return DomainError.Of(ModuleCode.UW, "RULESET-STATE", $"{clash} already takes effect on {from:yyyy-MM-dd} for {record.ProductCode} {record.Checkpoint}; choose a later date.");
            }

            if (!await store.ActivateAsync(record.RuleSetCode, record.VersionNo, from, compiled.Hash, RuleSetJson.Serialize(dto), cancellationToken).ConfigureAwait(false))
            {
                return DomainError.Of(ModuleCode.UW, "STALE", "The version was scheduled meanwhile; reload.");
            }

            // TODO(hardening): publish an event when a rule-set version is activated (no UW contract event exists yet).
            return await release.ResponseAsync(record.RuleSetCode, record.VersionNo, approvalId, from <= today, cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }
    }
}

internal sealed class RuleSetReleaseAuditor<TCommand> : ICommandAuditor<TCommand, RuleSetReleaseResponse>
    where TCommand : ICommand<RuleSetReleaseResponse>
{
    public CommandAuditFacts Describe(TCommand command, Result<RuleSetReleaseResponse>? result)
    {
        var version = result is { IsSuccess: true } done ? done.Value.Version : null;
        return new CommandAuditFacts
        {
            ObjectRef = new ObjectRef(ModuleCode.UW, "RuleSet", version is null ? "unknown" : version.RuleSetCode + "@" + version.Version),
            BusinessKeys = version is null ? BusinessKeys.Empty : BusinessKeys.Empty.With("ruleSetCode", version.RuleSetCode).With("version", version.Version),
        };
    }
}
