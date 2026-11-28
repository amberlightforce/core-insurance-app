using System.Text.Json;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Modules.Policy.Domain;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;

namespace CoreIns.Modules.Policy.Commands.Suspension;

/// <summary><c>pol.Suspension.create</c>: suspend the cover of a term (REQ-POL-236).</summary>
internal sealed record SuspendPolicy(SuspensionCreateRequest Request) : ICommand<SuspensionCreateResponse>;

/// <summary>Shared field checks of the suspension and reactivation requests.</summary>
internal static class SuspensionRequestChecks
{
    /// <summary>The reason is free text in the contract (Unspecified): a non-blank string of at most 500 characters.</summary>
    public static bool IsReason(JsonElement? reason) =>
        reason is { ValueKind: JsonValueKind.String } r && !string.IsNullOrWhiteSpace(r.GetString()) && r.GetString()!.Length <= 500;

    public static string ReasonText(JsonElement? reason) => reason!.Value.GetString()!.Trim();
}

internal sealed class SuspendPolicyValidator : AbstractValidator<SuspendPolicy>
{
    public SuspendPolicyValidator()
    {
        RuleFor(c => c.Request.TermId).NotNull().WithErrorCode("TERM_REQUIRED");
        RuleFor(c => c.Request.EffectiveAt).NotNull().WithErrorCode("EFFECTIVE_AT_REQUIRED");
        RuleFor(c => c.Request.Reason).Must(SuspensionRequestChecks.IsReason).WithErrorCode("REASON_REQUIRED");
    }
}

/// <summary>Suspends the cover of the term from the effective time: no refund, premium stops being earned (<c>suspended_from</c>).</summary>
internal sealed class SuspendPolicyHandler(SuspensionService service) : ICommandHandler<SuspendPolicy, SuspensionCreateResponse>
{
    public async Task<Result<SuspensionCreateResponse>> HandleAsync(SuspendPolicy command, CancellationToken cancellationToken)
    {
        var r = command.Request;
        var applied = await service.ApplyAsync(
            SuspensionAction.Suspend, new SuspensionInput(r.TermId!.Value, r.EffectiveAt!.Value, SuspensionRequestChecks.ReasonText(r.Reason), r.Evidence, r.ElementLocator),
            cancellationToken).ConfigureAwait(false);
        return applied.IsFailure ? applied.Error! : new SuspensionCreateResponse { JobId = applied.Value };
    }
}

/// <summary>Audit facts of <c>pol.Suspension.create</c>.</summary>
internal sealed class SuspendPolicyAuditor : ICommandAuditor<SuspendPolicy, SuspensionCreateResponse>
{
    public CommandAuditFacts Describe(SuspendPolicy command, Result<SuspensionCreateResponse>? result)
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
