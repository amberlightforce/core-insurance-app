using CoreIns.Modules.Finance.Contracts.Api;
using CoreIns.Modules.Finance.Domain;
using CoreIns.Modules.Finance.Services;
using CoreIns.Platform;
using CoreIns.Platform.Approvals;
using CoreIns.Platform.Authority;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Finance.Commands;

/// <summary>A command of the levy return workflow (<c>fin.LevyReturn.*</c>, BO-FIN-10). The ones that change a return carry a reason that is audited.</summary>
internal interface ILevyReturnCommand<TResult> : ICommand<TResult>
{
    Guid? ReturnId { get; }

    string? AuditReason { get; }

    Task<Result<TResult>> ExecuteAsync(LevyReturnService service, CancellationToken cancellationToken);
}

internal sealed record RecalculateLevyReturn(string Period, string? LevyType) : ILevyReturnCommand<LevyReturnRecalculateResponse>
{
    public Guid? ReturnId => null;

    public string? AuditReason => null;

    public Task<Result<LevyReturnRecalculateResponse>> ExecuteAsync(LevyReturnService service, CancellationToken cancellationToken) => service.RecalculateAsync(Period, LevyType, cancellationToken);
}

internal sealed record AddLevyReturnAdjustment(LevyReturnAddAdjustmentRequest Request) : ILevyReturnCommand<LevyReturnAddAdjustmentResponse>
{
    public Guid? ReturnId => Request.ReturnId;

    public string? AuditReason => Request.Reason;

    public Task<Result<LevyReturnAddAdjustmentResponse>> ExecuteAsync(LevyReturnService service, CancellationToken cancellationToken) =>
        service.AddAdjustmentAsync(Request.ReturnId, Request.Amount, Request.Reason, Request.EvidenceRef, Request.ExplainsReconciliation ?? false, cancellationToken);
}

internal sealed record GenerateLevyReturnFile(Guid Id) : ILevyReturnCommand<LevyReturnGenerateFileResponse>
{
    public Guid? ReturnId => Id;

    public string? AuditReason => null;

    public Task<Result<LevyReturnGenerateFileResponse>> ExecuteAsync(LevyReturnService service, CancellationToken cancellationToken) => service.GenerateFileAsync(Id, cancellationToken);
}

internal sealed record TransitionLevyReturn(LevyReturnTransitionRequest Request) : ILevyReturnCommand<LevyReturnTransitionResponse>
{
    public Guid? ReturnId => Request.ReturnId;

    public string? AuditReason => Request.Comment;

    public Task<Result<LevyReturnTransitionResponse>> ExecuteAsync(LevyReturnService service, CancellationToken cancellationToken) =>
        service.TransitionAsync(Request.ReturnId, Request.Action, Request.Comment, cancellationToken);
}

internal sealed record RecordLevyReturnFiling(LevyReturnRecordFilingRequest Request) : ILevyReturnCommand<LevyReturnRecordFilingResponse>
{
    public Guid? ReturnId => Request.ReturnId;

    public string? AuditReason => Request.FilingReference;

    public Task<Result<LevyReturnRecordFilingResponse>> ExecuteAsync(LevyReturnService service, CancellationToken cancellationToken) =>
        service.RecordFilingAsync(Request.ReturnId, Request.FilingReference, Request.FiledOn, cancellationToken);
}

internal sealed record RequestLevyReturnPayment(LevyReturnRequestPaymentRequest Request) : ILevyReturnCommand<LevyReturnRequestPaymentResponse>
{
    public Guid? ReturnId => Request.ReturnId;

    public string? AuditReason => null;

    public Task<Result<LevyReturnRequestPaymentResponse>> ExecuteAsync(LevyReturnService service, CancellationToken cancellationToken) =>
        service.RequestPaymentAsync(Request.ReturnId, Request.PayeePartyId.Value, Request.PayeeAccountId, Request.RequestedValueDate, cancellationToken);
}

internal sealed class LevyReturnValidator<TCommand, TResult> : AbstractValidator<TCommand>
    where TCommand : ILevyReturnCommand<TResult>
{
    public LevyReturnValidator()
    {
        RuleFor(c => c.AuditReason).MaximumLength(1000);
    }
}

internal sealed class LevyReturnAuditor<TCommand, TResult> : ICommandAuditor<TCommand, TResult>
    where TCommand : ILevyReturnCommand<TResult>
{
    public CommandAuditFacts Describe(TCommand command, Result<TResult>? result) => new()
    {
        ObjectRef = new ObjectRef(ModuleCode.FIN, "LevyReturn", command.ReturnId?.ToString("D") ?? "none"),
        BusinessKeys = command.ReturnId is { } id ? BusinessKeys.Empty.With("returnId", id.ToString("D")) : BusinessKeys.Empty,
    };
}

internal sealed class LevyReturnHandler<TCommand, TResult>(LevyReturnService service, RequestContext context) : ICommandHandler<TCommand, TResult>
    where TCommand : ILevyReturnCommand<TResult>
{
    public Task<Result<TResult>> HandleAsync(TCommand command, CancellationToken cancellationToken)
    {
        // The reason of an adjustment, a decision or a filing goes to the audit trail with the command.
        context.Reason = string.IsNullOrWhiteSpace(command.AuditReason) ? null : command.AuditReason.Trim();
        return command.ExecuteAsync(service, cancellationToken);
    }
}

/// <summary>Registration of the levy return service, commands and the maker-checker approval type (called once from <c>FinanceModule</c>).</summary>
internal static class LevyReturnRegistration
{
    public static IServiceCollection AddLevyReturns(this IServiceCollection services)
    {
        services.AddScoped<LevyReturnService>();
        services.AddAuthorityType(LevyReturnApproval.Definition);
        services.AddOwnerDecidedApprovalType(LevyReturnApproval.Type);
        Register<RecalculateLevyReturn, LevyReturnRecalculateResponse>(services, "fin.LevyReturn.recalculate", dryRun: true);
        Register<AddLevyReturnAdjustment, LevyReturnAddAdjustmentResponse>(services, "fin.LevyReturn.addAdjustment");
        Register<GenerateLevyReturnFile, LevyReturnGenerateFileResponse>(services, "fin.LevyReturn.generateFile", dryRun: true);
        Register<TransitionLevyReturn, LevyReturnTransitionResponse>(services, "fin.LevyReturn.transition");
        Register<RecordLevyReturnFiling, LevyReturnRecordFilingResponse>(services, "fin.LevyReturn.recordFiling");
        Register<RequestLevyReturnPayment, LevyReturnRequestPaymentResponse>(services, "fin.LevyReturn.requestPayment");
        return services;
    }

    private static void Register<TCommand, TResult>(IServiceCollection services, string operation, bool dryRun = false)
        where TCommand : class, ILevyReturnCommand<TResult>
    {
        services.AddScoped<IValidator<TCommand>, LevyReturnValidator<TCommand, TResult>>();
        services.AddCommandAuditor<TCommand, TResult, LevyReturnAuditor<TCommand, TResult>>();
        services.AddCommand<TCommand, TResult, LevyReturnHandler<TCommand, TResult>>(CommandDescriptor.For(operation) with { SupportsDryRun = dryRun });
    }
}
