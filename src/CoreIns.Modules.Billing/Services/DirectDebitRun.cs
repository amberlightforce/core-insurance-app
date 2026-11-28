using System.Security.Cryptography;
using System.Text;
using CoreIns.Modules.Billing.Commands;
using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Contracts.Events;
using CoreIns.Modules.Billing.Domain;
using CoreIns.Modules.Billing.Persistence;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Configuration;
using CoreIns.Platform.Context;
using CoreIns.Platform.Events;
using CoreIns.Platform.Numbering;
using CoreIns.Platform.Telemetry;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Billing.Services;

/// <summary>What one collection run did.</summary>
internal sealed record DirectDebitRunResult(int Submitted, int Settled, Guid? CollectionId);

/// <summary>The bank's word on one collected instalment (pain.002 / camt R-transaction).</summary>
internal enum DirectDebitOutcome
{
    /// <summary>The money arrived (settlement).</summary>
    Settled,

    /// <summary>Refused before settlement.</summary>
    Rejected,

    /// <summary>Returned after settlement (return, refund, reversal).</summary>
    Returned,
}

/// <summary>What a status ingestion did: the item as it stands and, for a return, the ledger entry that took the cash back.</summary>
internal sealed record DirectDebitStatusResult(Guid ItemId, string State, Guid? ReceiptId, Guid? ReturnEntryId);

/// <summary><c>bil.DirectDebit.run</c> (internal, scheduled): one SEPA direct-debit collection run (BO-BIL-06b).</summary>
internal sealed record RunDirectDebitCollection : ICommand<DirectDebitRunResult>;

/// <summary><c>bil.DirectDebit.status</c> (internal, bank file or dev bank): applies one bank status to a collected instalment.</summary>
internal sealed record ApplyDirectDebitStatus(string EndToEndId, DirectDebitOutcome Outcome, string? ReasonCode) : ICommand<DirectDebitStatusResult>;

internal sealed class RunDirectDebitCollectionHandler(DirectDebitCollector collector) : ICommandHandler<RunDirectDebitCollection, DirectDebitRunResult>
{
    public async Task<Result<DirectDebitRunResult>> HandleAsync(RunDirectDebitCollection command, CancellationToken cancellationToken) =>
        await collector.RunAsync(cancellationToken).ConfigureAwait(false);
}

internal sealed class ApplyDirectDebitStatusHandler(DirectDebitCollector collector) : ICommandHandler<ApplyDirectDebitStatus, DirectDebitStatusResult>
{
    public async Task<Result<DirectDebitStatusResult>> HandleAsync(ApplyDirectDebitStatus command, CancellationToken cancellationToken) =>
        await collector.ApplyStatusAsync(command.EndToEndId, command.Outcome, command.ReasonCode, cancellationToken).ConfigureAwait(false);
}

internal sealed class DirectDebitRunAuditor : ICommandAuditor<RunDirectDebitCollection, DirectDebitRunResult>
{
    public CommandAuditFacts Describe(RunDirectDebitCollection command, Result<DirectDebitRunResult>? result) => new()
    {
        ObjectRef = new ObjectRef(ModuleCode.BIL, "DirectDebitCollection", result is { IsSuccess: true } ok ? ok.Value.CollectionId?.ToString("D") ?? "none" : "none"),
        BusinessKeys = BusinessKeys.Empty,
        Changes = result is { IsSuccess: true } done ? AuditDiff.Compute(null, new { submitted = done.Value.Submitted, settled = done.Value.Settled }) : [],
    };
}

internal sealed class DirectDebitStatusAuditor : ICommandAuditor<ApplyDirectDebitStatus, DirectDebitStatusResult>
{
    public CommandAuditFacts Describe(ApplyDirectDebitStatus command, Result<DirectDebitStatusResult>? result) => new()
    {
        ObjectRef = new ObjectRef(ModuleCode.BIL, "DirectDebitItem", result is { IsSuccess: true } ok ? ok.Value.ItemId.ToString("D") : command.EndToEndId),
        BusinessKeys = BusinessKeys.Empty.With("endToEndId", command.EndToEndId),
        Changes = AuditDiff.Compute(null, new { outcome = command.Outcome.ToString(), reason = command.ReasonCode, state = result is { IsSuccess: true } done ? done.Value.State : null }),
    };
}

