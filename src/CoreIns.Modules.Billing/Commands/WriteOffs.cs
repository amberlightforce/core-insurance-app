using System.Globalization;
using System.Text.Json;
using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Billing.Domain;
using CoreIns.Modules.Billing.Persistence;
using CoreIns.Modules.Billing.Services;
using CoreIns.Modules.Finance.Contracts;
using CoreIns.Modules.Market.Contracts;
using CoreIns.Modules.Market.Contracts.Api;
using CoreIns.Modules.Market.Contracts.Spi;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Authority;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Billing.Commands;

/// <summary><c>bil.WriteOff.propose</c> (BO-BIL-13).</summary>
internal sealed record ProposeWriteOff(WriteOffProposeRequest Request) : ICommand<WriteOffProposeResponse>;

/// <summary><c>bil.WriteOff.decide</c> (BO-BIL-13).</summary>
internal sealed record DecideWriteOff(WriteOffDecideRequest Request) : ICommand<WriteOffDecideResponse>;

/// <summary><c>bil.WriteOff.post</c> (BO-BIL-13).</summary>
internal sealed record PostWriteOff(WriteOffPostRequest Request) : ICommand<WriteOffPostResponse>;

internal sealed class ProposeWriteOffValidator : AbstractValidator<ProposeWriteOff>
{
    public ProposeWriteOffValidator()
    {
        RuleFor(c => c.Request.ReasonCode).Must(AllocationReversalRules.ValidReason).WithErrorCode("REASON_CODE")
            .WithMessage("The reason is a code of capital letters, digits and underscores.");
        RuleFor(c => c.Request.Amount).Must(a => a.IsPositive && a.IsRoundedToMinorUnits).WithErrorCode("AMOUNT")
            .WithMessage("The amount must be positive and rounded to minor units.");
        RuleFor(c => c.Request.Comment).MaximumLength(500);
    }
}

internal sealed class DecideWriteOffValidator : AbstractValidator<DecideWriteOff>
{
    public DecideWriteOffValidator() => RuleFor(c => c.Request.Comment).MaximumLength(500);
}

/// <summary>Builds the API view and the posting preview of a write-off.</summary>
internal static class WriteOffViews
{
    public static WriteOffView View(WriteOffRow row, string? invoiceNumber)
    {
        var currency = Currency.FromCode(row.Currency);
        return new WriteOffView
        {
            WriteOffId = row.WriteOffId,
            BillingAccountId = row.BillingAccountId,
            InvoiceId = row.InvoiceId,
            InvoiceNumber = invoiceNumber,
            Amount = new Money(row.Amount, currency),
            Kind = row.Kind == WriteOffStates.KindTolerance ? WriteOffView.KindValue.Tolerance : WriteOffView.KindValue.Manual,
            State = row.State switch
            {
                WriteOffStates.Approved => WriteOffView.StateValue.Approved,
                WriteOffStates.Rejected => WriteOffView.StateValue.Rejected,
                WriteOffStates.Posted => WriteOffView.StateValue.Posted,
                _ => WriteOffView.StateValue.Proposed,
            },
            ReasonCode = row.ReasonCode,
            Comment = row.Comment,
            ProposedBy = row.ProposedBy,
            ProposedAt = row.ProposedAt,
            DecidedBy = row.DecidedBy,
            DecidedAt = row.DecidedAt,
            DecisionComment = row.DecisionComment,
            PostedAt = row.PostedAt,
            LedgerEntryId = row.LedgerEntryId,
            RecordVersion = row.RecordVersion,
        };
    }

    public static async Task<string?> InvoiceNumberAsync(BillingDbContext db, InvoiceId id, CancellationToken cancellationToken) =>
        await db.Invoices.AsNoTracking().Where(i => i.InvoiceId == id).Select(i => i.InvoiceNumber).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
}

