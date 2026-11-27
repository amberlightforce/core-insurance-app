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

/// <summary>A command of the IPT return workflow (<c>fin.TaxReturn.*</c>, BO-FIN-09). The ones that change a return carry a reason that is audited.</summary>
internal interface ITaxReturnCommand<TResult> : ICommand<TResult>
{
    Guid? ReturnId { get; }

    string? AuditReason { get; }

    Task<Result<TResult>> ExecuteAsync(TaxReturnService service, CancellationToken cancellationToken);
}

internal sealed record RecalculateTaxReturn(string Period, string? TaxCode) : ITaxReturnCommand<TaxReturnRecalculateResponse>
{
    public Guid? ReturnId => null;

    public string? AuditReason => null;

    public Task<Result<TaxReturnRecalculateResponse>> ExecuteAsync(TaxReturnService service, CancellationToken cancellationToken) => service.RecalculateAsync(Period, TaxCode, cancellationToken);
}

internal sealed record AddTaxReturnAdjustment(TaxReturnAddAdjustmentRequest Request) : ITaxReturnCommand<TaxReturnAddAdjustmentResponse>
{
    public Guid? ReturnId => Request.ReturnId;

    public string? AuditReason => Request.Reason;

    public Task<Result<TaxReturnAddAdjustmentResponse>> ExecuteAsync(TaxReturnService service, CancellationToken cancellationToken) =>
        service.AddAdjustmentAsync(Request.ReturnId, Request.Amount, Request.Reason, Request.EvidenceRef, Request.ExplainsReconciliation ?? false, cancellationToken);
}

internal sealed record GenerateTaxReturnFile(Guid Id) : ITaxReturnCommand<TaxReturnGenerateFileResponse>
{
    public Guid? ReturnId => Id;

    public string? AuditReason => null;

    public Task<Result<TaxReturnGenerateFileResponse>> ExecuteAsync(TaxReturnService service, CancellationToken cancellationToken) => service.GenerateFileAsync(Id, cancellationToken);
}

internal sealed record TransitionTaxReturn(TaxReturnTransitionRequest Request) : ITaxReturnCommand<TaxReturnTransitionResponse>
{
    public Guid? ReturnId => Request.ReturnId;

    public string? AuditReason => Request.Comment;

    public Task<Result<TaxReturnTransitionResponse>> ExecuteAsync(TaxReturnService service, CancellationToken cancellationToken) =>
        service.TransitionAsync(Request.ReturnId, Request.Action, Request.Comment, cancellationToken);
}

internal sealed record RecordTaxReturnFiling(TaxReturnRecordFilingRequest Request) : ITaxReturnCommand<TaxReturnRecordFilingResponse>
{
    public Guid? ReturnId => Request.ReturnId;

    public string? AuditReason => Request.FilingReference;

    public Task<Result<TaxReturnRecordFilingResponse>> ExecuteAsync(TaxReturnService service, CancellationToken cancellationToken) =>
        service.RecordFilingAsync(Request.ReturnId, Request.FilingReference, Request.FiledOn, cancellationToken);
}

internal sealed record RequestTaxReturnPayment(TaxReturnRequestPaymentRequest Request) : ITaxReturnCommand<TaxReturnRequestPaymentResponse>
{
    public Guid? ReturnId => Request.ReturnId;

    public string? AuditReason => null;

    public Task<Result<TaxReturnRequestPaymentResponse>> ExecuteAsync(TaxReturnService service, CancellationToken cancellationToken) =>
        service.RequestPaymentAsync(Request.ReturnId, Request.PayeePartyId.Value, Request.PayeeAccountId, Request.RequestedValueDate, cancellationToken);
}

internal sealed class TaxReturnValidator<TCommand, TResult> : AbstractValidator<TCommand>
    where TCommand : ITaxReturnCommand<TResult>
{
    public TaxReturnValidator()
    {
        RuleFor(c => c.AuditReason).MaximumLength(1000);
    }
}

internal sealed class TaxReturnAuditor<TCommand, TResult> : ICommandAuditor<TCommand, TResult>
    where TCommand : ITaxReturnCommand<TResult>
{
    public CommandAuditFacts Describe(TCommand command, Result<TResult>? result) => new()
    {
        ObjectRef = new ObjectRef(ModuleCode.FIN, "TaxReturn", command.ReturnId?.ToString("D") ?? "none"),
        BusinessKeys = command.ReturnId is { } id ? BusinessKeys.Empty.With("returnId", id.ToString("D")) : BusinessKeys.Empty,
    };
}

internal sealed class TaxReturnHandler<TCommand, TResult>(TaxReturnService service, RequestContext context) : ICommandHandler<TCommand, TResult>
    where TCommand : ITaxReturnCommand<TResult>
{
    public Task<Result<TResult>> HandleAsync(TCommand command, CancellationToken cancellationToken)
    {
        // The reason of an adjustment, a decision or a filing goes to the audit trail with the command.
        context.Reason = string.IsNullOrWhiteSpace(command.AuditReason) ? null : command.AuditReason.Trim();
        return command.ExecuteAsync(service, cancellationToken);
    }
}

/// <summary>Registration of the tax return service, commands and the maker-checker approval type (called once from <c>FinanceModule</c>).</summary>
internal static class TaxReturnRegistration
{
    public static IServiceCollection AddTaxReturns(this IServiceCollection services)
    {
        services.AddScoped<TaxReturnService>();
        services.AddAuthorityType(TaxReturnApproval.Definition);
        services.AddOwnerDecidedApprovalType(TaxReturnApproval.Type);
        Register<RecalculateTaxReturn, TaxReturnRecalculateResponse>(services, "fin.TaxReturn.recalculate", dryRun: true);
        Register<AddTaxReturnAdjustment, TaxReturnAddAdjustmentResponse>(services, "fin.TaxReturn.addAdjustment");
        Register<GenerateTaxReturnFile, TaxReturnGenerateFileResponse>(services, "fin.TaxReturn.generateFile", dryRun: true);
        Register<TransitionTaxReturn, TaxReturnTransitionResponse>(services, "fin.TaxReturn.transition");
        Register<RecordTaxReturnFiling, TaxReturnRecordFilingResponse>(services, "fin.TaxReturn.recordFiling");
        Register<RequestTaxReturnPayment, TaxReturnRequestPaymentResponse>(services, "fin.TaxReturn.requestPayment");
        return services;
    }

    private static void Register<TCommand, TResult>(IServiceCollection services, string operation, bool dryRun = false)
        where TCommand : class, ITaxReturnCommand<TResult>
    {
        services.AddScoped<IValidator<TCommand>, TaxReturnValidator<TCommand, TResult>>();
        services.AddCommandAuditor<TCommand, TResult, TaxReturnAuditor<TCommand, TResult>>();
        services.AddCommand<TCommand, TResult, TaxReturnHandler<TCommand, TResult>>(CommandDescriptor.For(operation) with { SupportsDryRun = dryRun });
    }
}
