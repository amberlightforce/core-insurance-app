using System.Text.Json;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Billing.Persistence;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Billing.Queries;

/// <summary>
/// Reads delinquency cases for the API and the in-process contract. The contract gives the output as "process", so the view is a
/// JSON object: id, account, policy, term, state, trigger, overdue amount, notice document, notification date, clock instance,
/// deadline, promise date, hold reason, outcome and the recorded contacts. No personal data other than ids.
/// </summary>
internal sealed class DelinquencyReader(BillingDbContext db)
{
    public async Task<JsonElement?> GetAsync(LegalEntityId legalEntity, Guid id, CancellationToken cancellationToken)
    {
        var row = await db.DelinquencyCases.AsNoTracking().SingleOrDefaultAsync(c => c.CaseId == id && c.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        return row is null ? null : await ViewAsync(row, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DelinquencyListPage> ListAsync(
        LegalEntityId legalEntity, BillingAccountId? account, string? state, string? cursor, int? limit, CancellationToken cancellationToken,
        Guid? policyId = null, string? trigger = null)
    {
        var size = Math.Clamp(limit ?? 50, 1, 200);
        var query = db.DelinquencyCases.AsNoTracking().Where(c => c.LegalEntityId == legalEntity);
        if (account is { } a)
        {
            query = query.Where(c => c.BillingAccountId == a);
        }

        if (policyId is { } p)
        {
            query = query.Where(c => c.PolicyId == new PolicyId(p));
        }

        if (!string.IsNullOrEmpty(trigger))
        {
            query = query.Where(c => c.Trigger == trigger);
        }

        if (!string.IsNullOrEmpty(state))
        {
            query = query.Where(c => c.State == state);
        }

        // Case ids are UUIDv7: ordering by id is creation order and a stable cursor.
        var all = (await query.ToListAsync(cancellationToken).ConfigureAwait(false)).OrderBy(c => c.CaseId).ToList();
        if (cursor is not null && Guid.TryParse(cursor, out var after))
        {
            all = [.. all.Where(c => c.CaseId.CompareTo(after) > 0)];
        }

        var page = all.Take(size + 1).ToList();
        var shown = page.Take(size).ToList();
        var items = new List<DelinquencyListItem>();
        foreach (var row in shown)
        {
            items.Add(new DelinquencyListItem { Process = await ViewAsync(row, cancellationToken, withContacts: false).ConfigureAwait(false) });
        }

        return new DelinquencyListPage { Items = items, NextCursor = page.Count > size ? shown[^1].CaseId.ToString("D") : null, Limit = size };
    }

    public async Task<JsonElement> ViewAsync(DelinquencyCaseRow row, CancellationToken cancellationToken, bool withContacts = true)
    {
        var contacts = withContacts
            ? (await db.DelinquencyContacts.AsNoTracking().Where(c => c.CaseId == row.CaseId).ToListAsync(cancellationToken).ConfigureAwait(false))
                .OrderBy(c => c.ContactId)
                .Select(c => new { contactId = c.ContactId, channel = c.Channel, outcome = c.Outcome, note = c.Note, recordedBy = c.RecordedBy, recordedAt = c.RecordedAt })
                .ToList()
            : null;
        return JsonSerializer.SerializeToElement(new
        {
            delinquencyId = row.CaseId,
            billingAccountId = row.BillingAccountId,
            policyId = row.PolicyId,
            termId = row.TermId,
            state = row.State,
            trigger = row.Trigger,
            overdueAmount = new Money(row.OverdueAmount, Currency.FromCode(row.Currency)),
            noticeDocumentId = row.NoticeDocumentId,
            notificationDate = row.NotificationDate,
            clockInstanceId = row.ClockInstanceId,
            deadline = row.Deadline,
            promiseDate = row.PromiseDate,
            holdReason = row.HoldReason,
            outcome = row.Outcome,
            cancellationRequestId = row.CancellationRequestId,
            openedAt = row.OpenedAt,
            resolvedAt = row.ResolvedAt,
            contacts,
        });
    }
}