/// <summary>
/// Plans and posts write-offs (BO-BIL-13). The open balance of an invoice is net of cash, credit and earlier write-offs; a write-off
/// is applied down the same waterfall as cash (taxes and levies before fees before premium) as append-only <see cref="WriteOffLineRow"/>s,
/// one <c>WRITTEN_OFF</c> ledger entry books it (write-off clearing LA-16, or tolerance clearing LA-19, against the billed receivable LA-02),
/// the items it covers are WrittenOff (Settled for a tolerance residual), and the invoice follows: WrittenOff when a manual write-off
/// leaves nothing open (the Overdue state-machine row), Paid when a tolerance residual does. A delinquency case the write-off cures closes.
/// </summary>
internal sealed class WriteOffPoster(
    BillingDbContext db, RequestContext context, LedgerWriter ledger, Allocator allocator, IClock clock, IServiceProvider services, IOptions<BillingOptions> options)
{
    /// <summary>The MKT key of the tolerance (an ExactDecimal amount in the account currency).</summary>
    public const string ToleranceKey = "bil.writeoff.tolerance";

    /// <summary>The tolerance in force: MKT key <c>bil.writeoff.tolerance</c>; a missing or malformed value falls back to the illustrative option (fail closed at 0 disables it).</summary>
    public async Task<decimal> ToleranceAsync(CancellationToken cancellationToken)
    {
        var fallback = options.Value.WriteOffTolerance;
        if (services.GetService<IMarketConfigurationService>() is not { } mkt)
        {
            return fallback;
        }

        var resolved = await mkt.ResolveAsync(
            new ConfigurationResolveRequest
            {
                LegalEntity = (context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.")).Value,
                Jurisdiction = ledger.Jurisdiction.Value,
                Keys = [ToleranceKey],
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (resolved.Values.FirstOrDefault(v => v.Key == ToleranceKey) is not { } item)
        {
            return fallback;
        }

        var value = item.Value.ValueKind switch
        {
            JsonValueKind.Number => item.Value.TryGetDecimal(out var n) ? n : (decimal?)null,
            JsonValueKind.String => decimal.TryParse(item.Value.GetString(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n) ? n : null,
            _ => null,
        };
        return value is >= 0m and <= 100000m ? value.Value : fallback;
    }

    /// <summary>The items of the invoice with what each can still take, in waterfall order.</summary>
    public async Task<(List<(InvoiceItemRow Item, decimal Open)> Items, decimal OpenTotal)> OpenItemsAsync(InvoiceRow invoice, CancellationToken cancellationToken)
    {
        var items = await db.InvoiceItems.Where(i => i.InvoiceId == invoice.InvoiceId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var open = await allocator.OpenByItemAsync([invoice.InvoiceId], cancellationToken).ConfigureAwait(false);
        var ordered = Waterfall.Order(items, i => i.ChargeCategory).ThenBy(i => i.LineNo)
            .Where(i => i.Amount > 0m && open.GetValueOrDefault(i.InvoiceItemId) > 0m)
            .Select(i => (i, open[i.InvoiceItemId])).ToList();
        return (ordered, ordered.Sum(x => x.Item2));
    }

    public async Task<Result<LedgerRule>> RuleAsync(string kind, CancellationToken cancellationToken) =>
        await ledger.RuleAsync(
            ledger.Key(EntryTypes.WrittenOff, RuleQualifiers.Any, kind == WriteOffStates.KindTolerance ? WriteOffStates.QualifierTolerance : WriteOffStates.QualifierWriteOff),
            cancellationToken).ConfigureAwait(false);

    /// <summary>The lines the posting books: the clearing account debited and the billed receivable credited by the amount.</summary>
    public static List<WriteOffPostingLine> Preview(LedgerRule rule, Money amount) =>
    [
        new WriteOffPostingLine { Account = rule.DebitAccount, Side = WriteOffPostingLine.SideValue.Debit, Amount = amount, RuleId = rule.RuleId },
        new WriteOffPostingLine { Account = rule.CreditAccount, Side = WriteOffPostingLine.SideValue.Credit, Amount = amount, RuleId = rule.RuleId },
    ];

    /// <summary>Posts an approved write-off in the caller's transaction.</summary>
    public async Task<Result<List<WriteOffPostingLine>>> PostAsync(WriteOffRow row, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.SingleOrDefaultAsync(i => i.InvoiceId == row.InvoiceId && i.LegalEntityId == row.LegalEntityId, cancellationToken).ConfigureAwait(false);
        if (invoice is null)
        {
            return BillingErrors.NotFound("invoice");
        }

        var state = Codes.Parse<InvoiceState>(invoice.State);
        if (state is not (InvoiceState.Due or InvoiceState.PartiallyPaid or InvoiceState.Overdue))
        {
            return DomainError.Of(ModuleCode.BIL, "INVOICE-STATE", $"Invoice {invoice.InvoiceNumber} is {invoice.State}; nothing of it can be written off.");
        }

        var period = await CheckPeriodAsync(cancellationToken).ConfigureAwait(false);
        if (period is not null)
        {
            return period;
        }

        var (items, openTotal) = await OpenItemsAsync(invoice, cancellationToken).ConfigureAwait(false);
        if (row.Amount > openTotal)
        {
            return DomainError.Of(ModuleCode.BIL, "AMOUNT-MISMATCH", $"The open balance of invoice {invoice.InvoiceNumber} is {openTotal}, below the write-off of {row.Amount}.");
        }

        var rule = await RuleAsync(row.Kind, cancellationToken).ConfigureAwait(false);
        if (rule.IsFailure)
        {
            return rule.Error!;
        }

        var currency = Currency.FromCode(row.Currency);
        var now = clock.Now;
        var remaining = row.Amount;
        var lines = new List<WriteOffLineRow>();
        var legs = new List<PostingLeg>();
        var fullyCovered = row.Amount == openTotal;
        foreach (var (item, open) in items)
        {
            if (remaining <= 0m)
            {
                break;
            }

            var applied = Math.Min(open, remaining);
            remaining -= applied;
            lines.Add(new WriteOffLineRow
            {
                WriteOffLineId = Guid.CreateVersion7(),
                WriteOffId = row.WriteOffId,
                LegalEntityId = row.LegalEntityId,
                InvoiceId = invoice.InvoiceId,
                InvoiceItemId = item.InvoiceItemId,
                Amount = applied,
                Currency = row.Currency,
            });
            if (applied == open)
            {
                item.State = Codes.Of(row.Kind == WriteOffStates.KindTolerance ? InvoiceItemState.Settled : InvoiceItemState.WrittenOff);
            }

            legs.Add(new PostingLeg(rule.Value, new Money(applied, currency), ServicingDimensions.Apply(
                new LineDimensions
                {
                    BillingAccountId = invoice.BillingAccountId,
                    PolicyId = invoice.PolicyId,
                    PolicyTermId = item.TermId,
                    TransactionId = item.TransactionId,
                    ChargeId = item.ChargeId,
                    ChargeType = item.ChargeType,
                    ChargeCategory = item.ChargeCategory,
                    CoverageCode = item.CoverageCode,
                    BillMode = PaymentPlans.DirectBill,
                    InvoiceId = invoice.InvoiceId,
                    InvoiceItemId = item.InvoiceItemId,
                },
                item.TransactionKind, item.CancellationSource, item.TreatmentRuleId)));
        }

        // A manual write-off of the whole remainder ends an Overdue invoice (state-machine row Overdue -> WrittenOff); a tolerance residual
        // settles it (Paid, like a payment that covers it). A partial write-off leaves the state.
        if (fullyCovered)
        {
            var trigger = row.Kind == WriteOffStates.KindTolerance ? InvoiceTrigger.AllocateInFull : InvoiceTrigger.WriteOff;
            if (InvoiceStateModel.Machine.Fire(state, trigger) is { IsSuccess: true } next)
            {
                invoice.State = Codes.Of(next.Value);
            }
            else
            {
                return DomainError.Of(ModuleCode.BIL, "INVOICE-STATE", $"Invoice {invoice.InvoiceNumber} is {invoice.State}; a write-off of its whole remainder needs it Overdue.");
            }
        }

        invoice.UpdatedAt = now;
        invoice.RecordVersion++;
        db.WriteOffLines.AddRange(lines);
        row.State = WriteOffStates.Posted;
        row.PostedBy = ledger.Actor;
        row.PostedAt = now;
        row.RecordVersion++;
        var lineage = BusinessKeys.Empty.With("writeOffId", row.WriteOffId.ToString("D")).With("invoiceId", invoice.InvoiceId.Value.ToString())
            .With("policyId", invoice.PolicyId.Value.ToString()).With("policyTermId", invoice.TermId.Value.ToString()).With("transactionId", invoice.TransactionId.Value.ToString());
        row.LedgerEntryId = ledger.Post(new EntrySpec(EntryTypes.WrittenOff, invoice.BillingAccountId, legs, lineage, "bil.WriteOff.post"));
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return DomainError.Of(ModuleCode.BIL, "STALE", "The write-off or the invoice changed meanwhile; reload and retry.");
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Allocator.OverAllocationState })
        {
            return DomainError.Of(ModuleCode.BIL, "AMOUNT-MISMATCH", "The database refused a write-off above the open amount of an invoice item.");
        }

        // Arrears the write-off clears cure a non-payment case (as cash does).
        if (services.GetService<NonpayService>() is { } nonpay)
        {
            await nonpay.CureAsync(invoice.BillingAccountId, cancellationToken).ConfigureAwait(false);
        }

        // TODO(hardening): publish a WriteOffPosted event for POL/CLM (arrears flags of the term).
        return Preview(rule.Value, new Money(row.Amount, currency));
    }

    /// <summary>Refuses when the accounting period of today (where the entry posts) is closed in FIN.</summary>
    private async Task<DomainError?> CheckPeriodAsync(CancellationToken cancellationToken)
    {
        if (services.GetService<IFinancePeriodService>() is not { } periods || context.LegalEntity is not { } legalEntity)
        {
            return null;
        }

        var status = await periods.StatusAsync(legalEntity.Value, ValidAt.From(ledger.Today), cancellationToken).ConfigureAwait(false);
        return status.ClosedForPosting == true
            ? DomainError.Of(ModuleCode.BIL, "PERIOD-LOCKED", $"The accounting period {status.Period} is closed; a write-off cannot be posted into it.")
            : null;
    }
}

internal sealed class ProposeWriteOffHandler(BillingDbContext db, LedgerWriter ledger, RequestContext context, IClock clock, WriteOffPoster poster)
    : ICommandHandler<ProposeWriteOff, WriteOffProposeResponse>
{
    public async Task<Result<WriteOffProposeResponse>> HandleAsync(ProposeWriteOff command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var invoice = await db.Invoices.SingleOrDefaultAsync(i => i.InvoiceId == request.InvoiceId && i.LegalEntityId == ledger.LegalEntityId, cancellationToken).ConfigureAwait(false);
        if (invoice is null)
        {
            return BillingErrors.NotFound("invoice");
        }

        if (request.Amount.Currency.Code != invoice.Currency)
        {
            return DomainError.Of(ModuleCode.BIL, "VALIDATION", $"The invoice is kept in {invoice.Currency}.");
        }

        var state = Codes.Parse<InvoiceState>(invoice.State);
        if (invoice.Kind == "CREDIT_NOTE" || state is not (InvoiceState.Due or InvoiceState.PartiallyPaid or InvoiceState.Overdue))
        {
            return DomainError.Of(ModuleCode.BIL, "INVOICE-STATE", $"Invoice {invoice.InvoiceNumber} is {invoice.State}; only a due, partly paid or overdue invoice has an open balance to write off.");
        }

        var (_, open) = await poster.OpenItemsAsync(invoice, cancellationToken).ConfigureAwait(false);
        var pending = await db.WriteOffs.AsNoTracking()
            .Where(w => w.InvoiceId == invoice.InvoiceId && (w.State == WriteOffStates.Proposed || w.State == WriteOffStates.Approved))
            .SumAsync(w => (decimal?)w.Amount, cancellationToken).ConfigureAwait(false) ?? 0m;
        var amount = request.Amount.Amount;
        if (open <= 0m)
        {
            return DomainError.Of(ModuleCode.BIL, "INVOICE-STATE", $"Invoice {invoice.InvoiceNumber} has nothing open to write off.");
        }

        if (amount + pending > open)
        {
            return DomainError.Of(ModuleCode.BIL, "AMOUNT-MISMATCH", $"The open balance of invoice {invoice.InvoiceNumber} is {open}, of which {pending} is already proposed or approved for write-off.");
        }

        // A residual that is the whole remaining balance and within the MKT tolerance is written off at once; anything else is a manual
        // write-off of an overdue invoice and waits for a second person.
        var tolerance = await poster.ToleranceAsync(cancellationToken).ConfigureAwait(false);
        var isTolerance = pending == 0m && amount == open && amount <= tolerance;
        if (!isTolerance && state != InvoiceState.Overdue)
        {
            return DomainError.Of(ModuleCode.BIL, "INVOICE-STATE", $"Invoice {invoice.InvoiceNumber} is {invoice.State}; a write-off beyond the tolerance of {tolerance} applies to an overdue invoice.");
        }

        var now = clock.Now;
        var row = new WriteOffRow
        {
            WriteOffId = Guid.CreateVersion7(),
            LegalEntityId = invoice.LegalEntityId,
            BillingAccountId = invoice.BillingAccountId,
            InvoiceId = invoice.InvoiceId,
            Amount = amount,
            Currency = invoice.Currency,
            Kind = isTolerance ? WriteOffStates.KindTolerance : WriteOffStates.KindManual,
            State = WriteOffStates.Proposed,
            ReasonCode = request.ReasonCode,
            Comment = request.Comment,
            ProposedBy = context.Actor.ToString(),
            ProposedAt = now,
            RecordVersion = 1,
        };
        var currency = Currency.FromCode(invoice.Currency);
        var rule = await poster.RuleAsync(row.Kind, cancellationToken).ConfigureAwait(false);
        if (rule.IsFailure)
        {
            return rule.Error!;
        }

        var preview = WriteOffPoster.Preview(rule.Value, new Money(amount, currency));
        if (isTolerance)
        {
            row.ToleranceLimit = tolerance;
            row.State = WriteOffStates.Approved;
            row.DecidedBy = "system:tolerance";
            row.DecidedAt = now;
            row.DecisionComment = $"Within the tolerance of {tolerance.ToString(CultureInfo.InvariantCulture)} (MKT {WriteOffPoster.ToleranceKey}).";
        }

        db.WriteOffs.Add(row);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex)
        {
            return DomainError.Of(ModuleCode.BIL, "STALE", $"The write-off could not be stored: {ex.GetBaseException().Message}");
        }

        if (isTolerance)
        {
            var posted = await poster.PostAsync(row, cancellationToken).ConfigureAwait(false);
            if (posted.IsFailure)
            {
                return posted.Error!;
            }

            preview = posted.Value;
        }

        return new WriteOffProposeResponse { WriteOff = WriteOffViews.View(row, invoice.InvoiceNumber), PostingPreview = preview };
    }
}

internal sealed class DecideWriteOffHandler(
    BillingDbContext db, LedgerWriter ledger, RequestContext context, IClock clock, IAuthorityService authority, WriteOffPoster poster)
    : ICommandHandler<DecideWriteOff, WriteOffDecideResponse>
{
    public async Task<Result<WriteOffDecideResponse>> HandleAsync(DecideWriteOff command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var row = await db.WriteOffs.SingleOrDefaultAsync(w => w.WriteOffId == request.WriteOffId && w.LegalEntityId == ledger.LegalEntityId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return BillingErrors.NotFound("write-off");
        }

        if (row.State != WriteOffStates.Proposed)
        {
            return DomainError.Of(ModuleCode.BIL, "WRITE-OFF-STATE", $"The write-off is {row.State}; only a proposed one can be decided.");
        }

        var actor = context.Actor.ToString();
        if (string.Equals(row.ProposedBy, actor, StringComparison.Ordinal))
        {
            return DomainError.Of(ModuleCode.BIL, "SOD", "The proposer cannot decide their own write-off (maker-checker).");
        }

        var currency = Currency.FromCode(row.Currency);
        var money = new Money(row.Amount, currency);
        var approve = request.Decision == WriteOffDecideRequest.DecisionValue.Approve;
        if (approve)
        {
            var check = await authority.CheckAsync(
                new AuthorityCheckRequest(
                    context.Actor, context.Roles, SupportAuthorityTypes.WriteOff,
                    new Dictionary<string, DimensionValue>(StringComparer.Ordinal) { [SupportAuthorityTypes.AmountDimension] = DimensionValue.Of(money) },
                    new ObjectRef(ModuleCode.BIL, "WriteOff", row.WriteOffId.ToString("D")), clock.Now),
                cancellationToken).ConfigureAwait(false);
            context.AuthorityChecks.Add(check);
            if (check.Decision != AuthorityDecision.Allow)
            {
                return DomainError.Of(ModuleCode.BIL, "SOD", $"No authority to approve a write-off of {money} (BIL.WRITE_OFF: {check.ReasonCode}).") with
                {
                    Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["authorityCheckId"] = check.CheckId.ToString(), ["reasonCode"] = check.ReasonCode },
                };
            }

            row.AuthorityCheckId = check.CheckId.Value;
        }

        row.State = approve ? WriteOffStates.Approved : WriteOffStates.Rejected;
        row.DecidedBy = actor;
        row.DecidedAt = clock.Now;
        row.DecisionComment = request.Comment;
        row.RecordVersion++;
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return DomainError.Of(ModuleCode.BIL, "STALE", "The write-off changed meanwhile; reload and retry.");
        }

        var rule = await poster.RuleAsync(row.Kind, cancellationToken).ConfigureAwait(false);
        if (rule.IsFailure)
        {
            return rule.Error!;
        }

        return new WriteOffDecideResponse
        {
            WriteOff = WriteOffViews.View(row, await WriteOffViews.InvoiceNumberAsync(db, row.InvoiceId, cancellationToken).ConfigureAwait(false)),
            PostingPreview = WriteOffPoster.Preview(rule.Value, money),
        };
    }
}

