using CoreIns.Modules.Billing.Commands;
using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Billing.Queries;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Billing.Services;

/// <summary>Shared plumbing of the in-process contracts: commands run through the pipeline with the caller's options.</summary>
internal static class InProcess
{
    public static async Task<T> RunAsync<TCommand, T>(RequestContext context, ICommandHandler<TCommand, T> handler, TCommand command, CommandOptions options, CancellationToken cancellationToken)
        where TCommand : ICommand<T>
    {
        ArgumentNullException.ThrowIfNull(options);
        using (context.Use(options.IdempotencyKey, options.DryRun))
        {
            var result = await handler.HandleAsync(command, cancellationToken).ConfigureAwait(false);
            return result.IsSuccess ? result.Value : throw new DomainException(result.Error!);
        }
    }

    public static LegalEntityId LegalEntity(RequestContext context, ILegalEntityDirectory directory) =>
        directory.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));

    public static T Found<T>(T? value, string what)
        where T : class => value ?? throw new DomainException(BillingErrors.NotFound(what));

    public static DomainException NotAvailable(string operation) =>
        new(DomainError.Of(ModuleCode.BIL, "NOT-AVAILABLE", $"{operation} is not built yet (SL-BIL builds account get, invoice get/list, payment take and receipt get)."));
}

/// <summary><see cref="IBillingBillingAccountService"/>: <c>get</c> (REQ-BIL-001); attachTerm runs from PolicyBound, not as a call.</summary>
internal sealed class BillingAccountService(RequestContext context, ILegalEntityDirectory legalEntities, BillingReader reader) : IBillingBillingAccountService
{
    public async Task<BillingAccountGetResponse> GetAsync(string id, CancellationToken cancellationToken = default) =>
        InProcess.Found(
            Guid.TryParse(id, out var guid)
                ? await reader.AccountAsync(InProcess.LegalEntity(context, legalEntities), new BillingAccountId(guid), cancellationToken).ConfigureAwait(false)
                : null,
            "billing account");

    public Task<BillingAccountAttachTermResponse> AttachTermAsync(BillingAccountAttachTermRequest request, CommandOptions options, CancellationToken cancellationToken = default) =>
        throw InProcess.NotAvailable("bil.BillingAccount.attachTerm (as a call; terms attach from PolicyBound)");

    public Task<BillingAccountMoveTermResponse> MoveTermAsync(BillingAccountMoveTermRequest request, CommandOptions options, CancellationToken cancellationToken = default) =>
        throw InProcess.NotAvailable("bil.BillingAccount.moveTerm");
}

/// <summary><see cref="IBillingInvoiceService"/>: <c>get</c> and <c>list</c> (REQ-BIL-086…088).</summary>
internal sealed class InvoiceService(RequestContext context, ILegalEntityDirectory legalEntities, BillingReader reader) : IBillingInvoiceService
{
    public async Task<InvoiceGetResponse> GetAsync(string id, CancellationToken cancellationToken = default) =>
        InProcess.Found(
            Guid.TryParse(id, out var guid)
                ? await reader.InvoiceAsync(InProcess.LegalEntity(context, legalEntities), new InvoiceId(guid), cancellationToken).ConfigureAwait(false)
                : null,
            "invoice");

    public Task<InvoiceListPage> ListAsync(
        string? cursor = null, int? limit = null, BillingAccountId? billingAccountId = null, PolicyId? policyId = null, PolicyTermId? policyTermId = null,
        CancellationToken cancellationToken = default) =>
        reader.InvoicesAsync(InProcess.LegalEntity(context, legalEntities), billingAccountId, policyId, policyTermId, cursor, limit, cancellationToken);
}

/// <summary><see cref="IBillingPaymentService"/>: <c>take</c> (REQ-BIL-004).</summary>
internal sealed class PaymentService(RequestContext context, ICommandHandler<TakePayment, PaymentTakeResponse> take) : IBillingPaymentService
{
    public Task<PaymentTakeResponse> TakeAsync(PaymentTakeRequest request, CommandOptions options, CancellationToken cancellationToken = default) =>
        InProcess.RunAsync(context, take, new TakePayment(request), options, cancellationToken);
}

/// <summary><see cref="IBillingReceiptService"/>: <c>get</c> (REQ-BIL-126).</summary>
internal sealed class ReceiptService(RequestContext context, ILegalEntityDirectory legalEntities, BillingReader reader) : IBillingReceiptService
{
    public async Task<ReceiptGetResponse> GetAsync(string id, string? filters = null, CancellationToken cancellationToken = default) =>
        InProcess.Found(
            Guid.TryParse(id, out var guid)
                ? await reader.ReceiptAsync(InProcess.LegalEntity(context, legalEntities), new PaymentId(guid), cancellationToken).ConfigureAwait(false)
                : null,
            "receipt");
}
