using System.Text.Json.Nodes;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts.Api;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.Platform.Persistence;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;

namespace CoreIns.Platform.Approvals;

internal sealed record WithdrawApproval(ApprovalWithdrawRequest Request, ModuleCode? CallerModule) : ICommand<ApprovalWithdrawResponse>;

internal sealed class WithdrawApprovalValidator : AbstractValidator<WithdrawApproval>
{
    public WithdrawApprovalValidator()
    {
        RuleFor(c => c.Request).NotNull();
        RuleFor(c => c.Request.Reason).NotEmpty().MaximumLength(1024).When(c => c.Request is not null);
    }
}

/// <summary>Only a trusted registered owning-module command can withdraw; caller identity is not a public claim.</summary>
internal sealed class WithdrawApprovalHandler(DbSession session, RequestContext context, IClock clock, IEventPublisher events)
    : ICommandHandler<WithdrawApproval, ApprovalWithdrawResponse>
{
    public async Task<Result<ApprovalWithdrawResponse>> HandleAsync(WithdrawApproval command, CancellationToken cancellationToken)
    {
        if (command.CallerModule is null)
        {
            return ApprovalErrors.Of("NOT-OWNER", "Withdrawal requires an owning module command.");
        }

        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var transaction = session.Transaction ?? throw new InvalidOperationException("Withdrawal requires the command transaction.");
        var legalEntity = context.LegalEntity?.Value ?? throw new InvalidOperationException("The request context has no legal entity.");
        var row = await ApprovalStore.FindAsync(connection, transaction, legalEntity, command.Request.ApprovalRequestId.Value, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return DomainError.Of(ModuleCode.PLT, PlatformErrors.NotFound, "The approval request does not exist.");
        }

        if (row.ObjectRef.Module != command.CallerModule)
        {
            return ApprovalErrors.Of("NOT-OWNER", "Only the subject's owning module can withdraw the request.");
        }

        context.Reason = command.Request.Reason;
        if (row.Status == ApprovalStatus.Withdrawn)
        {
            return Response(row);
        }

        if (row.Status != ApprovalStatus.PendingApproval)
        {
            return ApprovalErrors.Of(ApprovalErrors.Stale, "A decided request cannot be withdrawn.");
        }

        var now = clock.Now;
        if (!await ApprovalStore.TryWithdrawWithReasonAsync(connection, transaction, row.RequestId, row.Version, now, command.Request.Reason, cancellationToken).ConfigureAwait(false))
        {
            var current = await ApprovalStore.FindAsync(connection, transaction, legalEntity, row.RequestId, cancellationToken).ConfigureAwait(false);
            return current is { Status: ApprovalStatus.Withdrawn } ? Response(current)
                : ApprovalErrors.Of(ApprovalErrors.Stale, "The approval request was decided meanwhile; reload it.");
        }

        var withdrawn = row with { Status = ApprovalStatus.Withdrawn, Comment = command.Request.Reason, DecidedAt = now, Version = row.Version + 1 };
        ApprovalEvents.PublishDecided(events, withdrawn, ApprovalStore.Withdrawn, null, now);
        return Response(withdrawn);
    }

    private static ApprovalWithdrawResponse Response(ApprovalRow row) => new()
    {
        ApprovalRequestId = new ApprovalRequestId(row.RequestId), Status = ApprovalStatus.Withdrawn,
        WithdrawnAt = row.DecidedAt ?? throw new InvalidOperationException("A withdrawn request has no withdrawal time."),
    };
}

internal sealed class WithdrawApprovalAuditor : ICommandAuditor<WithdrawApproval, ApprovalWithdrawResponse>
{
    public CommandAuditFacts Describe(WithdrawApproval command, Result<ApprovalWithdrawResponse>? result) => new()
    {
        ObjectRef = command.Request is null ? null : ObjectRef.For(ModuleCode.PLT, ApprovalEvents.AggregateType, command.Request.ApprovalRequestId),
        Changes = result is { IsSuccess: true } ? [new AuditChange("status", null, JsonValue.Create(nameof(ApprovalStatus.Withdrawn)))] : [],
        BusinessKeys = command.Request is null ? BusinessKeys.Empty : BusinessKeys.Empty.With("requestId", command.Request.ApprovalRequestId.ToString()),
    };
}