internal sealed class PostWriteOffHandler(BillingDbContext db, LedgerWriter ledger, WriteOffPoster poster) : ICommandHandler<PostWriteOff, WriteOffPostResponse>
{
    public async Task<Result<WriteOffPostResponse>> HandleAsync(PostWriteOff command, CancellationToken cancellationToken)
    {
        var row = await db.WriteOffs.SingleOrDefaultAsync(w => w.WriteOffId == command.Request.WriteOffId && w.LegalEntityId == ledger.LegalEntityId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return BillingErrors.NotFound("write-off");
        }

        if (row.State != WriteOffStates.Approved)
        {
            return DomainError.Of(ModuleCode.BIL, "WRITE-OFF-STATE", $"The write-off is {row.State}; only an approved one can be posted.");
        }

        var posted = await poster.PostAsync(row, cancellationToken).ConfigureAwait(false);
        if (posted.IsFailure)
        {
            return posted.Error!;
        }

        return new WriteOffPostResponse
        {
            WriteOff = WriteOffViews.View(row, await WriteOffViews.InvoiceNumberAsync(db, row.InvoiceId, cancellationToken).ConfigureAwait(false)),
            PostingPreview = posted.Value,
            LedgerEntryId = row.LedgerEntryId!.Value,
        };
    }
}

internal sealed class WriteOffAuditor<TCommand, TResponse> : ICommandAuditor<TCommand, TResponse>
    where TCommand : ICommand<TResponse>
{
    public CommandAuditFacts Describe(TCommand command, Result<TResponse>? result)
    {
        var (writeOffId, what) = command switch
        {
            DecideWriteOff c => (c.Request.WriteOffId, c.Request.Decision.ToString()),
            PostWriteOff c => (c.Request.WriteOffId, "POST"),
            ProposeWriteOff c => (result is { IsSuccess: true, Value: WriteOffProposeResponse r } ? r.WriteOff.WriteOffId : default, c.Request.ReasonCode),
            _ => (default, string.Empty),
        };
        return new CommandAuditFacts
        {
            ObjectRef = new ObjectRef(ModuleCode.BIL, "WriteOff", writeOffId.ToString("D")),
            BusinessKeys = BusinessKeys.Empty.With("writeOffId", writeOffId.ToString("D")),
            Changes = result is { IsSuccess: true } ? AuditDiff.Compute(null, new { operation = typeof(TCommand).Name, detail = what }) : [],
        };
    }
}
