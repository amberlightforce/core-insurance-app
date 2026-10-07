using System.Text.Json;
using System.Text.Json.Nodes;
using CoreIns.Modules.Billing.Contracts.Events;
using CoreIns.Modules.Billing.Domain;
using CoreIns.Modules.Billing.Persistence;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts.Common;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Billing.Services;

/// <summary>What to post: one sub-ledger entry of a billing account.</summary>
/// <param name="EntryType">Entry type (<see cref="EntryTypes"/>).</param>
/// <param name="BillingAccountId">Account the entry belongs to (event ordering key).</param>
/// <param name="Legs">Rule-matched legs (each gives a debit and a credit line).</param>
/// <param name="Lineage">Business lineage keys (REQ-BIL-284, D5); the entry id is added.</param>
/// <param name="Operation">Operation that caused the entry.</param>
internal sealed record EntrySpec(string EntryType, BillingAccountId BillingAccountId, IReadOnlyList<PostingLeg> Legs, BusinessKeys Lineage, string Operation);

/// <summary>
/// The only writer of the billing sub-ledger (REQ-BIL-300). It loads the billing-ledger rule table (REQ-BIL-286), turns
/// rule-matched legs into a balanced entry (REQ-BIL-279/280: header plus at least two lines; the database re-checks
/// the balance at commit), stores the dimensions on every line (REQ-BIL-283) and the lineage keys on the header
/// (REQ-BIL-284), and publishes one <c>BillingEntryPosted</c> per entry in the same transaction (REQ-BIL-313,
/// REQ-BIL-332). Entries are never updated: corrections are reversals (REQ-BIL-288).
/// </summary>
internal sealed class LedgerWriter(
    BillingDbContext db,
    RequestContext context,
    ILegalEntityDirectory legalEntities,
    IClock clock,
    IEventPublisher events,
    IOptions<BillingOptions> options)
{
    private IReadOnlyList<LedgerRule>? _rules;

    /// <summary>Today's business date in the legal entity's zone (the accounting date: no cut-off in the slice).</summary>
    public BusinessDate Today => clock.Now.ToBusinessDate(options.Value.Zone);

    /// <summary>The single rule for <paramref name="key"/> effective today (BIL-ERR-NO-RULE when none or ambiguous).</summary>
    public async Task<Result<LedgerRule>> RuleAsync(PostingKey key, CancellationToken cancellationToken)
    {
        if (_rules is null)
        {
            var today = Today;
            var rows = await db.LedgerRules.AsNoTracking()
                .Where(r => r.ValidFrom <= today && (r.ValidTo == null || r.ValidTo > today))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            _rules = [.. rows.Select(r => new LedgerRule(
                r.RuleId, r.Version, r.EventType, r.ChargeCategory, r.BillMode, r.Jurisdiction, r.Qualifier, r.DebitAccount, r.CreditAccount, r.AmountExpression))];
        }

        return Posting.Match(_rules, key);
    }

    /// <summary>The posting key of an entry type and charge category in the request's jurisdiction.</summary>
    public PostingKey Key(string entryType, string chargeCategory, string qualifier) =>
        new(entryType, chargeCategory, PaymentPlans.DirectBill, Jurisdiction.Value, qualifier);

    /// <summary>Stages the entry, its lines and its <c>BillingEntryPosted</c>; returns the entry id.</summary>
    public Guid Post(EntrySpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var lines = Posting.Lines(spec.Legs);
        if (lines.Count < 2 || !Posting.IsBalanced(lines))
        {
            throw new InvalidOperationException($"A {spec.EntryType} entry needs balanced lines (REQ-BIL-279, REQ-BIL-280).");
        }

        var now = clock.Now;
        var today = Today;
        var entryId = Guid.CreateVersion7();
        var legalEntityCode = context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.");
        var legalEntityId = LegalEntityId;
        var keys = spec.Lineage.With("entryId", entryId.ToString()).With("billingAccountId", spec.BillingAccountId.Value.ToString());

        db.LedgerEntries.Add(new LedgerEntryRow
        {
            EntryId = entryId,
            LegalEntityId = legalEntityId,
            Jurisdiction = Jurisdiction.Value,
            BillingAccountId = spec.BillingAccountId,
            EntryType = spec.EntryType,
            AccountingDate = today,
            BusinessDate = today,
            RecordedAt = now,
            CauseEventId = context.CausationId,
            CauseOperation = spec.Operation,
            CorrelationId = context.CorrelationId.Value,
            LineageKeys = JsonSerializer.Serialize(keys, CoreIns.SharedKernel.Json.SharedKernelJson.Options),
        });

        var published = new List<LedgerLine>();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var d = line.Dimensions;
            db.LedgerLines.Add(new LedgerLineRow
            {
                LineId = Guid.CreateVersion7(),
                EntryId = entryId,
                LineNo = i + 1,
                AccountCode = line.Account,
                Side = Codes.Of(line.Side),
                Amount = line.Amount.Amount,
                Currency = line.Amount.Currency.Code,
                RuleId = line.RuleId,
                LegalEntityId = legalEntityId,
                BillingAccountId = d.BillingAccountId,
                PolicyId = d.PolicyId,
                TermId = d.PolicyTermId,
                TransactionId = d.TransactionId,
                ChargeId = d.ChargeId,
                ChargeType = d.ChargeType,
                ChargeCategory = d.ChargeCategory,
                CoverageCode = d.CoverageCode,
                ProductCode = d.ProductCode,
                BillMode = d.BillMode,
                InvoiceId = d.InvoiceId,
                InvoiceItemId = d.InvoiceItemId,
                ReceiptId = d.ReceiptId,
                AllocationId = d.AllocationId,
            });
            published.Add(new LedgerLine
            {
                Account = line.Account,
                Side = line.Side == LedgerSide.Debit ? LedgerLine.SideValue.Debit : LedgerLine.SideValue.Credit,
                Amount = line.Amount,
                Dimensions = Dimensions(legalEntityCode, line.RuleId, d),
            });
        }

        events.Publish(new OutgoingEvent(
            EventDescriptor.From(BillingEntryPostedV1.Descriptor), "BillingAccount", spec.BillingAccountId.Value.ToString(),
            new BillingEntryPostedV1 { EntryId = entryId, EventTypeValue = spec.EntryType, AccountingDate = today, BusinessDate = today, Lines = published },
            keys) { OccurredAt = now });
        return entryId;
    }

    /// <summary>The request's legal entity.</summary>
    public LegalEntityId LegalEntityId => legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));

    /// <summary>The request's jurisdiction.</summary>
    public Jurisdiction Jurisdiction => context.Jurisdiction ?? throw new InvalidOperationException("The request context has no jurisdiction.");

    /// <summary>The line dimensions on the wire (camelCase; a dimension that does not apply is null, REQ-BIL-283).</summary>
    private JsonElement Dimensions(LegalEntityCode legalEntity, string ruleId, LineDimensions d)
    {
        var json = new JsonObject
        {
            ["legalEntity"] = legalEntity.Value,
            ["jurisdiction"] = Jurisdiction.Value,
            ["billingAccountId"] = d.BillingAccountId?.Value.ToString("D"),
            ["policyId"] = d.PolicyId?.Value.ToString("D"),
            ["policyTermId"] = d.PolicyTermId?.Value.ToString("D"),
            ["transactionId"] = d.TransactionId?.Value.ToString("D"),
            ["chargeId"] = d.ChargeId?.Value.ToString("D"),
            ["chargeType"] = d.ChargeType,
            ["chargeCategory"] = d.ChargeCategory,
            ["coverageCode"] = d.CoverageCode,
            ["productCode"] = d.ProductCode,
            ["billMode"] = d.BillMode,
            ["invoiceId"] = d.InvoiceId?.Value.ToString("D"),
            ["invoiceItemId"] = d.InvoiceItemId?.ToString("D"),
            ["receiptId"] = d.ReceiptId?.Value.ToString("D"),
            ["allocationId"] = d.AllocationId?.ToString("D"),
            ["ruleId"] = ruleId,
        };
        return JsonSerializer.SerializeToElement(json);
    }
}
