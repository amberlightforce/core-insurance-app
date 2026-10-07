using System.Security.Cryptography;
using System.Text;
using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Contracts.Events;
using CoreIns.Modules.Billing.Domain;
using CoreIns.Modules.Billing.Persistence;
using CoreIns.Modules.Compliance.Contracts;
using CoreIns.Modules.Compliance.Contracts.Api;
using CoreIns.Modules.Market.Contracts;
using CoreIns.Modules.Market.Contracts.Api;
using CoreIns.Modules.Product.Contracts;
using CoreIns.Modules.Product.Contracts.Api;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Contracts.Common;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.Platform.Numbering;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Billing.Services;

/// <summary>
/// Moves the charges of one policy term through the billing states (REQ-BIL-002, REQ-BIL-065…068, REQ-BIL-364):
/// <list type="number">
/// <item>Nothing happens until the term is attached to an account (<c>PolicyBound</c> → plan instance). Charge deltas
/// that arrive first wait as Received.</item>
/// <item>Each Received charge is validated against the term and the term's pinned PFC catalogue (REQ-BIL-065) and gets
/// its WRITTEN entry through the rule table (REQ-BIL-066), or is quarantined with a reason and no entry
/// (REQ-BIL-065, REQ-BIL-287).</item>
/// <item>A set (<c>set_id</c> = POL transaction) is scheduled only when all <c>set_size</c> members are written; a set
/// with a quarantined member is never billed partially (REQ-BIL-364). Under ANNUAL (D-SLC-10c) the whole set becomes
/// one invoice, billed and due on the billing date: BILLED entry (LA-01 → LA-02), and for IPT under liability point
/// DUE the IPT_DUE entry (LA-27 → LA-06, PRD-06 §4.13). One fiscal request per transaction goes to CMP
/// (trigger point TRANSACTION, REQ-BIL-096, D3).</item>
/// </list>
/// Every step is idempotent and commutative: the same events in any order, or replayed, give the same rows (D-ARC-26:
/// the handlers never depend on aggregate order, so a late dead-letter replay is harmless).
/// </summary>
internal sealed class TermBilling(
    BillingDbContext db,
    RequestContext context,
    LedgerWriter ledger,
    INumberingService numbering,
    IEventPublisher events,
    IClock clock,
    IServiceProvider services)
{
    /// <summary>Fiscal trigger point of the slice: one fiscal document per bound transaction (PRD-06 REQ-BIL-096, D3; Greek motor).</summary>
    public const string FiscalTriggerPoint = "TRANSACTION";

    /// <summary>Payment method recorded on ANNUAL invoices; PolicyBound carries no method (bank transfer or cashier receipt).</summary>
    public const string DefaultMethod = ReceiptCodes.BankTransfer;

    private string? _liabilityQualifier;
    private bool _liabilityResolved;

    /// <summary>Writes and schedules what the term's state allows; returns the number of invoices created.</summary>
    public async Task<int> ProgressAsync(PolicyTermId termId, CancellationToken cancellationToken)
    {
        await LockTermAsync(termId, cancellationToken).ConfigureAwait(false);
        var plan = await db.PlanInstances.SingleOrDefaultAsync(p => p.TermId == termId, cancellationToken).ConfigureAwait(false);
        if (plan is null)
        {
            return 0;
        }

        var account = await db.Accounts.SingleAsync(a => a.BillingAccountId == plan.BillingAccountId, cancellationToken).ConfigureAwait(false);
        var received = Codes.Of(ChargeStatus.Received);
        var pending = await db.Charges.Where(c => c.TermId == termId && c.Status == received)
            .OrderBy(c => c.SetId).ThenBy(c => c.SetIndex).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (pending.Count > 0)
        {
            var catalogue = await CatalogueAsync(plan, cancellationToken).ConfigureAwait(false);
            foreach (var charge in pending)
            {
                await WriteAsync(charge, plan, account, catalogue, cancellationToken).ConfigureAwait(false);
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return await ScheduleAsync(plan, account, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteAsync(ChargeRow charge, PlanInstanceRow plan, BillingAccountRow account, IReadOnlyDictionary<string, ChargeTypeListItem> catalogue, CancellationToken cancellationToken)
    {
        var currency = Currency.FromCode(charge.Currency);
        var amount = new Money(charge.Amount, currency);
        string? reason = null;
        catalogue.TryGetValue(charge.ChargeType, out var type);
        if (!string.Equals(charge.DeltaKind, "NET", StringComparison.Ordinal))
        {
            reason = "DELTA-KIND";
        }
        else if (!string.Equals(charge.Currency, account.Currency, StringComparison.Ordinal))
        {
            reason = "CURRENCY";
        }
        else if (!amount.IsRoundedToMinorUnits)
        {
            reason = "AMOUNT-NOT-ROUNDED";
        }
        else if (amount.IsNegative)
        {
            reason = "CREDIT-NOT-SUPPORTED";
        }
        else if (type is null)
        {
            reason = "CHARGE-TYPE-UNKNOWN";
        }
        else if (!string.Equals(Codes.Of(type.Category), charge.ChargeCategory, StringComparison.Ordinal))
        {
            reason = "CHARGE-CATEGORY-MISMATCH";
        }
        else if (charge.ValidFrom < plan.TermFrom || (charge.ValidTo ?? plan.TermTo) > plan.TermTo)
        {
            reason = "PERIOD-OUTSIDE-TERM";
        }

        if (reason is not null)
        {
            await QuarantineAsync(charge, reason, cancellationToken).ConfigureAwait(false);
            return;
        }

        var accrueOnly = type!.BillingTreatment == BillingTreatment.AccruedNotBilled;
        var entryType = accrueOnly ? EntryTypes.Accrued : EntryTypes.Written;
        var qualifier = RuleQualifiers.Any;
        if (charge.ChargeCategory == ChargeCategories.Tax)
        {
            qualifier = await LiabilityQualifierAsync(cancellationToken).ConfigureAwait(false) ?? string.Empty;
            if (qualifier.Length == 0)
            {
                await QuarantineAsync(charge, "IPT-LIABILITY-POINT-MISSING", cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        var rule = await ledger.RuleAsync(ledger.Key(entryType, charge.ChargeCategory, qualifier), cancellationToken).ConfigureAwait(false);
        if (rule.IsFailure)
        {
            await QuarantineAsync(charge, "NO-RULE", cancellationToken, rule.Error!.Detail).ConfigureAwait(false);
            return;
        }

        var posted = Posting.Amount(rule.Value, amount);
        if (posted.IsFailure)
        {
            await QuarantineAsync(charge, "NO-RULE", cancellationToken, posted.Error!.Detail).ConfigureAwait(false);
            return;
        }

        charge.FiscalCategoryKey = type.FiscalCategoryKey;
        charge.Status = Codes.Of(accrueOnly ? ChargeStatus.Accrued : ChargeStatus.Written);
        if (posted.Value.IsZero)
        {
            return;
        }

        charge.WrittenEntryId = ledger.Post(new EntrySpec(
            entryType,
            account.BillingAccountId,
            [new PostingLeg(rule.Value, posted.Value, ChargeDimensions(charge, plan))],
            Lineage(charge.PolicyId, charge.TermId, charge.TransactionId).With("chargeId", charge.ChargeId.Value.ToString()),
            "bil.Charge.intake"));
    }

    private async Task<int> ScheduleAsync(PlanInstanceRow plan, BillingAccountRow account, CancellationToken cancellationToken)
    {
        var charges = await db.Charges.Where(c => c.TermId == plan.TermId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var created = 0;
        foreach (var set in charges.GroupBy(c => c.SetId))
        {
            var members = set.OrderBy(c => c.SetIndex).ToList();
            var statuses = members.Select(m => Codes.Parse<ChargeStatus>(m.Status)).ToList();
            if (members.Count != members[0].SetSize || statuses.Any(s => s is ChargeStatus.Received or ChargeStatus.Scheduled))
            {
                continue; // incomplete (REQ-BIL-364), waiting for the term, or already scheduled
            }

            if (statuses.Contains(ChargeStatus.Quarantined))
            {
                await RaiseAsync(ExceptionKinds.SetBlocked, set.Key.ToString(), "QUARANTINED-MEMBER",
                    "A member of the delta set is quarantined; the set is never billed partially (REQ-BIL-364).", cancellationToken).ConfigureAwait(false);
                continue;
            }

            var billable = members.Where(m => m.Status == Codes.Of(ChargeStatus.Written) && m.Amount > 0m).ToList();
            if (billable.Count == 0)
            {
                continue;
            }

            await BillAsync(plan, account, billable, cancellationToken).ConfigureAwait(false);
            created++;
        }

        return created;
    }

    /// <summary>ANNUAL: one invoice for the transaction's charges, billed and due on the billing date.</summary>
    private async Task BillAsync(PlanInstanceRow plan, BillingAccountRow account, List<ChargeRow> charges, CancellationToken cancellationToken)
    {
        var today = ledger.Today;
        var now = clock.Now;
        var currency = Currency.FromCode(account.Currency);
        var transactionId = charges[0].TransactionId;
        var invoiceId = InvoiceId.New();
        var number = await numbering.NextAsync(new NumberRequest(NumberingSchemes.Invoice, today), cancellationToken).ConfigureAwait(false);
        var billed = InvoiceStateModel.Machine.FireOrThrow(InvoiceStateModel.Machine.Start(InvoiceState.Planned).Value, InvoiceTrigger.Bill);
        var due = InvoiceStateModel.Machine.FireOrThrow(billed, InvoiceTrigger.BecomeDue);
        var total = Money.Sum(charges.Select(c => new Money(c.Amount, currency)), currency);

        var invoice = new InvoiceRow
        {
            InvoiceId = invoiceId,
            LegalEntityId = account.LegalEntityId,
            Jurisdiction = account.Jurisdiction,
            BillingAccountId = account.BillingAccountId,
            InvoiceNumber = number.Value,
            Kind = "INVOICE",
            State = Codes.Of(due),
            PolicyId = plan.PolicyId,
            TermId = plan.TermId,
            TransactionId = transactionId,
            IssueDate = today,
            DueDate = today,
            Method = plan.Method,
            Total = total.Amount,
            Currency = currency.Code,
            FiscalStatus = Codes.Of(FiscalStatus.NotRequested),
            CreatedAt = now,
            CreatedBy = context.Actor.ToString(),
            RecordVersion = 1,
        };
        db.Invoices.Add(invoice);

        var items = new List<InvoiceItemRow>();
        var billedLegs = new List<PostingLeg>();
        var iptLegs = new List<PostingLeg>();
        var line = 0;
        foreach (var charge in charges.OrderBy(c => c.SetIndex))
        {
            var item = new InvoiceItemRow
            {
                InvoiceItemId = Guid.CreateVersion7(),
                InvoiceId = invoiceId,
                LegalEntityId = account.LegalEntityId,
                ChargeId = charge.ChargeId,
                TermId = charge.TermId,
                TransactionId = charge.TransactionId,
                ElementLocator = charge.ElementLocator,
                CoverageCode = charge.CoverageCode,
                ChargeType = charge.ChargeType,
                ChargeCategory = charge.ChargeCategory,
                FiscalCategoryKey = charge.FiscalCategoryKey,
                ValidFrom = charge.ValidFrom,
                ValidTo = charge.ValidTo,
                Amount = charge.Amount,
                Currency = charge.Currency,
                State = Codes.Of(InvoiceItemState.Open), // Billed, and Open at once: the ANNUAL item is due on billing
                LineNo = ++line,
            };
            items.Add(item);
            charge.Status = Codes.Of(ChargeStatus.Scheduled);
            var amount = new Money(charge.Amount, currency);
            var dimensions = ChargeDimensions(charge, plan) with { InvoiceId = invoiceId, InvoiceItemId = item.InvoiceItemId };

            var rule = await ledger.RuleAsync(ledger.Key(EntryTypes.Billed, charge.ChargeCategory, RuleQualifiers.Any), cancellationToken).ConfigureAwait(false);
            billedLegs.Add(new PostingLeg(Unwrap(rule), amount, dimensions));

            if (charge.ChargeCategory == ChargeCategories.Tax
                && await LiabilityQualifierAsync(cancellationToken).ConfigureAwait(false) == RuleQualifiers.IptLiabilityDue)
            {
                var due1 = await ledger.RuleAsync(ledger.Key(EntryTypes.IptDue, charge.ChargeCategory, RuleQualifiers.IptLiabilityDue), cancellationToken).ConfigureAwait(false);
                iptLegs.Add(new PostingLeg(Unwrap(due1), amount, dimensions));
            }
        }

        db.InvoiceItems.AddRange(items);
        var lineage = Lineage(plan.PolicyId, plan.TermId, transactionId).With("invoiceId", invoiceId.Value.ToString());

        events.Publish(new OutgoingEvent(
            EventDescriptor.From(ChargesScheduledV1.Descriptor), "BillingAccount", account.BillingAccountId.Value.ToString(),
            new ChargesScheduledV1
            {
                TermId = plan.TermId,
                TransactionId = transactionId,
                ChargeIds = [.. charges.Select(c => c.ChargeId)],
                Items = [.. items.Select(i => new ScheduledItem { ItemId = i.InvoiceItemId, Status = ScheduledItem.StatusValue.Billed })],
            },
            lineage.With("billingAccountId", account.BillingAccountId.Value.ToString())) { OccurredAt = now });

        ledger.Post(new EntrySpec(EntryTypes.Billed, account.BillingAccountId, billedLegs, lineage, "bil.Invoice.bill"));
        if (iptLegs.Count > 0)
        {
            ledger.Post(new EntrySpec(EntryTypes.IptDue, account.BillingAccountId, iptLegs, lineage, "bil.Invoice.bill"));
        }

        await RequestFiscalDocumentAsync(invoice, account, items, cancellationToken).ConfigureAwait(false);

        events.Publish(new OutgoingEvent(
            EventDescriptor.From(InvoiceIssuedV1.Descriptor), "BillingAccount", account.BillingAccountId.Value.ToString(),
            new InvoiceIssuedV1
            {
                InvoiceId = invoiceId,
                InvoiceNumber = InvoiceNumber.Parse(invoice.InvoiceNumber),
                Kind = InvoiceIssuedV1.KindValue.Invoice,
                TermIds = [plan.TermId],
                TotalsByCategory = [.. items.GroupBy(i => i.ChargeCategory).OrderBy(g => g.Key, StringComparer.Ordinal)
                    .Select(g => new CategoryTotal { Category = g.Key, Amount = Money.Sum(g.Select(i => new Money(i.Amount, currency)), currency) })],
                DueDate = invoice.DueDate,
                Method = invoice.Method,
                FiscalTriggerRef = invoice.FiscalDocumentId?.Value.ToString("D"),
            },
            lineage.With("billingAccountId", account.BillingAccountId.Value.ToString())) { OccurredAt = now });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One fiscal request per transaction (REQ-BIL-096) through CMP; never blocks billing (REQ-BIL-099): a failed request
    /// is recorded on the invoice and as an exception, and billing goes on. The MARK arrives with FiscalDocRegistered.
    /// </summary>
    private async Task RequestFiscalDocumentAsync(InvoiceRow invoice, BillingAccountRow account, List<InvoiceItemRow> items, CancellationToken cancellationToken)
    {
        invoice.FiscalTriggerPoint = FiscalTriggerPoint;
        var cmp = services.GetService<IComplianceFiscalDocumentService>();
        if (cmp is null || items.Any(i => i.FiscalCategoryKey is null))
        {
            invoice.FiscalStatus = Codes.Of(FiscalStatus.RequestFailed);
            await RaiseAsync(ExceptionKinds.FiscalRequestFailed, invoice.InvoiceId.Value.ToString(), cmp is null ? "CMP-UNAVAILABLE" : "FISCAL-CATEGORY-MISSING",
                "The fiscal request could not be sent; billing continues (REQ-BIL-099).", cancellationToken).ConfigureAwait(false);
            return;
        }

        var currency = Currency.FromCode(invoice.Currency);
        var request = new FiscalDocumentRequestRequest
        {
            SourceType = FiscalTriggerPoint,
            SourceId = invoice.TransactionId.Value.ToString("D"),
            Role = FiscalDocumentRequestRequest.RoleValue.Issue,
            Revision = 0,
            Lines = [.. items.Select(i => new FiscalDocumentRequestRequest.LineItem
            {
                FiscalCategoryKey = i.FiscalCategoryKey!, Amount = new Money(i.Amount, currency), ChargeId = i.ChargeId,
            })],
            CounterpartyPartyId = account.PayerPartyId,
            IssueDate = invoice.IssueDate,
        };
        try
        {
            var response = await cmp.RequestAsync(request, new CommandOptions(DerivedKey($"bil.fiscal:{request.SourceType}:{request.SourceId}")), cancellationToken)
                .ConfigureAwait(false);
            invoice.FiscalDocumentId = response.FiscalDocumentId;
            invoice.FiscalDocumentType = response.DocumentType;
            invoice.FiscalStatus = Codes.Of(response.Status == "REJECTED" ? FiscalStatus.Rejected : FiscalStatus.Pending);
        }
        catch (DomainException ex)
        {
            invoice.FiscalStatus = Codes.Of(FiscalStatus.RequestFailed);
            await RaiseAsync(ExceptionKinds.FiscalRequestFailed, invoice.InvoiceId.Value.ToString(), ex.Error.Code.Value,
                "CMP refused the fiscal request; billing continues (REQ-BIL-099).", cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The term's pinned charge-type catalogue (REQ-PFC-004) by code; a PFC failure propagates (the event is retried).</summary>
    private async Task<IReadOnlyDictionary<string, ChargeTypeListItem>> CatalogueAsync(PlanInstanceRow plan, CancellationToken cancellationToken)
    {
        var pfc = services.GetRequiredService<IProductChargeTypeService>();
        var result = new Dictionary<string, ChargeTypeListItem>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var page = await pfc.ListAsync(cursor, 200, Sha256Hash.Parse(plan.ArtefactHash), null, cancellationToken).ConfigureAwait(false);
            foreach (var item in page.Items)
            {
                result[item.Code] = item;
            }

            cursor = page.NextCursor;
        }
        while (cursor is not null);

        return result;
    }

    /// <summary>
    /// The rule qualifier of MKT key <c>tax.ipt.liability_point</c> (DUE / WRITTEN), resolved once per unit of work at MKT's
    /// current configuration state; null when the key is missing (fail closed, D-REG-01).
    /// </summary>
    private async Task<string?> LiabilityQualifierAsync(CancellationToken cancellationToken)
    {
        if (_liabilityResolved)
        {
            return _liabilityQualifier;
        }

        const string key = "tax.ipt.liability_point";
        var mkt = services.GetRequiredService<IMarketConfigurationService>();
        var resolved = await mkt.ResolveAsync(
            new ConfigurationResolveRequest
            {
                LegalEntity = (context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.")).Value,
                Jurisdiction = ledger.Jurisdiction.Value,
                Keys = [key],

                // Not pinned to the envelope's configuration hash: that hash is the producer's pinned state, which MKT's
                // resolver does not accept until configuration history exists (W1-MKT-01); the current state is used.
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var value = resolved.Values.FirstOrDefault(v => v.Key == key)?.Value;
        _liabilityQualifier = RuleQualifiers.ForLiabilityPoint(value is { ValueKind: System.Text.Json.JsonValueKind.String } text ? text.GetString() : null);
        _liabilityResolved = true;
        return _liabilityQualifier;
    }

    private async Task QuarantineAsync(ChargeRow charge, string reason, CancellationToken cancellationToken, string? detail = null)
    {
        charge.Status = Codes.Of(ChargeStatus.Quarantined);
        charge.QuarantineReason = reason;
        await RaiseAsync(ExceptionKinds.DeltaException, charge.ChargeId.Value.ToString(), reason,
            detail ?? $"Charge {charge.ChargeId.Value} ({charge.ChargeType}) is quarantined: {reason} (REQ-BIL-065).", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Serialises every unit of work that touches the term's charges (intake, attach, progress) with a transaction-scoped
    /// advisory lock on the term id. Under READ COMMITTED the statements after the lock see what the previous holder
    /// committed, so of two concurrent deliveries of a set's last members exactly one sees the set complete and bills it;
    /// none is stranded (review SL-BIL D2). Re-entrant within the transaction.
    /// </summary>
    public async Task LockTermAsync(PolicyTermId termId, CancellationToken cancellationToken)
    {
        var key = BitConverter.ToInt64(termId.Value.ToByteArray(), 8);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({key})", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Records an intake exception once (the activity of the PRD; WRK is not wired in the slice).</summary>
    public async Task RaiseAsync(string kind, string subject, string reasonCode, string detail, CancellationToken cancellationToken)
    {
        var legalEntity = ledger.LegalEntityId.Value;
        var now = clock.Now.ToUtcDateTime();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO bil.intake_exception (exception_id, legal_entity_id, kind, subject, reason_code, detail, source_event_id, raised_at)
            VALUES ({Guid.CreateVersion7()}, {legalEntity}, {kind}, {subject}, {reasonCode}, {detail}, {context.CausationId}, {now})
            ON CONFLICT (kind, subject, reason_code) DO NOTHING
            """,
            cancellationToken).ConfigureAwait(false);
    }

    private static LineDimensions ChargeDimensions(ChargeRow charge, PlanInstanceRow plan) => new()
    {
        BillingAccountId = plan.BillingAccountId,
        PolicyId = charge.PolicyId,
        PolicyTermId = charge.TermId,
        TransactionId = charge.TransactionId,
        ChargeId = charge.ChargeId,
        ChargeType = charge.ChargeType,
        ChargeCategory = charge.ChargeCategory,
        CoverageCode = charge.CoverageCode,
        ProductCode = plan.ProductCode,
        BillMode = plan.BillMode,
    };

    private static BusinessKeys Lineage(PolicyId policyId, PolicyTermId termId, PolicyTransactionId transactionId) =>
        BusinessKeys.Empty.With("policyId", policyId.Value.ToString()).With("policyTermId", termId.Value.ToString())
            .With("transactionId", transactionId.Value.ToString());

    private static LedgerRule Unwrap(CoreIns.SharedKernel.Results.Result<LedgerRule> rule) =>
        rule.IsSuccess ? rule.Value : throw new DomainException(rule.Error!);

    private static IdempotencyKey DerivedKey(string operation)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(operation));
        var bytes = hash.AsSpan(0, 16).ToArray();
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x40); // version 4 layout, deterministic
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return IdempotencyKey.From(new Guid(bytes));
    }
}

/// <summary>The default allocation waterfall inside an item set: taxes and levies before fees before premium (REQ-BIL-129).</summary>
internal static class Waterfall
{
    public static IOrderedEnumerable<T> Order<T>(IEnumerable<T> items, Func<T, string> category) =>
        items.OrderBy(i => category(i) switch
        {
            "TAX" => 0,
            "LEVY" => 1,
            "FEE" => 2,
            _ => 3,
        });
}
