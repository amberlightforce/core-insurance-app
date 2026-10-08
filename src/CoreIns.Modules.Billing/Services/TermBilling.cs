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
using CoreIns.Modules.Market.Contracts.Spi;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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
    IServiceProvider services,
    Allocator allocator,
    IOptions<BillingOptions> options)
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

        // A credit waits until the invoice it credits is billed (possibly by another set of this very pass): schedule again
        // while a pass creates documents.
        var total = 0;
        for (var pass = 0; pass < 5; pass++)
        {
            var made = await ScheduleAsync(plan, account, cancellationToken).ConfigureAwait(false);
            total += made;
            if (made == 0)
            {
                break;
            }
        }

        return total;
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

        reason ??= ServicingReason(charge, plan);
        if (reason is not null)
        {
            await QuarantineAsync(charge, reason, cancellationToken).ConfigureAwait(false);
            return;
        }

        // REQ-BIL-079: every tax and levy delta must follow TaxCalculator.treatment (fail closed).
        if (charge.TransactionKind is not null && charge.ChargeCategory is ChargeCategories.Tax or ChargeCategories.Levy)
        {
            var mismatch = await CheckTreatmentAsync(charge, cancellationToken).ConfigureAwait(false);
            if (mismatch is not null)
            {
                await QuarantineAsync(charge, mismatch.Value.Reason, cancellationToken, mismatch.Value.Detail).ConfigureAwait(false);
                return;
            }
        }

        if (amount.IsNegative)
        {
            if (charge.ChargeCategory is ChargeCategories.Tax or ChargeCategories.Levy)
            {
                // The treatment allows it (REDUCE / VOID), but the slice has no tested mechanics for reducing IPT or levy payable
                // (D-SL3-05/06): fail closed. KEEP_NOT_REDUCED credits are zero and never reach this point (INV-05).
                await QuarantineAsync(charge, QuarantineReasons.TaxCreditNotSupported, cancellationToken).ConfigureAwait(false);
                return;
            }

            // A credit posts nothing yet: CREDIT_WRITTEN and CREDIT_BILLED are posted together, with the credit note, when
            // its set is complete and the invoice it credits is known (ScheduleAsync). Only its ledger rule is checked here.
            var creditRule = await ledger.RuleAsync(ledger.Key(EntryTypes.CreditWritten, charge.ChargeCategory, RuleQualifiers.Any), cancellationToken).ConfigureAwait(false);
            if (creditRule.IsFailure)
            {
                await QuarantineAsync(charge, "NO-RULE", cancellationToken, creditRule.Error!.Detail).ConfigureAwait(false);
                return;
            }

            charge.FiscalCategoryKey = type!.FiscalCategoryKey;
            charge.Status = Codes.Of(ChargeStatus.Written);
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

            var written = Codes.Of(ChargeStatus.Written);
            var billable = members.Where(m => m.Status == written && m.Amount > 0m).ToList();
            var credits = members.Where(m => m.Status == written && m.Amount < 0m).ToList();
            if (billable.Count == 0 && credits.Count == 0)
            {
                continue;
            }

            IReadOnlyList<CreditAllocation> allocations = [];
            if (credits.Count > 0)
            {
                var resolution = await ResolveCreditsAsync(credits, cancellationToken).ConfigureAwait(false);
                if (resolution.Waiting)
                {
                    continue; // the invoice to credit is not billed yet (REQ-BIL-073); the set waits, nothing is posted
                }

                if (resolution.Failed is { } failed)
                {
                    await QuarantineAsync(failed.Charge, QuarantineReasons.CreditExceedsBilled, cancellationToken, failed.Detail).ConfigureAwait(false);
                    await RaiseAsync(ExceptionKinds.SetBlocked, set.Key.ToString(), "QUARANTINED-MEMBER",
                        "A credit exceeds what was billed on its element and charge type; the set is never billed partially (REQ-BIL-364).", cancellationToken).ConfigureAwait(false);
                    continue;
                }

                allocations = resolution.Allocations;
            }

            if (billable.Count > 0)
            {
                await BillAsync(plan, account, billable, cancellationToken).ConfigureAwait(false);
                created++;
            }

            if (credits.Count > 0)
            {
                created += await BillCreditsAsync(plan, account, credits, allocations, cancellationToken).ConfigureAwait(false);
            }
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
                LegalStatus = charge.LegalStatus,
                Provisional = charge.Provisional,
                ValidFrom = charge.ValidFrom,
                ValidTo = charge.ValidTo,
                Amount = charge.Amount,
                Currency = charge.Currency,
                State = Codes.Of(InvoiceItemState.Open), // Billed, and Open at once: the ANNUAL item is due on billing
                LineNo = ++line,
                TransactionKind = charge.TransactionKind,
                CancellationSource = charge.CancellationSource,
                TreatmentRuleId = charge.TreatmentRuleId,
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

    /// <summary>
    /// The servicing checks of a delta that do not need MKT: the transaction kind (always set from slice 3; a negative
    /// delta without one is refused, a positive legacy delta is new business), the sign against the kind, the cancellation
    /// source of a CANCELLATION or VOID, and no new debit on a cancelled term. Null when the delta passes.
    /// </summary>
    private static string? ServicingReason(ChargeRow charge, PlanInstanceRow plan)
    {
        var kind = charge.TransactionKind;
        if (kind is null)
        {
            return charge.Amount < 0m ? QuarantineReasons.TransactionKindMissing : null;
        }

        if (!TransactionKinds.IsAccepted(kind))
        {
            return QuarantineReasons.TransactionKindUnsupported;
        }

        if (charge.Amount < 0m && !TransactionKinds.AllowsCredit(kind))
        {
            return QuarantineReasons.KindAmountMismatch;
        }

        if (TransactionKinds.NeedsCancellationSource(kind) && string.IsNullOrWhiteSpace(charge.CancellationSource))
        {
            return QuarantineReasons.CancellationSourceMissing;
        }

        if (plan.CancelledEffective is { } stopped && charge.Amount > 0m && !TransactionKinds.NeedsCancellationSource(kind) && charge.ValidFrom >= stopped)
        {
            return QuarantineReasons.TermCancelled;
        }

        return null;
    }

    /// <summary>
    /// REQ-BIL-079: asks <c>TaxCalculator.treatment</c> for the line and compares (see <see cref="TreatmentCheck"/>). A missing
    /// rule, an invalid request or a non-Settled rule in Production quarantines the set; a missing binding or an
    /// unavailable or slow service throws so the event is retried and nothing is stored (never a guess).
    /// </summary>
    private async Task<(string Reason, string Detail)?> CheckTreatmentAsync(ChargeRow charge, CancellationToken cancellationToken)
    {
        var calculator = services.GetService<ITaxCalculator>()
                         ?? throw new InvalidOperationException("TaxCalculator (MKT SPI 4) is not bound: tax and levy deltas cannot be validated (REQ-BIL-079); the event is retried.");
        var kind = TransactionKinds.ToTax(charge.TransactionKind!);
        var request = new TaxTreatmentRequest
        {
            LegalEntityId = ledger.LegalEntityId.Value,
            RiskJurisdiction = ledger.Jurisdiction.Value,
            TaxPointDate = charge.BookingDate.Value,
            ChargeType = charge.ChargeType,
            Category = charge.ChargeCategory == ChargeCategories.Levy ? TaxCategory.Levy : TaxCategory.Tax,
            ChargeOrigin = ChargeOrigin.Pol,
            TransactionKind = kind,
            CancellationSource = charge.CancellationSource,
            PolicyholderType = options.Value.TreatmentPolicyholderType,
            BusinessBasis = options.Value.TreatmentBusinessBasis,
        };

        TaxTreatmentResult result;
        try
        {
            result = await calculator.TreatmentAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (SpiException ex) when (ex.Error.Category == SpiErrorCategory.RuleMissing)
        {
            return (QuarantineReasons.TreatmentRuleMissing, $"No treatment rule for {charge.ChargeType} under {charge.TransactionKind}: {ex.Error.Code} (REQ-BIL-079).");
        }
        catch (SpiException ex) when (ex.Error.Category is SpiErrorCategory.Validation or SpiErrorCategory.NotApplicable)
        {
            return (QuarantineReasons.TreatmentInvalid, $"The treatment request was refused: {ex.Error.Code}.");
        }
        catch (DomainException ex) when (ex.Error.Code.Value.EndsWith("CFG-NOT-SETTLED", StringComparison.Ordinal))
        {
            return (QuarantineReasons.TreatmentNotSettled, "The treatment rule is not Settled and Production refuses it (D-REG-02).");
        }

        var servicing = TransactionKinds.IsServicing(charge.TransactionKind);
        var mismatch = TreatmentCheck.Mismatch(result, charge.Amount, charge.TreatmentRuleId, charge.TreatmentRuleVersion, charge.Provisional, servicing);
        return mismatch is null
            ? null
            : (mismatch, $"The delta of {charge.ChargeType} ({charge.Amount}) disagrees with treatment {result.Action} rule {result.RuleId} {result.RuleVersion} (REQ-BIL-079): {mismatch}.");
    }

    /// <summary>One slice of a credit charge applied to one original invoice item (newest first).</summary>
    private sealed record CreditAllocation(ChargeRow Charge, InvoiceItemRow OriginalItem, InvoiceRow OriginalInvoice, decimal Amount);

    private sealed record CreditResolution(IReadOnlyList<CreditAllocation> Allocations, bool Waiting, (ChargeRow Charge, string Detail)? Failed);

    /// <summary>
    /// Finds the billed invoice items each credit charge reduces (REQ-BIL-073, REQ-BIL-091): the same term, element,
    /// coverage and charge type, newest invoice first, never more than an item still has uncredited. ANNUAL bills every
    /// written charge in the unit of work that writes it, so there is no unbilled item to net against first; the credit
    /// becomes a billed credit item. A credit whose original is still on its way (a pending positive charge of the
    /// element and type) waits; one that exceeds everything billed is refused (fail closed, PITFALLS 10).
    /// </summary>
    private async Task<CreditResolution> ResolveCreditsAsync(List<ChargeRow> credits, CancellationToken cancellationToken)
    {
        var allocations = new List<CreditAllocation>();
        var reserved = new Dictionary<Guid, decimal>();
        foreach (var credit in credits.OrderBy(c => c.SetIndex))
        {
            var items = await db.InvoiceItems.Where(i => i.CreditsItemId == null && i.TermId == credit.TermId && i.ElementLocator == credit.ElementLocator
                                                          && i.ChargeType == credit.ChargeType && i.CoverageCode == credit.CoverageCode)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var invoiceIds = items.Select(i => i.InvoiceId).Distinct().ToList();
            var invoices = await db.Invoices.Where(i => invoiceIds.Contains(i.InvoiceId)).ToListAsync(cancellationToken).ConfigureAwait(false);
            var itemIds = items.Select(i => i.InvoiceItemId).ToList();
            var credited = (await db.InvoiceItems.Where(i => i.CreditsItemId != null && itemIds.Contains(i.CreditsItemId.Value))
                    .GroupBy(i => i.CreditsItemId!.Value).Select(g => new { Item = g.Key, Sum = g.Sum(i => i.Amount) })
                    .ToListAsync(cancellationToken).ConfigureAwait(false))
                .ToDictionary(x => x.Item, x => x.Sum);

            var remaining = -credit.Amount;
            foreach (var item in items.OrderByDescending(i => invoices.Single(v => v.InvoiceId == i.InvoiceId).CreatedAt).ThenBy(i => i.LineNo).ThenBy(i => i.InvoiceItemId))
            {
                var creditable = item.Amount - credited.GetValueOrDefault(item.InvoiceItemId) - reserved.GetValueOrDefault(item.InvoiceItemId);
                if (creditable <= 0m || remaining <= 0m)
                {
                    continue;
                }

                var take = Math.Min(creditable, remaining);
                allocations.Add(new CreditAllocation(credit, item, invoices.Single(v => v.InvoiceId == item.InvoiceId), take));
                reserved[item.InvoiceItemId] = reserved.GetValueOrDefault(item.InvoiceItemId) + take;
                remaining -= take;
            }

            if (remaining > 0m)
            {
                var received = Codes.Of(ChargeStatus.Received);
                var written = Codes.Of(ChargeStatus.Written);
                var pending = await db.Charges.AnyAsync(
                    c => c.TermId == credit.TermId && c.ChargeType == credit.ChargeType && c.ElementLocator == credit.ElementLocator
                         && c.CoverageCode == credit.CoverageCode && c.Amount > 0m && c.SetId != credit.SetId && (c.Status == received || c.Status == written),
                    cancellationToken).ConfigureAwait(false);
                return pending
                    ? new CreditResolution([], true, null)
                    : new CreditResolution([], false, (credit, $"Credit {-credit.Amount} exceeds the {-credit.Amount - remaining} billed and still uncredited on {credit.ChargeType} of {credit.ElementLocator}."));
            }
        }

        return new CreditResolution(allocations, false, null);
    }

    /// <summary>
    /// Bills the credits of a complete set at once (REQ-BIL-074): per credit charge a CREDIT_WRITTEN entry (premium or fee
    /// clearing → written unbilled), per original invoice one CREDIT_NOTE referencing it with its own gapless number and a
    /// CREDIT_BILLED entry (written unbilled → billed receivable), the credit offset against the original's open balance
    /// (REQ-BIL-073; what is left is the account's credit balance), the fiscal CREDIT request correlated to the original
    /// document (REQ-BIL-096, REQ-BIL-097) and the events. Everything is sealed at this transaction (D-ARC-34).
    /// Returns the number of credit notes.
    /// </summary>
    private async Task<int> BillCreditsAsync(
        PlanInstanceRow plan, BillingAccountRow account, List<ChargeRow> credits, IReadOnlyList<CreditAllocation> allocations, CancellationToken cancellationToken)
    {
        var today = ledger.Today;
        var now = clock.Now;
        var currency = Currency.FromCode(account.Currency);
        var transactionId = credits[0].TransactionId;

        foreach (var credit in credits.OrderBy(c => c.SetIndex))
        {
            var rule = Unwrap(await ledger.RuleAsync(ledger.Key(EntryTypes.CreditWritten, credit.ChargeCategory, RuleQualifiers.Any), cancellationToken).ConfigureAwait(false));
            credit.WrittenEntryId = ledger.Post(new EntrySpec(
                EntryTypes.CreditWritten, account.BillingAccountId,
                [new PostingLeg(rule, new Money(-credit.Amount, currency), ChargeDimensions(credit, plan))],
                Lineage(credit.PolicyId, credit.TermId, credit.TransactionId).With("chargeId", credit.ChargeId.Value.ToString()),
                "bil.Charge.intake"));
            credit.Status = Codes.Of(ChargeStatus.Scheduled);
        }

        var notes = 0;
        foreach (var group in allocations.GroupBy(a => a.OriginalInvoice.InvoiceId).OrderBy(g => g.Key.Value))
        {
            var original = group.First().OriginalInvoice;
            var number = await numbering.NextAsync(new NumberRequest(BillingNumbering.CreditNote, today), cancellationToken).ConfigureAwait(false);
            var state = InvoiceStateModel.Machine.FireOrThrow(InvoiceStateModel.Machine.Start(InvoiceState.Planned).Value, InvoiceTrigger.Bill);
            var noteId = InvoiceId.New();
            var total = Money.Sum(group.Select(a => new Money(a.Amount, currency)), currency);
            var note = new InvoiceRow
            {
                InvoiceId = noteId,
                LegalEntityId = account.LegalEntityId,
                Jurisdiction = account.Jurisdiction,
                BillingAccountId = account.BillingAccountId,
                InvoiceNumber = number.Value,
                Kind = "CREDIT_NOTE",
                State = Codes.Of(state),
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
                OriginalInvoiceId = original.InvoiceId,
            };
            db.Invoices.Add(note);

            var items = new List<(InvoiceItemRow Item, CreditAllocation Allocation)>();
            var line = 0;
            foreach (var allocation in group.OrderBy(a => a.Charge.SetIndex).ThenBy(a => a.OriginalItem.LineNo))
            {
                var charge = allocation.Charge;
                items.Add((new InvoiceItemRow
                {
                    InvoiceItemId = Guid.CreateVersion7(),
                    InvoiceId = noteId,
                    LegalEntityId = account.LegalEntityId,
                    ChargeId = charge.ChargeId,
                    TermId = charge.TermId,
                    TransactionId = charge.TransactionId,
                    ElementLocator = charge.ElementLocator,
                    CoverageCode = charge.CoverageCode,
                    ChargeType = charge.ChargeType,
                    ChargeCategory = charge.ChargeCategory,
                    FiscalCategoryKey = charge.FiscalCategoryKey,
                    LegalStatus = charge.LegalStatus,
                    Provisional = charge.Provisional,
                    ValidFrom = charge.ValidFrom,
                    ValidTo = charge.ValidTo,
                    Amount = allocation.Amount,
                    Currency = charge.Currency,
                    State = Codes.Of(InvoiceItemState.Open),
                    LineNo = ++line,
                    TransactionKind = charge.TransactionKind,
                    CancellationSource = charge.CancellationSource,
                    TreatmentRuleId = charge.TreatmentRuleId,
                    CreditsItemId = allocation.OriginalItem.InvoiceItemId,
                }, allocation));
            }

            db.InvoiceItems.AddRange(items.Select(i => i.Item));
            var lineage = Lineage(plan.PolicyId, plan.TermId, transactionId).With("invoiceId", noteId.Value.ToString())
                .With("originalInvoiceId", original.InvoiceId.Value.ToString());

            var billedLegs = new List<PostingLeg>();
            foreach (var (item, allocation) in items)
            {
                var rule = Unwrap(await ledger.RuleAsync(ledger.Key(EntryTypes.CreditBilled, item.ChargeCategory, RuleQualifiers.Any), cancellationToken).ConfigureAwait(false));
                var dimensions = ChargeDimensions(allocation.Charge, plan) with { InvoiceId = noteId, InvoiceItemId = item.InvoiceItemId };
                billedLegs.Add(new PostingLeg(rule, new Money(item.Amount, currency), dimensions));
            }

            ledger.Post(new EntrySpec(EntryTypes.CreditBilled, account.BillingAccountId, billedLegs, lineage, "bil.CreditNote.bill"));

            await ApplyToOriginalAsync(note, original, items, currency, now, cancellationToken).ConfigureAwait(false);
            await RequestCreditFiscalDocumentAsync(note, original, account, [.. items.Select(i => i.Item)], cancellationToken).ConfigureAwait(false);

            var keys = lineage.With("billingAccountId", account.BillingAccountId.Value.ToString());
            events.Publish(new OutgoingEvent(
                EventDescriptor.From(ChargesScheduledV1.Descriptor), "BillingAccount", account.BillingAccountId.Value.ToString(),
                new ChargesScheduledV1
                {
                    TermId = plan.TermId,
                    TransactionId = transactionId,
                    ChargeIds = [.. items.Select(i => i.Item.ChargeId).Distinct()],
                    Items = [.. items.Select(i => new ScheduledItem { ItemId = i.Item.InvoiceItemId, Status = ScheduledItem.StatusValue.Billed })],
                },
                keys) { OccurredAt = now });
            events.Publish(new OutgoingEvent(
                EventDescriptor.From(InvoiceIssuedV1.Descriptor), "BillingAccount", account.BillingAccountId.Value.ToString(),
                new InvoiceIssuedV1
                {
                    InvoiceId = noteId,
                    InvoiceNumber = InvoiceNumber.Parse(note.InvoiceNumber),
                    Kind = InvoiceIssuedV1.KindValue.CreditNote,
                    TermIds = [plan.TermId],
                    TotalsByCategory = [.. items.GroupBy(i => i.Item.ChargeCategory).OrderBy(g => g.Key, StringComparer.Ordinal)
                        .Select(g => new CategoryTotal { Category = g.Key, Amount = Money.Sum(g.Select(i => new Money(i.Item.Amount, currency)), currency) })],
                    DueDate = note.DueDate,
                    Method = note.Method,
                    FiscalTriggerRef = note.FiscalDocumentId?.Value.ToString("D"),
                },
                keys) { OccurredAt = now });
            notes++;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return notes;
    }

    /// <summary>
    /// REQ-BIL-073: a credit note offsets the open balance of the invoice item it credits (a credit application, no ledger
    /// entry: both sides are already in LA-02 through CREDIT_BILLED). What the original no longer asks for is its open
    /// amount less the credit; what could not be applied (the original was paid) stays on the credit note as account
    /// credit for a refund. An original left with nothing open is Reversed (the credit note offsets it in full).
    /// </summary>
    private async Task ApplyToOriginalAsync(
        InvoiceRow note, InvoiceRow original, List<(InvoiceItemRow Item, CreditAllocation Allocation)> items, Currency currency, Instant now, CancellationToken cancellationToken)
    {
        var open = await allocator.OpenByItemAsync([original.InvoiceId], cancellationToken).ConfigureAwait(false);
        foreach (var (item, allocation) in items)
        {
            var target = allocation.OriginalItem;
            var apply = Math.Min(item.Amount, open.GetValueOrDefault(target.InvoiceItemId));
            if (apply <= 0m)
            {
                continue;
            }

            db.CreditApplications.Add(new CreditApplicationRow
            {
                CreditApplicationId = Guid.CreateVersion7(),
                LegalEntityId = note.LegalEntityId,
                BillingAccountId = note.BillingAccountId,
                CreditNoteId = note.InvoiceId,
                CreditItemId = item.InvoiceItemId,
                TargetKind = CreditApplicationTargets.InvoiceItem,
                TargetInvoiceId = original.InvoiceId,
                TargetInvoiceItemId = target.InvoiceItemId,
                Amount = apply,
                Currency = currency.Code,
                Actor = context.Actor.ToString(),
                RecordedAt = now,
            });
            open[target.InvoiceItemId] -= apply;
            if (open[target.InvoiceItemId] == 0m && target.State == Codes.Of(InvoiceItemState.Open))
            {
                target.State = Codes.Of(InvoiceItemState.Settled);
            }

            if (apply == item.Amount)
            {
                item.State = Codes.Of(InvoiceItemState.Settled);
            }
        }

        var originalState = Codes.Parse<InvoiceState>(original.State);
        if (open.Values.All(v => v == 0m) && InvoiceStateModel.Machine.CanFire(originalState, InvoiceTrigger.Reverse))
        {
            original.State = Codes.Of(InvoiceStateModel.Machine.FireOrThrow(originalState, InvoiceTrigger.Reverse));
            original.UpdatedAt = now;
            original.RecordVersion++;
        }
    }

    /// <summary>
    /// One fiscal request of role CREDIT per credit note (REQ-BIL-096, REQ-BIL-097), correlated to the original invoice's
    /// fiscal document (D-SL3-07). Never blocks billing (REQ-BIL-099); without the original's document the credit is not
    /// requested (an uncorrelated credit would be fiscally wrong) and an exception is raised.
    /// </summary>
    private async Task RequestCreditFiscalDocumentAsync(InvoiceRow note, InvoiceRow original, BillingAccountRow account, List<InvoiceItemRow> items, CancellationToken cancellationToken)
    {
        note.FiscalTriggerPoint = FiscalTriggerPoint;
        var cmp = services.GetService<IComplianceFiscalDocumentService>();
        if (cmp is null || original.FiscalDocumentId is null || items.Any(i => i.FiscalCategoryKey is null))
        {
            note.FiscalStatus = Codes.Of(FiscalStatus.RequestFailed);
            await RaiseAsync(ExceptionKinds.FiscalRequestFailed, note.InvoiceId.Value.ToString(),
                cmp is null ? "CMP-UNAVAILABLE" : original.FiscalDocumentId is null ? "FISCAL-ORIGINAL-MISSING" : "FISCAL-CATEGORY-MISSING",
                "The credit's fiscal request could not be sent; billing continues (REQ-BIL-099).", cancellationToken).ConfigureAwait(false);
            return;
        }

        var currency = Currency.FromCode(note.Currency);
        var request = new FiscalDocumentRequestRequest
        {
            SourceType = "CREDIT",
            SourceId = note.InvoiceId.Value.ToString("D"),
            Role = FiscalDocumentRequestRequest.RoleValue.Credit,
            Revision = 0,
            OriginalFiscalDocumentId = original.FiscalDocumentId,
            CorrelatedDocumentId = original.FiscalDocumentId.Value.Value,
            Lines = [.. items.Select(i => new FiscalDocumentRequestRequest.LineItem
            {
                FiscalCategoryKey = i.FiscalCategoryKey!, Amount = new Money(i.Amount, currency), ChargeId = i.ChargeId,
            })],
            CounterpartyPartyId = account.PayerPartyId,
            IssueDate = note.IssueDate,
        };
        try
        {
            var response = await cmp.RequestAsync(request, new CommandOptions(DerivedKey($"bil.fiscal:{request.SourceType}:{request.SourceId}")), cancellationToken)
                .ConfigureAwait(false);
            note.FiscalDocumentId = response.FiscalDocumentId;
            note.FiscalDocumentType = response.DocumentType;
            note.FiscalStatus = Codes.Of(response.Status == "REJECTED" ? FiscalStatus.Rejected : FiscalStatus.Pending);
        }
        catch (DomainException ex)
        {
            note.FiscalStatus = Codes.Of(FiscalStatus.RequestFailed);
            await RaiseAsync(ExceptionKinds.FiscalRequestFailed, note.InvoiceId.Value.ToString(), ex.Error.Code.Value,
                "CMP refused the credit's fiscal request; billing continues (REQ-BIL-099).", cancellationToken).ConfigureAwait(false);
        }
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

    private static LineDimensions ChargeDimensions(ChargeRow charge, PlanInstanceRow plan) => ServicingDimensions.Apply(
        new LineDimensions
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
        },
        charge.TransactionKind, charge.CancellationSource, charge.TreatmentRuleId);

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