/// <summary>
/// The SEPA direct-debit collection run (BO-BIL-06b, REQ-BIL-106..109). A run selects the invoices of DIRECT_DEBIT terms that fall due within
/// <see cref="BillingOptions.DirectDebitLeadDays"/> and have an ACTIVE mandate on a live instrument of the payer, writes one pain.008 file
/// through the <see cref="IBankFileStore"/> port (IBANs decrypted for the file only), records the submission (one live item per invoice,
/// database-enforced, so an instalment is collected once) and moves the mandate from FRST to RCUR. Settlement and R-transactions come in
/// through <see cref="ApplyStatusAsync"/>: a settlement creates the DIRECT_DEBIT receipt and allocates it to the invoice; a reject before
/// settlement frees the invoice; a return after settlement unallocates the receipt, books <c>RECEIPT_RETURNED</c> (cash back out of the bank),
/// reverses the receipt and reopens the invoice, so it can be collected again or chased as arrears. Without a bank-file store (Production in the
/// slice) or without the creditor identity the run does nothing (fail closed). With the stub bank in its default Auto mode, outside Production
/// only, the run settles what it collects at once.
/// TODO(hardening): ingest real pain.002/camt.054 status files; retry policy for a returned instalment (re-presentation, mandate cancellation on MD01/MD07, delinquency trigger on AM04/MS03);
/// FRST stays FRST when the first collection is rejected; mandate-pre-notification (REQ-BIL-109) document; business-day calendar for the collection date.
/// </summary>
internal sealed class DirectDebitCollector(
    BillingDbContext db,
    RequestContext context,
    IClock clock,
    IOptions<BillingOptions> options,
    LedgerWriter ledger,
    Allocator allocator,
    AllocationReverser reverser,
    PayeeProtection protection,
    INumberingService numbering,
    IEventPublisher events,
    IServiceProvider services)
{
    public async Task<Result<DirectDebitRunResult>> RunAsync(CancellationToken cancellationToken)
    {
        var o = options.Value;
        if (services.GetService<IBankFileStore>() is not { } store)
        {
            return DomainError.Of(ModuleCode.BIL, "METHOD-UNAVAILABLE", "No bank-file store is bound; direct debits are not collected (fail closed).");
        }

        if (string.IsNullOrWhiteSpace(o.DirectDebitCreditorId) || string.IsNullOrWhiteSpace(o.DirectDebitCreditorIban) || string.IsNullOrWhiteSpace(o.DirectDebitCreditorName))
        {
            return DomainError.Of(ModuleCode.BIL, "VALIDATION", "The SEPA creditor name, identifier and account are not configured; direct debits are not collected (fail closed).");
        }

        var legalEntity = ledger.LegalEntityId;
        var today = ledger.Today;
        var horizon = today.AddDays(o.DirectDebitLeadDays);
        var now = clock.Now;
        var debitable = new[] { Codes.Of(InvoiceState.Due), Codes.Of(InvoiceState.Overdue) };
        var active = Codes.Of(MandateStatus.Active);

        var candidates = await (
            from i in db.Invoices
            join p in db.PlanInstances on i.TermId equals p.TermId
            join a in db.Accounts on i.BillingAccountId equals a.BillingAccountId
            where i.LegalEntityId == legalEntity && i.Kind == "INVOICE" && debitable.Contains(i.State) && i.DueDate <= horizon
                  && p.Method == PaymentPlanRules.DirectDebit && a.Status == Codes.Of(BillingAccountStatus.Active) && i.Currency == "EUR"
                  && !db.DirectDebitItems.Any(d => d.InvoiceId == i.InvoiceId && DirectDebitStates.Live.Contains(d.State))
            orderby i.DueDate, i.InvoiceNumber
            select i).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            return new DirectDebitRunResult(0, 0, null);
        }

        var accountIds = candidates.Select(i => i.BillingAccountId).Distinct().ToList();
        var mandates = await db.Mandates.Where(m => accountIds.Contains(m.BillingAccountId) && m.LegalEntityId == legalEntity && m.Status == active && m.InstrumentId != null)
            .ToDictionaryAsync(m => m.BillingAccountId, cancellationToken).ConfigureAwait(false);
        var instrumentIds = mandates.Values.Select(m => m.InstrumentId!.Value).Distinct().ToList();
        var instruments = await db.PaymentInstruments.AsNoTracking().Where(i => instrumentIds.Contains(i.InstrumentId) && i.Status == Codes.Of(InstrumentStatus.Active))
            .ToDictionaryAsync(i => i.InstrumentId, cancellationToken).ConfigureAwait(false);

        var collectionId = Guid.CreateVersion7();
        var messageId = "DD" + collectionId.ToString("N");
        var debits = new List<Pain008Debit>();
        var items = new List<DirectDebitItemRow>();
        var firstUsed = new HashSet<Guid>();
        foreach (var invoice in candidates)
        {
            if (!mandates.TryGetValue(invoice.BillingAccountId, out var mandate) || mandate.SignatureDate is not { } signed
                || !instruments.TryGetValue(mandate.InstrumentId!.Value, out var instrument))
            {
                continue;
            }

            var open = await allocator.OpenAsync(invoice, cancellationToken).ConfigureAwait(false);
            if (open.Amount <= 0m || !open.IsRoundedToMinorUnits)
            {
                continue;
            }

            // The first debit under a FRST mandate is FRST; a second one in the same run is already RCUR.
            var sequence = string.Equals(mandate.SequenceType, Codes.Of(MandateSequence.Frst), StringComparison.Ordinal) && firstUsed.Add(mandate.MandateId)
                ? "FRST" : "RCUR";
            var itemId = Guid.CreateVersion7();
            var collectionDate = BusinessDate.Max(invoice.DueDate, today);
            var iban = await protection.DecryptInstrumentAsync(instrument.InstrumentId, instrument.IbanEncrypted, cancellationToken).ConfigureAwait(false);
            var endToEnd = "DD" + itemId.ToString("N");
            debits.Add(new Pain008Debit(endToEnd, open.Amount, mandate.MandateReference, signed, instrument.HolderName, iban, sequence, collectionDate, invoice.InvoiceNumber));
            items.Add(new DirectDebitItemRow
            {
                ItemId = itemId,
                CollectionId = collectionId,
                LegalEntityId = legalEntity,
                BillingAccountId = invoice.BillingAccountId,
                InvoiceId = invoice.InvoiceId,
                MandateId = mandate.MandateId,
                InstrumentId = instrument.InstrumentId,
                EndToEndId = endToEnd,
                Amount = open.Amount,
                Currency = invoice.Currency,
                SequenceType = sequence,
                CollectionDate = collectionDate,
                State = DirectDebitStates.Submitted,
                CreatedAt = now,
                RecordVersion = 1,
            });
        }

        if (items.Count == 0)
        {
            return new DirectDebitRunResult(0, 0, null);
        }

        var xml = Pain008.Build(messageId, now, o.DirectDebitCreditorName, o.DirectDebitCreditorId, o.DirectDebitCreditorIban.Replace(" ", string.Empty, StringComparison.Ordinal), debits);
        var bytes = Encoding.UTF8.GetBytes(xml);
        var reference = "direct-debit/" + messageId + ".xml";
        await store.StoreAsync(reference, bytes, cancellationToken).ConfigureAwait(false);

        db.DirectDebitCollections.Add(new DirectDebitCollectionRow
        {
            CollectionId = collectionId,
            LegalEntityId = legalEntity,
            MessageId = messageId,
            FileReference = reference,
            FileSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            CollectionDate = items.Max(i => i.CollectionDate),
            ItemCount = items.Count,
            TotalAmount = items.Sum(i => i.Amount),
            Currency = "EUR",
            CreatedAt = now,
            CreatedBy = context.Actor.ToString(),
        });
        db.DirectDebitItems.AddRange(items);

        // The mandate has now been used: first_used_at is set once and a FRST mandate becomes RCUR (amendment returns it to FRST).
        foreach (var id in items.Select(i => i.MandateId).Distinct())
        {
            var mandate = mandates.Values.Single(m => m.MandateId == id);
            mandate.FirstUsedAt ??= now;
            mandate.SequenceType = Codes.Of(MandateSequence.Rcur);
            mandate.RecordVersion++;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // The stub bank (never bound in Production) settles at once unless it is held in Manual mode.
        var settled = 0;
        if (services.GetService<IBankChannel>() is StubBankChannel { Manual: false })
        {
            foreach (var item in items.Where(i => i.CollectionDate <= today))
            {
                var done = await ApplyStatusAsync(item.EndToEndId, DirectDebitOutcome.Settled, null, cancellationToken).ConfigureAwait(false);
                if (done.IsFailure)
                {
                    return done.Error!;
                }

                settled++;
            }
        }

        return new DirectDebitRunResult(items.Count, settled, collectionId);
    }

    public async Task<Result<DirectDebitStatusResult>> ApplyStatusAsync(string endToEndId, DirectDebitOutcome outcome, string? reasonCode, CancellationToken cancellationToken)
    {
        var legalEntity = ledger.LegalEntityId;
        var item = await db.DirectDebitItems.SingleOrDefaultAsync(i => i.LegalEntityId == legalEntity && i.EndToEndId == endToEndId, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return BillingErrors.NotFound("direct debit");
        }

        var reason = string.IsNullOrWhiteSpace(reasonCode) ? null : reasonCode.Trim().ToUpperInvariant();
        if (reason is { Length: > 4 } || reason is not null && !reason.All(char.IsAsciiLetterOrDigit))
        {
            return DomainError.Of(ModuleCode.BIL, "VALIDATION", "The reason is an ISO 20022 external code of at most four characters.");
        }

        switch (outcome)
        {
            case DirectDebitOutcome.Settled:
                if (item.State == DirectDebitStates.Collected)
                {
                    return Result(item, null);
                }

                if (item.State != DirectDebitStates.Submitted)
                {
                    return NotApplicable(item);
                }

                return await SettleAsync(item, cancellationToken).ConfigureAwait(false);

            case DirectDebitOutcome.Rejected when item.State == DirectDebitStates.Submitted:
            case DirectDebitOutcome.Returned when item.State == DirectDebitStates.Submitted:
                // Nothing was credited: the instalment is simply collectable again.
                item.State = DirectDebitStates.Rejected;
                item.ReasonCode = reason;
                item.ClosedAt = clock.Now;
                item.RecordVersion++;
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return Result(item, null);

            case DirectDebitOutcome.Rejected or DirectDebitOutcome.Returned:
                if (item.State is DirectDebitStates.Rejected or DirectDebitStates.Returned)
                {
                    return Result(item, null);
                }

                return await ReturnAsync(item, reason, cancellationToken).ConfigureAwait(false);

            default:
                return NotApplicable(item);
        }
    }

    private async Task<Result<DirectDebitStatusResult>> SettleAsync(DirectDebitItemRow item, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.SingleAsync(i => i.InvoiceId == item.InvoiceId, cancellationToken).ConfigureAwait(false);
        var account = await db.Accounts.AsNoTracking().SingleAsync(a => a.BillingAccountId == item.BillingAccountId, cancellationToken).ConfigureAwait(false);
        var now = clock.Now;
        var today = ledger.Today;
        var currency = Currency.FromCode(item.Currency);
        var amount = new Money(item.Amount, currency);
        var number = await numbering.NextAsync(new NumberRequest(NumberingSchemes.Receipt, today), cancellationToken).ConfigureAwait(false);
        var receipt = new ReceiptRow
        {
            ReceiptId = PaymentId.New(),
            LegalEntityId = item.LegalEntityId,
            Jurisdiction = account.Jurisdiction,
            BillingAccountId = item.BillingAccountId,
            ReceiptNumber = number.Value,
            Channel = ReceiptCodes.ChannelBank,
            Method = ReceiptCodes.DirectDebit,
            Amount = item.Amount,
            Currency = item.Currency,
            ValueDate = item.CollectionDate,
            AccountingDate = today,
            InvoiceRef = invoice.InvoiceId,
            BankReference = item.EndToEndId,
            State = Codes.Of(PaymentStateModel.Machine.Start(PaymentState.Received).Value),
            RecordedAt = now,
            CreatedBy = context.Actor.ToString(),
            RecordVersion = 1,
        };
        db.Receipts.Add(receipt);

        var rule = await ledger.RuleAsync(ledger.Key(EntryTypes.Received, RuleQualifiers.Any, RuleQualifiers.Any), cancellationToken).ConfigureAwait(false);
        if (rule.IsFailure)
        {
            db.Entry(receipt).State = EntityState.Detached;
            return rule.Error!;
        }

        var keys = BusinessKeys.Empty.With("receiptId", receipt.ReceiptId.Value.ToString()).With("endToEndId", item.EndToEndId);
        ledger.Post(new EntrySpec(
            EntryTypes.Received, account.BillingAccountId,
            [new PostingLeg(rule.Value, amount, new LineDimensions { BillingAccountId = account.BillingAccountId, ReceiptId = receipt.ReceiptId })],
            keys, "bil.DirectDebit.settle"));
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(PaymentReceivedV1.Descriptor), "BillingAccount", account.BillingAccountId.Value.ToString(),
            new PaymentReceivedV1
            {
                ReceiptId = receipt.ReceiptId,
                ReceiptNumber = ReceiptNumber.Parse(receipt.ReceiptNumber),
                Channel = receipt.Channel,
                Method = receipt.Method,
                Amount = amount,
                ValueDate = receipt.ValueDate,
                AccountingDate = receipt.AccountingDate,
            },
            keys.With("billingAccountId", account.BillingAccountId.Value.ToString())) { OccurredAt = now });

        item.State = DirectDebitStates.Collected;
        item.ReceiptId = receipt.ReceiptId.Value;
        item.SettledAt = now;
        item.RecordVersion++;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // The collected amount is exactly the open amount at submission; if the invoice changed since, the cash stays unapplied in suspense.
        var open = await allocator.OpenAsync(invoice, cancellationToken).ConfigureAwait(false);
        var applied = false;
        if (open == amount)
        {
            var allocated = await allocator.AllocateAsync(receipt, [(invoice, amount)], ReceiptCodes.RuleDirectDebit, "RULE", cancellationToken).ConfigureAwait(false);
            applied = allocated.IsSuccess;
            if (!applied)
            {
                await allocator.DiscardPendingAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        if (!applied)
        {
            receipt.State = Codes.Of(PaymentStateModel.Machine.FireOrThrow(PaymentState.Received, PaymentTrigger.MoveToSuspense));
            receipt.SuspenseReason = open == amount ? ReceiptCodes.ConcurrentAllocation : ReceiptCodes.AmountMismatch;
            receipt.RecordVersion++;
            db.SuspenseItems.Add(SuspenseWorkbench.NewItem(receipt, now));
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return Result(item, null);
    }

    private async Task<Result<DirectDebitStatusResult>> ReturnAsync(DirectDebitItemRow item, string? reason, CancellationToken cancellationToken)
    {
        var receiptId = new PaymentId(item.ReceiptId ?? throw new InvalidOperationException("A collected item has a receipt."));
        var receipt = await db.Receipts.SingleAsync(r => r.ReceiptId == receiptId, cancellationToken).ConfigureAwait(false);
        var state = Codes.Parse<PaymentState>(receipt.State);
        if (state is PaymentState.Allocated or PaymentState.PartiallyAllocated)
        {
            // A bank return is a fact: no authority is asked. A closed accounting period still refuses (the item stays COLLECTED and is retried).
            var reversed = await reverser.ReverseAsync(receiptId, null, "DIRECT_DEBIT_RETURN", reason is null ? null : "R-transaction " + reason, cancellationToken, bankFact: true).ConfigureAwait(false);
            if (reversed.IsFailure)
            {
                return reversed.Error!;
            }

            receipt = reversed.Value.Receipt;
            state = Codes.Parse<PaymentState>(receipt.State);
        }

        if (state != PaymentState.Suspense)
        {
            return DomainError.Of(ModuleCode.BIL, "STALE", $"Receipt {receipt.ReceiptNumber} is {receipt.State}; the return cannot be booked.");
        }

        var now = clock.Now;
        var rule = await ledger.RuleAsync(ledger.Key(EntryTypes.ReceiptReturned, RuleQualifiers.Any, RuleQualifiers.Any), cancellationToken).ConfigureAwait(false);
        if (rule.IsFailure)
        {
            return rule.Error!;
        }

        var amount = new Money(receipt.Amount, Currency.FromCode(receipt.Currency));
        var entryId = ledger.Post(new EntrySpec(
            EntryTypes.ReceiptReturned, receipt.BillingAccountId,
            [new PostingLeg(rule.Value, amount, new LineDimensions { BillingAccountId = receipt.BillingAccountId, ReceiptId = receipt.ReceiptId })],
            BusinessKeys.Empty.With("receiptId", receipt.ReceiptId.Value.ToString()).With("endToEndId", item.EndToEndId), "bil.DirectDebit.return"));

        receipt.State = Codes.Of(PaymentStateModel.Machine.FireOrThrow(state, PaymentTrigger.Reverse));
        receipt.SuspenseReason = null;
        receipt.RecordVersion++;
        var suspense = await db.SuspenseItems.SingleOrDefaultAsync(s => s.ReceiptId == receipt.ReceiptId, cancellationToken).ConfigureAwait(false);
        if (suspense is not null)
        {
            suspense.State = SuspenseStates.Returned;
            suspense.ResolvedAt = now;
            suspense.ResolvedBy = context.Actor.ToString();
            suspense.RecordVersion++;
        }

        item.State = DirectDebitStates.Returned;
        item.ReasonCode = reason;
        item.ClosedAt = now;
        item.RecordVersion++;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result(item, entryId);
    }

    private static DirectDebitStatusResult Result(DirectDebitItemRow item, Guid? entryId) => new(item.ItemId, item.State, item.ReceiptId, entryId);

    private static DomainError NotApplicable(DirectDebitItemRow item) =>
        DomainError.Of(ModuleCode.BIL, "NOT-ELIGIBLE", $"The direct debit {item.EndToEndId} is {item.State}; the bank status does not apply.");
}

/// <summary>One collection run as the service actor <c>bil-direct-debit</c> in its own scope (as the delinquency sweep).</summary>
internal sealed partial class DirectDebitRunner(IServiceScopeFactory scopes, IOptions<StampOptions> stamp, ILogger<DirectDebitRunner> logger)
{
    public const string ActorId = "bil-direct-debit";

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var legalEntity = stamp.Value.LegalEntity is { Length: > 0 } code
            ? LegalEntityCode.Parse(code)
            : throw new InvalidOperationException("Stamp:LegalEntity is not configured.");
        var jurisdiction = Jurisdiction.Parse(stamp.Value.Country ?? throw new InvalidOperationException("Stamp:Country is not configured."));

        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var context = scope.ServiceProvider.GetRequiredService<RequestContext>();
            context.Actor = ActorRef.Service(ActorId);
            context.LegalEntity = legalEntity;
            context.Jurisdiction = jurisdiction;
            context.Origin = EventOrigin.Live;
            context.ConfigurationHash = await scope.ServiceProvider.GetRequiredService<IConfigurationResolver>()
                .CurrentHashAsync(null, cancellationToken).ConfigureAwait(false);

            var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<RunDirectDebitCollection, DirectDebitRunResult>>();
            var result = await handler.HandleAsync(new RunDirectDebitCollection(), cancellationToken).ConfigureAwait(false);
            if (result.IsFailure)
            {
                LogFailed(logger, result.Error!.ToString());
                return 0;
            }

            if (result.Value.Submitted > 0)
            {
                LogSubmitted(logger, result.Value.Submitted, result.Value.Settled);
            }

            return result.Value.Submitted;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The direct-debit collection run did nothing: {Reason}")]
    private static partial void LogFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "The direct-debit collection run submitted {Submitted} instalment(s), {Settled} settled.")]
    private static partial void LogSubmitted(ILogger logger, int submitted, int settled);
}

/// <summary>The hosted job: runs <see cref="DirectDebitRunner"/> every <see cref="BillingOptions.DirectDebitIntervalSeconds"/> seconds (worker role, <see cref="BillingOptions.DirectDebitEnabled"/>).</summary>
internal sealed partial class DirectDebitSweeper(DirectDebitRunner runner, IOptions<BillingOptions> options, ILogger<DirectDebitSweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.Value.DirectDebitIntervalSeconds)));
        do
        {
            try
            {
                await runner.RunAsync(stoppingToken).ConfigureAwait(false);
                CoreInsMetrics.SweeperSucceeded("direct-debit");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogRunFailed(logger, ex);
                CoreInsMetrics.SweeperFailed("direct-debit");
            }
        }
        while (await WaitAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The direct-debit collection run failed; it runs again at the next tick.")]
    private static partial void LogRunFailed(ILogger logger, Exception exception);
}
