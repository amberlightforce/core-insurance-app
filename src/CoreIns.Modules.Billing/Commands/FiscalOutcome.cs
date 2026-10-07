using CoreIns.Modules.Billing.Domain;
using CoreIns.Modules.Billing.Persistence;
using CoreIns.Modules.Billing.Services;
using CoreIns.Modules.Compliance.Contracts;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Billing.Commands;

/// <summary>
/// <c>bil.Invoice.recordFiscal</c> (internal, from <c>cmp.FiscalDocRegistered</c> / <c>cmp.FiscalDocRejected</c>):
/// stores CMP's series, number, MARK and UID on the invoice for display (REQ-BIL-086, REQ-BIL-098), or flags the
/// rejection with its codes and an exception <c>CMP-FISCAL-REJECTED</c>. Invoices are never held for a MARK
/// (REQ-BIL-099). Idempotent: a registered invoice stays registered with the same identifiers.
/// </summary>
/// <param name="FiscalDocumentId">The document (envelope business key).</param>
/// <param name="Registered">True for FiscalDocRegistered, false for FiscalDocRejected.</param>
/// <param name="Mark">MARK from the event.</param>
/// <param name="Uid">UID from the event.</param>
/// <param name="RejectionCodes">Rejection codes.</param>
internal sealed record RecordFiscalOutcome(FiscalDocumentId FiscalDocumentId, bool Registered, string? Mark, string? Uid, IReadOnlyList<string> RejectionCodes)
    : ICommand<int>;

internal sealed class RecordFiscalOutcomeHandler(BillingDbContext db, IClock clock, TermBilling billing, IServiceProvider services)
    : ICommandHandler<RecordFiscalOutcome, int>
{
    public async Task<Result<int>> HandleAsync(RecordFiscalOutcome command, CancellationToken cancellationToken)
    {
        var invoices = await db.Invoices.Where(i => i.FiscalDocumentId == command.FiscalDocumentId).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (invoices.Count == 0)
        {
            return 0; // not a BIL-sourced document
        }

        string? series = null, number = null;
        if (command.Registered && services.GetService<IComplianceFiscalDocumentService>() is { } cmp)
        {
            try
            {
                var document = await cmp.GetAsync(command.FiscalDocumentId.Value.ToString("D"), cancellationToken).ConfigureAwait(false);
                series = document.Identifiers.Series;
                number = document.Identifiers.Number;
            }
            catch (DomainException)
            {
                // Series and number are display data; the MARK from the event is stored regardless.
            }
        }

        var registered = Codes.Of(FiscalStatus.Registered);
        var changed = 0;
        foreach (var invoice in invoices)
        {
            if (invoice.FiscalStatus == registered)
            {
                continue;
            }

            if (command.Registered)
            {
                invoice.FiscalStatus = registered;
                invoice.FiscalMark = command.Mark;
                invoice.FiscalUid = command.Uid;
                invoice.FiscalSeries = series;
                invoice.FiscalNumber = number;
                invoice.FiscalRejectionCodes = [];
            }
            else
            {
                invoice.FiscalStatus = Codes.Of(FiscalStatus.Rejected);
                invoice.FiscalRejectionCodes = [.. command.RejectionCodes];
                await billing.RaiseAsync(ExceptionKinds.FiscalRejected, invoice.InvoiceId.Value.ToString(), "REJECTED",
                    $"CMP rejected fiscal document {command.FiscalDocumentId.Value}: {string.Join(", ", command.RejectionCodes)} (REQ-BIL-098).", cancellationToken)
                    .ConfigureAwait(false);
            }

            invoice.UpdatedAt = clock.Now;
            invoice.RecordVersion++;
            changed++;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return changed;
    }
}

internal sealed class RecordFiscalOutcomeAuditor : ICommandAuditor<RecordFiscalOutcome, int>
{
    public CommandAuditFacts Describe(RecordFiscalOutcome command, Result<int>? result) => new()
    {
        ObjectRef = ObjectRef.For(ModuleCode.CMP, "FiscalDocument", command.FiscalDocumentId),
        BusinessKeys = BusinessKeys.Empty.With("fiscalDocumentId", command.FiscalDocumentId.Value.ToString()),
        Changes = result is { IsSuccess: true } ok
            ? AuditDiff.Compute(null, new { registered = command.Registered, invoices = ok.Value, mark = command.Mark })
            : [],
    };
}
