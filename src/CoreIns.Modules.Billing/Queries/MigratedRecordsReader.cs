using CoreIns.Modules.Billing.Persistence;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Billing.Queries;

/// <summary>A page of migrated records; <c>NextCursor</c> is the id after which the next page starts.</summary>
internal sealed record MigratedPage<T>(IReadOnlyList<T> Items, string? NextCursor, int Limit);

internal sealed record MigratedReceivableView(
    Guid ReceivableId, Guid BatchId, string LegacyInvoiceNumber, Guid BillingAccountId, string? PolicyLegacyNumber, decimal OriginalAmount,
    decimal OutstandingAmount, decimal CollectedAmount, string Currency, DateOnly DueDate, string Origin);

internal sealed record MigratedMandateView(
    Guid MandateId, Guid BatchId, Guid BillingAccountId, string MandateReference, DateOnly SignatureDate, string? IbanMasked, string Status, string Origin);

/// <summary>
/// Read side of the records the migration imported (origin=MIGRATION): opening receivables and SEPA mandates, for list and search by
/// batch, billing account and a legacy-reference prefix. Entity-scoped; no payer personal data is returned (ids and masked IBAN only).
/// </summary>
internal sealed class MigratedRecordsReader(BillingDbContext db)
{
    public async Task<MigratedPage<MigratedReceivableView>> ReceivablesAsync(
        LegalEntityId legalEntity, Guid? batchId, Guid? billingAccountId, string? search, string? cursor, int? limit, CancellationToken cancellationToken)
    {
        var size = Math.Clamp(limit ?? 50, 1, 200);
        var query = db.MigrationReceivables.AsNoTracking().Where(r => r.LegalEntityId == legalEntity);
        if (batchId is { } b)
        {
            query = query.Where(r => r.BatchId == b);
        }

        if (billingAccountId is { } a)
        {
            var account = new BillingAccountId(a);
            query = query.Where(r => r.BillingAccountId == account);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(r => r.LegacyInvoiceNumber.StartsWith(term) || (r.PolicyLegacyNumber != null && r.PolicyLegacyNumber.StartsWith(term)));
        }

        if (Guid.TryParse(cursor, out var after))
        {
            query = query.Where(r => r.ReceivableId.CompareTo(after) > 0);
        }

        var rows = await query.OrderBy(r => r.ReceivableId).Take(size + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        var page = rows.Take(size).ToList();
        return new MigratedPage<MigratedReceivableView>(
            [.. page.Select(r => new MigratedReceivableView(
                r.ReceivableId, r.BatchId, r.LegacyInvoiceNumber, r.BillingAccountId.Value, r.PolicyLegacyNumber, r.OriginalAmount, r.OutstandingAmount,
                r.CollectedAmount, r.Currency, r.DueDate, r.Origin))],
            rows.Count > size ? page[^1].ReceivableId.ToString("D") : null, size);
    }

    public async Task<MigratedPage<MigratedMandateView>> MandatesAsync(
        LegalEntityId legalEntity, Guid? batchId, Guid? billingAccountId, string? search, string? cursor, int? limit, CancellationToken cancellationToken)
    {
        var size = Math.Clamp(limit ?? 50, 1, 200);
        var query = db.MigrationMandates.AsNoTracking().Where(m => m.LegalEntityId == legalEntity);
        if (batchId is { } b)
        {
            query = query.Where(m => m.BatchId == b);
        }

        if (billingAccountId is { } a)
        {
            var account = new BillingAccountId(a);
            query = query.Where(m => m.BillingAccountId == account);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(m => m.MandateReference.StartsWith(term));
        }

        if (Guid.TryParse(cursor, out var after))
        {
            query = query.Where(m => m.MandateId.CompareTo(after) > 0);
        }

        var rows = await query.OrderBy(m => m.MandateId).Take(size + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        var page = rows.Take(size).ToList();
        return new MigratedPage<MigratedMandateView>(
            [.. page.Select(m => new MigratedMandateView(m.MandateId, m.BatchId, m.BillingAccountId.Value, m.MandateReference, m.SignatureDate, m.IbanMasked, m.Status, m.Origin))],
            rows.Count > size ? page[^1].MandateId.ToString("D") : null, size);
    }
}
