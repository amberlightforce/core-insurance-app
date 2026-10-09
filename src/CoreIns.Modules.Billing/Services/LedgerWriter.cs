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
/// <param name="BillingAccountId">Account the entry belongs to (event ordering key); null for a disbursement entry.</param>
/// <param name="Legs">Rule-matched legs (each gives a debit and a credit line).</param>
/// <param name="Lineage">Business lineage keys (REQ-BIL-284, D5); the entry id is added.</param>
/// <param name="Operation">Operation that caused the entry.</param>
/// <param name="DisbursementId">The disbursement of a disbursement entry (event ordering key when there is no account).</param>
internal sealed record EntrySpec(
    string EntryType, BillingAccountId? BillingAccountId, IReadOnlyList<PostingLeg> Legs, BusinessKeys Lineage, string Operation, DisbursementId? DisbursementId = null);

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
        var (aggregateType, aggregateId) = spec switch
        {
            { BillingAccountId: { } account } => ("BillingAccount", account.Value.ToString()),
            { DisbursementId: { } disbursement } => ("Disbursement", disbursement.Value.ToString()),
            _ => throw new InvalidOperationException($"A {spec.EntryType} entry needs a billing account or a disbursement."),
        };
        var keys = spec.Lineage.With("entryId", entryId.ToString());
        keys = spec.BillingAccountId is { } accountId ? keys.With("billingAccountId", accountId.Value.ToString()) : keys;
        keys = spec.DisbursementId is { } disbursementId ? keys.With("disbursementId", disbursementId.Value.ToString()) : keys;

        db.LedgerEntries.Add(new LedgerEntryRow
        {
            EntryId = entryId,
            LegalEntityId = legalEntityId,
            Jurisdiction = Jurisdiction.Value,
            BillingAccountId = spec.BillingAccountId,
            DisbursementId = spec.DisbursementId,
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
                DisbursementId = d.DisbursementId,
                SourceType = d.SourceType,
                SourceId = d.SourceId,
                ClaimId = d.ClaimId,
                RecoveryId = d.RecoveryId,
                StatementRef = d.StatementRef,
                CounterpartyPartyId = d.CounterpartyPartyId,
                TransactionKind = d.TransactionKind,
                CancellationSource = d.CancellationSource,
                TreatmentRuleId = d.TreatmentRuleId,
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
            EventDescriptor.From(BillingEntryPostedV1.Descriptor), aggregateType, aggregateId,
            new BillingEntryPostedV1 { EntryId = entryId, EventTypeValue = spec.EntryType, AccountingDate = today, BusinessDate = today, Lines = published },
            keys) { OccurredAt = now });
        return entryId;
    }

    /// <summary>The request's legal entity.</summary>
    /// <summary>The authenticated actor that records a receivable allocation.</summary>
    public string Actor => context.Actor.ToString();

    public LegalEntityId LegalEntityId => legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));

    /// <summary>The request's jurisdiction.</summary>
    public Jurisdiction Jurisdiction => context.Jurisdiction ?? throw new InvalidOperationException("The request context has no jurisdiction.");

    /// <summary>The line dimensions on the wire (typed, D-SLC-19a; a dimension that does not apply is null, REQ-BIL-283).</summary>
    private LedgerDimensions Dimensions(LegalEntityCode legalEntity, string ruleId, LineDimensions d) => new()
    {
        LegalEntity = legalEntity.Value,
        Jurisdiction = Jurisdiction.Value,
        RuleId = ruleId,
        BillingAccountId = d.BillingAccountId,
        PolicyId = d.PolicyId,
        PolicyTermId = d.PolicyTermId,
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
        DisbursementId = d.DisbursementId,
        SourceType = d.SourceType,
        SourceId = d.SourceId,
        ClaimId = d.ClaimId,
        RecoveryId = d.RecoveryId,
        StatementRef = d.StatementRef,
        CounterpartyPartyId = d.CounterpartyPartyId,
        TransactionKind = d.TransactionKind is null ? null : TransactionKinds.ToDimension(d.TransactionKind),
        CancellationSource = d.CancellationSource,
        TreatmentRuleId = d.TreatmentRuleId,
    };
}
