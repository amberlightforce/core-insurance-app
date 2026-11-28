using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Modules.Policy.Domain;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;

namespace CoreIns.Modules.Policy.Commands.Suspension;

/// <summary><c>pol.Reactivation.create</c>: resume the cover of a suspended term (REQ-POL-236).</summary>
internal sealed record ReactivatePolicy(ReactivationCreateRequest Request) : ICommand<ReactivationCreateResponse>;

internal sealed class ReactivatePolicyValidator : AbstractValidator<ReactivatePolicy>
{
    public ReactivatePolicyValidator()
    {
        RuleFor(c => c.Request.TermId).NotNull().WithErrorCode("TERM_REQUIRED");
        RuleFor(c => c.Request.EffectiveAt).NotNull().WithErrorCode("EFFECTIVE_AT_REQUIRED");
        RuleFor(c => c.Request.Reason).Must(SuspensionRequestChecks.IsReason).WithErrorCode("REASON_REQUIRED");
    }
}

/// <summary>Reactivates the cover from the effective time: premium is earned again (the suspended period stays recorded by the two transactions).</summary>
internal sealed class ReactivatePolicyHandler(SuspensionService service) : ICommandHandler<ReactivatePolicy, ReactivationCreateResponse>
{
    public async Task<Result<ReactivationCreateResponse>> HandleAsync(ReactivatePolicy command, CancellationToken cancellationToken)
    {
        var r = command.Request;
        var applied = await service.ApplyAsync(
            SuspensionAction.Reactivate, new SuspensionInput(r.TermId!.Value, r.EffectiveAt!.Value, SuspensionRequestChecks.ReasonText(r.Reason), r.Evidence, r.ElementLocator),
            cancellationToken).ConfigureAwait(false);
        return applied.IsFailure ? applied.Error! : new ReactivationCreateResponse { JobId = applied.Value };
    }
}

/// <summary>Audit facts of <c>pol.Reactivation.create</c>.</summary>
internal sealed class ReactivatePolicyAuditor : ICommandAuditor<ReactivatePolicy, ReactivationCreateResponse>
{
    public CommandAuditFacts Describe(ReactivatePolicy command, Result<ReactivationCreateResponse>? result)
    {
        var facts = new CommandAuditFacts { ObjectRef = ObjectRef.For(ModuleCode.POL, "Term", command.Request.TermId ?? default) };
        return result is { IsSuccess: true } success
            ? facts with
            {
                BusinessKeys = BusinessKeys.Empty.With("jobId", success.Value.JobId?.Value.ToString() ?? string.Empty).With("termId", command.Request.TermId.ToString() ?? string.Empty),
                Changes = AuditDiff.Compute(null, new { effectiveAt = command.Request.EffectiveAt?.ToString(), reason = SuspensionRequestChecks.IsReason(command.Request.Reason) ? SuspensionRequestChecks.ReasonText(command.Request.Reason) : null }),
            }
            : facts;
    }
}
