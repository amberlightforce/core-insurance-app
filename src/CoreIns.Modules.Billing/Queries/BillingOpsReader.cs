using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Billing.Domain;
using CoreIns.Modules.Billing.Persistence;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Billing.Queries;

internal sealed record OpsPage<T>(IReadOnlyList<T> Items, string? NextCursor, int Limit);

/// <summary>A policy term's plan instance as the billing operations screens show it (NX-UI-14c).</summary>
internal sealed record PlanInstanceView(
    Guid PlanInstanceId, Guid TermId, Guid BillingAccountId, Guid PolicyId, string PolicyNumber, int TermNumber, string ProductCode,
    string PlanCode, string BillMode, string Method, string TermFrom, string TermTo, string? CancelledEffective);

/// <summary>
/// Read side for the billing operations screens (suspense workbench, payment plans): entity-scoped lists that
/// the contract only types loosely. Ids are UUIDv7, so ordering by id is creation order and a stable cursor.
/// </summary>
internal sealed class BillingOpsReader(BillingDbContext db)
{
    /// <summary>Plan instances (one per attached policy term), optionally for one account or a policy-number prefix.</summary>
    public async Task<OpsPage<PlanInstanceView>> PlanInstancesAsync(
        LegalEntityId legalEntity, Guid? billingAccountId, string? search, string? cursor, int? limit, CancellationToken cancellationToken)
    {
        var size = Math.Clamp(limit ?? 50, 1, 200);
        var query = db.PlanInstances.AsNoTracking().Where(p => p.LegalEntityId == legalEntity);
        if (billingAccountId is { } a)
        {
            var account = new BillingAccountId(a);
            query = query.Where(p => p.BillingAccountId == account);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(p => p.PolicyNumber.StartsWith(term));
        }

        if (Guid.TryParse(cursor, out var after))
        {
            query = query.Where(p => p.PlanInstanceId.CompareTo(after) > 0);
        }

        var rows = await query.OrderBy(p => p.PlanInstanceId).Take(size + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        var page = rows.Take(size).ToList();
        return new OpsPage<PlanInstanceView>(
            [.. page.Select(p => new PlanInstanceView(
                p.PlanInstanceId, p.TermId.Value, p.BillingAccountId.Value, p.PolicyId.Value, p.PolicyNumber, p.TermNumber, p.ProductCode,
                p.PlanCode, p.BillMode, p.Method, p.TermFrom.ToString(), p.TermTo.ToString(), p.CancelledEffective?.ToString()))],
            rows.Count > size ? page[^1].PlanInstanceId.ToString("D") : null, size);
    }
}
