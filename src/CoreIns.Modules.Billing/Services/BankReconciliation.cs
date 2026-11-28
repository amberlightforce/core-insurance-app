using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CoreIns.Modules.Billing.Commands;
using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Billing.Domain;
using CoreIns.Modules.Billing.Persistence;
using CoreIns.Platform.Context;
using CoreIns.Platform.Numbering;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Billing.Services;

/// <summary>
/// Matches the lines of an imported statement (BO-BIL-11, REQ-BIL-302..312). A credit is matched, in this order, to
/// (a) a receipt that carries its bank reference (equal amount: MATCHED; other amount: PARTIAL), then (b) the one invoice whose number the
/// remittance text names (the money is recorded on that account and allocated when it equals the open amount, else it waits in suspense
/// as PARTIAL), then (c) nothing identifiable: the money is recorded as unapplied cash on the legal entity's suspense account (SUSPENSE).
/// A debit is matched to the disbursement whose number it carries. Money is recorded through the ordinary <c>bil.Payment.take</c> logic,
/// so receipts, ledger entries and suspense items are those of every other intake path.
/// </summary>
internal sealed partial class BankReconciler(
    BillingDbContext db,
    TakePaymentHandler take,
    LedgerWriter ledger,
    INumberingService numbering,
    IClock clock,
    RequestContext context)
{
    /// <summary>The account type of the legal entity's holding account for unidentified bank credits.</summary>
    public const string SuspenseAccountType = "SUSPENSE";

    [GeneratedRegex("[A-Za-z0-9][A-Za-z0-9/_.-]*[A-Za-z0-9]|[A-Za-z0-9]", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TokenPattern();

    /// <summary>Words of the remittance text, at most 24 (a reference is never a paragraph).</summary>
    internal static IReadOnlyList<string> Tokens(string? text) =>
        string.IsNullOrWhiteSpace(text) ? [] : [.. TokenPattern().Matches(text).Select(m => m.Value).Distinct(StringComparer.Ordinal).Take(24)];

    /// <summary>Everything on a line that could be a reference: end-to-end id, whole text and its words.</summary>
    private static List<string> Candidates(ParsedStatementLine line)
    {
        var all = new List<string>();
        if (line.EndToEndId is not null)
        {
            all.Add(line.EndToEndId);
        }

        if (line.Reference is { Length: <= 140 } whole)
        {
            all.Add(whole);
        }

        all.AddRange(Tokens(line.Reference));
        return [.. all.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>Matches every line and stages the rows (the statement row must already be added). Saves while recording money.</summary>
    public async Task<List<BankStatementLineRow>> MatchAsync(BankStatementRow statement, ParsedStatement parsed, CancellationToken cancellationToken)
    {
        var legalEntity = statement.LegalEntityId;
        var claimedReceipts = new HashSet<PaymentId>();
        var claimedDisbursements = new HashSet<DisbursementId>();
        var rows = new List<BankStatementLineRow>(parsed.Lines.Count);
        foreach (var line in parsed.Lines)
        {
            var row = new BankStatementLineRow
            {
                LineId = Guid.CreateVersion7(),
                StatementId = statement.StatementId,
                LegalEntityId = legalEntity,
                LineNo = line.LineNo,
                Direction = line.Direction,
                BookingDate = line.BookingDate,
                ValueDate = line.ValueDate,
                Amount = line.Amount,
                Currency = parsed.Currency,
                Reference = line.Reference,
                EndToEndId = line.EndToEndId,
                Status = BankLineStates.Unmatched,
            };
            if (line.Direction == BankLineStates.Credit)
            {
                await MatchCreditAsync(statement, line, row, claimedReceipts, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await MatchDebitAsync(legalEntity, line, row, claimedDisbursements, cancellationToken).ConfigureAwait(false);
            }

            db.BankStatementLines.Add(row);
            rows.Add(row);
        }

        return rows;
    }

    private async Task MatchCreditAsync(
        BankStatementRow statement, ParsedStatementLine line, BankStatementLineRow row, HashSet<PaymentId> claimed, CancellationToken cancellationToken)
    {
        var legalEntity = statement.LegalEntityId;
        var candidates = Candidates(line);

        // (a) A receipt that already carries this bank reference (recorded by staff or by an earlier intake).
        if (candidates.Count > 0)
        {
            var receipt = await db.Receipts.AsNoTracking()
                .Where(r => r.LegalEntityId == legalEntity && (r.Method == ReceiptCodes.BankTransfer || r.Method == ReceiptCodes.DirectDebit) && r.BankReference != null && candidates.Contains(r.BankReference))
                .OrderBy(r => r.ReceiptNumber).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (receipt is not null)
            {
                row.ReceiptId = receipt.ReceiptId;
                var taken = claimed.Contains(receipt.ReceiptId)
                    || await db.BankStatementLines.AnyAsync(l => l.ReceiptId == receipt.ReceiptId, cancellationToken).ConfigureAwait(false);
                if (taken)
                {
                    row.Status = BankLineStates.Unmatched;
                    row.Note = $"Receipt {receipt.ReceiptNumber} was already reconciled by another statement line.";
                    row.ReceiptId = null;
                    return;
                }

                claimed.Add(receipt.ReceiptId);
                if (receipt.Amount == line.Amount && receipt.Currency == row.Currency)
                {
                    row.Status = BankLineStates.Matched;
                    row.MatchedAmount = line.Amount;
                }
                else
                {
                    row.Status = BankLineStates.Partial;
                    row.MatchedAmount = receipt.Currency == row.Currency ? Math.Min(receipt.Amount, line.Amount) : 0m;
                    row.Note = $"Receipt {receipt.ReceiptNumber} is {receipt.Amount:0.00} {receipt.Currency}; the statement line is {line.Amount:0.00} {row.Currency}.";
                }

                row.SuspenseItemId = await db.SuspenseItems.AsNoTracking().Where(i => i.ReceiptId == receipt.ReceiptId)
                    .Select(i => (Guid?)i.SuspenseItemId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        // (b) The one invoice the remittance text names.
        var tokens = Tokens(line.Reference);
        InvoiceRow? invoice = null;
        if (tokens.Count > 0)
        {
            var named = await db.Invoices.AsNoTracking()
                .Where(i => i.LegalEntityId == legalEntity && i.Kind == "INVOICE" && tokens.Contains(i.InvoiceNumber)).Take(2).ToListAsync(cancellationToken).ConfigureAwait(false);
            invoice = named.Count == 1 ? named[0] : null;
        }

        BillingAccountId accountId;
        if (invoice is not null)
        {
            accountId = invoice.BillingAccountId;
        }
        else
        {
            // (c) Nothing identifiable: the legal entity's suspense account holds the cash until a person decides.
            accountId = (await SuspenseAccountAsync(legalEntity, row.Currency, cancellationToken).ConfigureAwait(false)).BillingAccountId;
        }

        var bankReference = line.EndToEndId ?? (line.Reference is { Length: <= 140 } r ? r : null) ?? $"STMT-{statement.StatementRef}-{line.LineNo}";
        var request = new PaymentTakeRequest
        {
            BillingAccountId = accountId,
            Amount = new Money(line.Amount, Currency.FromCode(row.Currency)),
            Method = PaymentTakeRequest.MethodValue.BankTransfer,
            ValueDate = line.ValueDate ?? line.BookingDate,
            InvoiceId = invoice?.InvoiceId,
            BankReference = bankReference,
        };
        var taken2 = await take.HandleAsync(new TakePayment(request), cancellationToken).ConfigureAwait(false);
        if (taken2.IsFailure)
        {
            row.Status = BankLineStates.Unmatched;
            row.Note = $"The payment could not be recorded: {taken2.Error!.Code}.";
            return;
        }

        var response = taken2.Value;
        var receiptId = response.Receipt.ReceiptId;
        row.ReceiptId = receiptId;
        claimed.Add(receiptId);
        row.MatchedAmount = line.Amount;
        if (response.AllocationOutcome == PaymentTakeResponse.AllocationOutcomeValue.Allocated)
        {
            row.Status = BankLineStates.Matched;
            return;
        }

        row.SuspenseItemId = await db.SuspenseItems.AsNoTracking().Where(i => i.ReceiptId == receiptId)
            .Select(i => (Guid?)i.SuspenseItemId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (invoice is not null)
        {
            row.Status = BankLineStates.Partial;
            row.Note = $"The amount differs from the open amount of invoice {invoice.InvoiceNumber}; the cash waits in suspense on its account.";
        }
        else
        {
            row.Status = BankLineStates.Suspense;
            row.Note = "No receipt or invoice could be identified; the cash is unapplied in suspense.";
        }
    }

    private async Task MatchDebitAsync(LegalEntityId legalEntity, ParsedStatementLine line, BankStatementLineRow row, HashSet<DisbursementId> claimed, CancellationToken cancellationToken)
    {
        var candidates = Candidates(line);
        if (candidates.Count == 0)
        {
            row.Note = "The debit carries no reference.";
            return;
        }

        var disbursement = await db.Disbursements.AsNoTracking()
            .Where(d => d.LegalEntityId == legalEntity && candidates.Contains(d.DisbursementNumber)).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (disbursement is null)
        {
            row.Note = "No disbursement carries this reference.";
            return;
        }

        var taken = claimed.Contains(disbursement.DisbursementId)
            || await db.BankStatementLines.AnyAsync(l => l.DisbursementId == disbursement.DisbursementId, cancellationToken).ConfigureAwait(false);
        if (taken)
        {
            row.Note = $"Disbursement {disbursement.DisbursementNumber} was already reconciled by another statement line.";
            return;
        }

        var released = disbursement.State is "RELEASED" or "ISSUED" or "CLEARED";
        if (!released || disbursement.Amount != line.Amount || disbursement.Currency != row.Currency)
        {
            row.Note = released
                ? $"Disbursement {disbursement.DisbursementNumber} is {disbursement.Amount:0.00} {disbursement.Currency}; the statement line is {line.Amount:0.00} {row.Currency}."
                : $"Disbursement {disbursement.DisbursementNumber} is {disbursement.State}, not released to the bank.";
            return;
        }

        claimed.Add(disbursement.DisbursementId);
        row.DisbursementId = disbursement.DisbursementId;
        row.Status = BankLineStates.Matched;
        row.MatchedAmount = line.Amount;
    }

    /// <summary>The legal entity's holding account for unidentified bank credits in a currency; created on first use.</summary>
    private async Task<BillingAccountRow> SuspenseAccountAsync(LegalEntityId legalEntity, string currency, CancellationToken cancellationToken)
    {
        var existing = await db.Accounts.SingleOrDefaultAsync(
            a => a.LegalEntityId == legalEntity && a.AccountType == SuspenseAccountType && a.Currency == currency && a.Status == "ACTIVE", cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        // A fixed pseudo party per legal entity: the account has no real payer (REQ-BIL-033 needs one party for its uniqueness).
        var payer = new PartyId(new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("bil.bank-suspense:" + legalEntity.Value.ToString("D")))[..16]));
        var number = await numbering.NextAsync(new NumberRequest(NumberingSchemes.BillingAccount, ledger.Today), cancellationToken).ConfigureAwait(false);
        var account = new BillingAccountRow
        {
            BillingAccountId = BillingAccountId.New(),
            LegalEntityId = legalEntity,
            Jurisdiction = ledger.Jurisdiction.Value,
            AccountNumber = number.Value,
            AccountType = SuspenseAccountType,
            PayerPartyId = payer,
            Currency = currency,
            Status = "ACTIVE",
            CreatedAt = clock.Now,
            CreatedBy = context.Actor.ToString(),
            RecordVersion = 1,
        };
        db.Accounts.Add(account);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return account;
    }
}
