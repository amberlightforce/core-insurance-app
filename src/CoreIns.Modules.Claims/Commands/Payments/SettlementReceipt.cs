using CoreIns.Modules.Claims.Domain;
using CoreIns.Modules.Claims.Persistence;
using CoreIns.Modules.Compliance.Contracts;
using CoreIns.Modules.Compliance.Contracts.Api;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Claims.Commands.Payments;

/// <summary>
/// The fiscal settlement receipt of an issued claim payment (REQ-CLM-138, REQ-CMP-001, D-SL4-11): one request to
/// <c>cmp.FiscalDocument.request</c> with source type CLM_CLAIM_PAYMENT, category CLAIM_SETTLEMENT_RECEIPT, role ISSUE and the claim
/// payment id as source (CMP's idempotency makes a replay return the same document). CMP decides whether a receipt is required
/// (Cyprus: NOT_REQUIRED); the MARK arrives later with <c>FiscalDocRegistered</c>. Never blocks the payment: a refused request is logged
/// and the payment goes on. A CLEARING payment has no receipt.
/// </summary>
internal sealed partial class SettlementReceipt(IServiceProvider services, IClock clock, IOptions<ClaimsOptions> options, ILogger<SettlementReceipt> logger)
{
    public const string SourceType = "CLM_CLAIM_PAYMENT";
    public const string Category = "CLAIM_SETTLEMENT_RECEIPT";

    public async Task RequestAsync(ClaimPaymentRow payment, IReadOnlyList<(string CostType, decimal Amount)> lines, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payment);
        ArgumentNullException.ThrowIfNull(lines);
        if (payment.Method == "CLEARING" || lines.Count == 0 || services.GetService(typeof(IComplianceFiscalDocumentService)) is not IComplianceFiscalDocumentService cmp)
        {
            return;
        }

        var request = new FiscalDocumentRequestRequest
        {
            SourceType = SourceType,
            SourceId = payment.ClaimPaymentId.Value.ToString("D"),
            Role = FiscalDocumentRequestRequest.RoleValue.Issue,
            Revision = 0,
            DocumentCategory = Category,
            CounterpartyPartyId = payment.PayeePartyId,
            IssueDate = options.Value.DateOf(clock.Now),
            Lines = [.. lines.GroupBy(l => l.CostType).Select(g => new FiscalDocumentRequestRequest.LineItem
            {
                FiscalCategoryKey = $"CLAIM_SETTLEMENT_{g.Key}", Amount = ClaimMoney.Of(g.Sum(l => l.Amount), payment.Currency),
            })],
        };
        try
        {
            await cmp.RequestAsync(request, new CommandOptions(PaymentSupport.Derived(null, payment.ClaimPaymentId, "fiscal-receipt")), cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException ex)
        {
            LogFailed(logger, payment.ClaimPaymentId.Value, ex.Error.Code.Value);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The fiscal settlement receipt of claim payment {ClaimPaymentId} was refused ({Code}); the payment goes on.")]
    private static partial void LogFailed(ILogger logger, Guid claimPaymentId, string code);
}

/// <summary>Stores the MARK of the settlement receipt on the payment (from <c>FiscalDocRegistered</c>).</summary>
internal sealed record RecordPaymentFiscalMark(ClaimPaymentId ClaimPaymentId, string Mark) : ICommand<string>;

internal sealed class RecordPaymentFiscalMarkHandler(ClaimsDbContext db, RequestContext context, ClaimProtection protection, IClock clock)
    : ICommandHandler<RecordPaymentFiscalMark, string>
{
    public async Task<Result<string>> HandleAsync(RecordPaymentFiscalMark command, CancellationToken cancellationToken)
    {
        var legalEntity = protection.Current(context);
        var payment = await db.ClaimPayments.SingleOrDefaultAsync(p => p.ClaimPaymentId == command.ClaimPaymentId && p.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (payment is null || payment.FiscalMark is not null)
        {
            return "IGNORED";
        }

        payment.FiscalMark = command.Mark;
        payment.UpdatedAt = clock.Now;
        payment.RecordVersion++;
        return "RECORDED";
    }
}

internal sealed class RecordPaymentFiscalMarkAuditor : ICommandAuditor<RecordPaymentFiscalMark, string>
{
    public CommandAuditFacts Describe(RecordPaymentFiscalMark command, Result<string>? result) => new()
    {
        ObjectRef = ObjectRef.For(ModuleCode.CLM, "ClaimPayment", command.ClaimPaymentId),
        BusinessKeys = BusinessKeys.Empty.With("paymentId", command.ClaimPaymentId.Value.ToString()),
        Changes = result is { IsSuccess: true } ok ? AuditDiff.Compute(null, new { fiscalMark = command.Mark, outcome = ok.Value }) : [],
    };
}
