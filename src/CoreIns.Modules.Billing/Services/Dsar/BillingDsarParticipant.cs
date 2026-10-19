using System.Text.Json;
using CoreIns.Modules.Billing.Persistence;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel.Dsar;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Billing.Services.Dsar;

/// <summary>
/// Billing participant in data-subject requests (SL6-DSAR-PART-B, D-SL6-07). Every record class this module holds is
/// regulated (INVOICE, LEDGER, PAYEE_ACCOUNT): export returns the subject's records as JSON; erase and restrict never destroy them, they
/// write one <c>dsar_restriction</c> row per (subject, class) with the legal basis and retentionEnd UNSET_F404 (F-404).
/// A class with no data for the subject is reported NOT_HELD; no class is skipped silently. Idempotent: a second call
/// finds the existing restriction row and reports the same outcome.
/// </summary>
internal sealed class BillingDsarParticipant(IServiceScopeFactory scopes, IClock clock) : IDsarParticipant
{
    private const string RetentionBasis = "GDPR Art 17(3)(b): legal obligation to retain; retention period pending F-404";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    /// <inheritdoc />
    public string Module => "BIL";

    /// <inheritdoc />
    public async Task<DsarModuleResult> ExportAsync(SubjectRef subject, DsarContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
        var held = await CollectAsync(db, subject.PartyId, ct).ConfigureAwait(false);
        var payload = JsonSerializer.Serialize(
            new { module = Module, partyId = subject.PartyId, records = held.ToDictionary(h => h.RecordClass, h => h.Rows) },
            Json);
        var outcomes = held
            .Select(h => h.Rows.Count == 0
                ? DsarRecordOutcome.NotHeld(h.RecordClass)
                : DsarRecordOutcome.Restricted(h.RecordClass, h.LegalBasis, h.Rows.Count) with { Detail = "exported" })
            .ToList();
        return new DsarModuleResult(Module, outcomes, payload);
    }

    /// <inheritdoc />
    public Task<DsarModuleResult> RestrictAsync(
        SubjectRef subject, string reason, string legalBasis, DateOnly? retentionEnd, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        return ApplyAsync(subject, reason, string.IsNullOrWhiteSpace(legalBasis) ? null : legalBasis, retentionEnd, "dsar", ct);
    }

    /// <inheritdoc />
    public Task<DsarModuleResult> EraseAsync(SubjectRef subject, DsarContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(context);

        // Every class here is restrict-only (ledger, invoice, claim file, policy/fiscal document); nothing is erasable.
        // TODO(hardening): erase non-regulated classes (none exist in this module today) through DsarRetentionPolicy.
        return ApplyAsync(subject, "ERASURE_REQUEST", null, null, context.ApprovedBy ?? context.RequestedBy, ct);
    }

    private async Task<DsarModuleResult> ApplyAsync(
        SubjectRef subject, string reason, string? legalBasis, DateOnly? retentionEnd, string actor, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
        var held = await CollectAsync(db, subject.PartyId, ct).ConfigureAwait(false);
        var existing = await db.Set<DsarRestrictionRow>().Where(r => r.SubjectPartyId == subject.PartyId).ToListAsync(ct).ConfigureAwait(false);
        var requestedEnd = retentionEnd?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? DsarRetention.UnsetF404;
        var outcomes = new List<DsarRecordOutcome>();
        foreach (var h in held)
        {
            if (h.Rows.Count == 0)
            {
                outcomes.Add(DsarRecordOutcome.NotHeld(h.RecordClass));
                continue;
            }

            var row = existing.FirstOrDefault(r => string.Equals(r.RecordClass, h.RecordClass, StringComparison.Ordinal));
            if (row is null)
            {
                row = new DsarRestrictionRow
                {
                    RestrictionId = Guid.NewGuid(),
                    SubjectPartyId = subject.PartyId,
                    RecordClass = h.RecordClass,
                    LegalBasis = legalBasis ?? h.LegalBasis,
                    RetentionEnd = requestedEnd,
                    Reason = reason,
                    RecordCount = h.Rows.Count,
                    RestrictedBy = actor,
                    RestrictedAt = clock.Now,
                };
                db.Set<DsarRestrictionRow>().Add(row);
            }
            else
            {
                // Idempotent: keep the original basis and end; only the record count follows new data of the same class.
                row.RecordCount = h.Rows.Count;
            }

            outcomes.Add(DsarRecordOutcome.Restricted(h.RecordClass, row.LegalBasis, h.Rows.Count, row.RetentionEnd));
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new DsarModuleResult(Module, outcomes);
    }

    private sealed record Held(string RecordClass, string LegalBasis, IReadOnlyList<object> Rows);

    private static async Task<List<Held>> CollectAsync(BillingDbContext db, PartyId p, CancellationToken ct)
    {
        var accounts = await db.Accounts.AsNoTracking().Where(a => a.PayerPartyId == p).ToListAsync(ct).ConfigureAwait(false);
        var accountIds = accounts.Select(a => a.BillingAccountId).ToList();
        var invoices = await db.Invoices.AsNoTracking().Where(i => accountIds.Contains(i.BillingAccountId)).ToListAsync(ct).ConfigureAwait(false);
        var receipts = await db.Receipts.AsNoTracking().Where(r => accountIds.Contains(r.BillingAccountId)).ToListAsync(ct).ConfigureAwait(false);
        var entries = await db.LedgerEntries.AsNoTracking().Where(e => e.BillingAccountId != null && accountIds.Contains(e.BillingAccountId.Value)).ToListAsync(ct).ConfigureAwait(false);
        var refunds = await db.Refunds.AsNoTracking().Where(r => r.PayeePartyId == p).ToListAsync(ct).ConfigureAwait(false);
        var disbursements = await db.Disbursements.AsNoTracking().Where(d => d.PayeePartyId == p).ToListAsync(ct).ConfigureAwait(false);
        var receivables = await db.Receivables.AsNoTracking().Where(r => r.CounterpartyPartyId == p).ToListAsync(ct).ConfigureAwait(false);
        var payees = await db.PayeeAccounts.AsNoTracking().Where(a => a.PartyId == p).ToListAsync(ct).ConfigureAwait(false);

        var ledger = new List<object>();
        ledger.AddRange(accounts.Select(a => new { kind = "billingAccount", id = a.BillingAccountId, number = a.AccountNumber, a.Currency, a.Status, createdAt = a.CreatedAt }));
        ledger.AddRange(receipts.Select(r => new { kind = "receipt", id = r.ReceiptId, number = r.ReceiptNumber, r.Amount, r.Currency, valueDate = r.ValueDate }));
        ledger.AddRange(entries.Select(e => new { kind = "ledgerEntry", id = e.EntryId, e.EntryType, accountingDate = e.AccountingDate }));
        ledger.AddRange(refunds.Select(r => new { kind = "refund", id = r.RefundId, r.State, r.Amount, r.Currency, proposedAt = r.ProposedAt }));
        ledger.AddRange(disbursements.Select(d => new { kind = "disbursement", id = d.DisbursementId, number = d.DisbursementNumber, d.Amount, d.Currency, d.State }));
        ledger.AddRange(receivables.Select(r => new { kind = "receivable", id = r.ReceivableId, r.Purpose, r.Amount, r.Currency, dueDate = r.DueDate }));

        return
        [
            new Held(
                DsarRetentionPolicy.Invoice,
                RetentionBasis,
                invoices.Select(i => (object)new { id = i.InvoiceId, number = i.InvoiceNumber, i.Kind, i.State, issueDate = i.IssueDate, dueDate = i.DueDate, i.Total, i.Currency, fiscalDocumentId = i.FiscalDocumentId }).ToList()),
            new Held(DsarRetentionPolicy.Ledger, RetentionBasis, ledger),
            new Held(
                "PAYEE_ACCOUNT",
                RetentionBasis,
                payees.Select(a => (object)new { id = a.PayeeAccountId, a.Purpose, maskedIban = "****" + a.IbanLast4, a.HolderName, a.Status, validFrom = a.ValidFrom, validTo = a.ValidTo }).ToList()),
        ];
    }
}
