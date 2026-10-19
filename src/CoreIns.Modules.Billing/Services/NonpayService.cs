using CoreIns.Modules.Billing.Commands;
using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Contracts.Events;
using CoreIns.Modules.Billing.Domain;
using CoreIns.Modules.Billing.Persistence;
using CoreIns.Modules.Compliance.Contracts;
using CoreIns.Modules.Compliance.Contracts.Api;
using CoreIns.Modules.Documents.Contracts;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Contracts.Common;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CoreIns.Modules.Billing.Services;

/// <summary>
/// The non-payment process (SL6-BIL-DELINQ, D-SL6-01/03/04): a case opens on arrears, the notice DT-NONPAY-NOTICE goes out by
/// registered post (DOC), the BIL_NONPAY_NOTICE clock (CMP) starts only from a proven notification date, a cure stops it, and
/// when it elapses with arrears above tolerance <c>CancellationForNonPaymentRequested</c> is emitted. The services of DOC and CMP
/// are resolved when used, so a host without them still takes payments.
/// </summary>
internal sealed class NonpayService(
    BillingDbContext db,
    RequestContext context,
    LedgerWriter ledger,
    TermBilling billing,
    IClock clock,
    IEventPublisher events,
    IOptions<BillingOptions> options,
    IServiceProvider services)
{
    private BillingOptions Options => options.Value;

    public static ObjectRef SubjectOf(Guid caseId) => new(ModuleCode.BIL, Nonpay.ObjectType, caseId.ToString("D"));

    /// <summary>Opens a case for the term when it has arrears above the threshold and no open case (idempotent); null when there is nothing to chase.</summary>
    public async Task<DelinquencyCaseRow?> OpenAsync(PolicyTermId termId, DelinquencyTrigger trigger, CancellationToken cancellationToken)
    {
        await billing.LockTermAsync(termId, cancellationToken).ConfigureAwait(false);
        var open = await db.DelinquencyCases.FirstOrDefaultAsync(c => c.TermId == termId && Nonpay.OpenStates.Contains(c.State), cancellationToken).ConfigureAwait(false);
        if (open is not null)
        {
            return open;
        }

        var plan = await db.PlanInstances.AsNoTracking().SingleOrDefaultAsync(p => p.TermId == termId, cancellationToken).ConfigureAwait(false);
        if (plan is null || plan.CancelledEffective is not null)
        {
            return null;
        }

        var today = ledger.Today;
        var cutoff = trigger == DelinquencyTrigger.Overdue ? today.AddDays(-Options.DelinquencyGraceDays - 1) : today;
        var arrears = await ArrearsAsync(termId, cutoff, cancellationToken).ConfigureAwait(false);
        var threshold = trigger == DelinquencyTrigger.Overdue ? Options.DelinquencyTolerance : 0m;
        if (arrears <= threshold)
        {
            return null;
        }

        var account = await db.Accounts.AsNoTracking().SingleAsync(a => a.BillingAccountId == plan.BillingAccountId, cancellationToken).ConfigureAwait(false);
        var now = clock.Now;
        var row = new DelinquencyCaseRow
        {
            CaseId = Guid.CreateVersion7(),
            LegalEntityId = ledger.LegalEntityId,
            BillingAccountId = plan.BillingAccountId,
            TermId = termId,
            PolicyId = plan.PolicyId,
            ProductCode = plan.ProductCode,
            PlanCode = plan.PlanCode,
            State = Codes.Of(DelinquencyState.NoticeRequested),
            Trigger = Codes.Of(trigger),
            OverdueAmount = arrears,
            Currency = account.Currency,
            OpenedAt = now,
            RecordVersion = 1,
        };
        db.DelinquencyCases.Add(row);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.Entry(row).State = EntityState.Detached;
            return await db.DelinquencyCases.FirstOrDefaultAsync(c => c.TermId == termId && Nonpay.OpenStates.Contains(c.State), cancellationToken).ConfigureAwait(false);
        }

        Publish(
            DelinquencyStartedV1.Descriptor,
            new DelinquencyStartedV1 { TermId = termId, OverdueAmount = MoneyOf(row, arrears), PlanCode = plan.PlanCode, PlanVersion = Nonpay.PlanVersion, ProcessId = row.CaseId },
            row,
            BusinessKeys.Empty.With("policyTermId", termId.Value.ToString()).With("processId", row.CaseId.ToString()));

        // D-SL6-03: registered post only; rendering is not proof, so the clock waits for DocumentDelivered.
        var outcome = await services.GetRequiredService<IDocumentRequestService>().RequestAsync(
            new DocumentRequestSpec(
                Nonpay.NoticeTemplate,
                row.CaseId.ToString("D"),
                SubjectOf(row.CaseId),
                account.PayerPartyId,
                "el",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    // TODO(hardening): the addressee's name belongs to PTY; BIL holds none, so the letter greets "Sir/Madam" until pty.Party is read here.
                    ["customerName"] = "Κύριε/Κυρία",
                    ["policyNo"] = plan.PolicyNumber,
                    ["amountDue"] = $"{arrears.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} {account.Currency}",
                    ["dueDate"] = (await db.Invoices.AsNoTracking().Where(i => i.TermId == termId && i.Total > 0m && i.DueDate <= cutoff).MinAsync(i => i.DueDate, cancellationToken).ConfigureAwait(false)).ToString(),
                    ["caseId"] = row.CaseId.ToString("D"),
                },
                "REGISTERED_POST"),
            cancellationToken).ConfigureAwait(false);
        row.NoticeDocumentId = outcome.DocumentId;
        Touch(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return row;
    }

    /// <summary>Opens a case for every term with an invoice overdue past the grace period; resumes arrangements whose promise date passed unpaid.</summary>
    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        var today = ledger.Today;
        var cutoff = today.AddDays(-Options.DelinquencyGraceDays - 1);
        string[] unpaid = [Codes.Of(InvoiceState.Billed), Codes.Of(InvoiceState.Due), Codes.Of(InvoiceState.PartiallyPaid), Codes.Of(InvoiceState.Overdue)];
        var terms = await db.Invoices.AsNoTracking()
            .Where(i => i.Total > 0m && i.DueDate <= cutoff && unpaid.Contains(i.State))
            .Select(i => i.TermId).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
        var opened = 0;
        foreach (var term in terms)
        {
            var before = await db.DelinquencyCases.AnyAsync(c => c.TermId == term && Nonpay.OpenStates.Contains(c.State), cancellationToken).ConfigureAwait(false);
            if (!before && await OpenAsync(term, DelinquencyTrigger.Overdue, cancellationToken).ConfigureAwait(false) is not null)
            {
                opened++;
            }
        }

        // A broken promise to pay: the clock runs on.
        var arranged = Codes.Of(DelinquencyState.Arranged);
        var broken = await db.DelinquencyCases.Where(c => c.State == arranged && c.PromiseDate != null && c.PromiseDate < today).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var row in broken)
        {
            if (await ArrearsAsync(row.TermId, today, cancellationToken).ConfigureAwait(false) > 0m)
            {
                await ResumeAsync(row, "PROMISE-BROKEN", cancellationToken).ConfigureAwait(false);
            }
        }

        return opened;
    }

    /// <summary>DOC proved delivery: the notification date starts the clock (D-SL6-03).</summary>
    public async Task NoticeDeliveredAsync(DocumentId documentId, BusinessDate notificationDate, string medium, string proofRef, CancellationToken cancellationToken)
    {
        var row = await db.DelinquencyCases.FirstOrDefaultAsync(c => c.NoticeDocumentId == documentId, cancellationToken).ConfigureAwait(false);
        if (row is null || row.ClockInstanceId is not null || Codes.Parse<DelinquencyState>(row.State) is DelinquencyState.Resolved or DelinquencyState.CancellationRequested)
        {
            return; // not ours, a redelivery, or the case is closed
        }

        if (!string.Equals(medium, "REGISTERED_POST", StringComparison.Ordinal))
        {
            return; // D-SL6-03: no other proof starts the clock
        }

        var subject = SubjectOf(row.CaseId);
        var clocks = services.GetRequiredService<IComplianceClockService>();
        var started = await clocks.StartAsync(
            new ClockStartRequest
            {
                ClockCode = ClockCode.Parse(Nonpay.ClockCode),
                SubjectRef = subject,
                Key = $"nonpay-{row.CaseId:N}",
                StartTime = notificationDate.StartOfDayIn(Options.Zone),
                Reason = "Non-payment notice delivered by registered post",
            },
            CommandOptions.New(),
            cancellationToken).ConfigureAwait(false);

        var state = Codes.Parse<DelinquencyState>(row.State);
        row.ClockInstanceId = started.InstanceId;
        row.NotificationDate = notificationDate;
        row.Deadline = started.Deadline;
        if (state is DelinquencyState.OnHold or DelinquencyState.Arranged)
        {
            await clocks.PauseAsync(
                new ClockPauseRequest { InstanceId = started.InstanceId, Key = $"nonpay-pause-{row.CaseId:N}-0", Reason = row.HoldReason ?? "Arrangement" },
                CommandOptions.New(),
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            row.State = Codes.Of(DelinquencyState.ClockRunning);
        }

        Touch(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        Publish(
            NonPaymentNoticeSentV1.Descriptor,
            new NonPaymentNoticeSentV1
            {
                TermId = row.TermId,
                OverdueAmount = MoneyOf(row, row.OverdueAmount),
                DocumentId = documentId,
                ProofRef = proofRef,
                ClockInstanceId = started.InstanceId,
                Deadline = started.Deadline ?? notificationDate.AddMonths(1).AddDays(1).StartOfDayIn(Options.Zone),
            },
            row,
            BusinessKeys.Empty.With("policyTermId", row.TermId.Value.ToString()).With("documentId", documentId.Value.ToString()).With("clockInstanceId", started.InstanceId.ToString()));
    }

    /// <summary>The registered letter came back: no clock; a work request is recorded (D-SL6-01, D-SL6-03).</summary>
    public async Task NoticeFailedAsync(DocumentId documentId, string reason, CancellationToken cancellationToken)
    {
        var row = await db.DelinquencyCases.FirstOrDefaultAsync(c => c.NoticeDocumentId == documentId, cancellationToken).ConfigureAwait(false);
        if (row is null || Codes.Parse<DelinquencyState>(row.State) != DelinquencyState.NoticeRequested)
        {
            return;
        }

        row.State = Codes.Of(DelinquencyState.NoticeReturned);
        Touch(row);
        await billing.RaiseAsync(
            Nonpay.NoticeReturnedActivity, row.CaseId.ToString("D"), "NOTICE-RETURNED",
            $"The non-payment notice of delinquency case {row.CaseId} was not delivered ({reason}); no clock was started (D-SL6-03).", cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A receipt was allocated: a case whose arrears are gone is cured (clock stopped as Met).</summary>
    public async Task CureAsync(BillingAccountId account, CancellationToken cancellationToken)
    {
        var cases = await db.DelinquencyCases.Where(c => c.BillingAccountId == account && Nonpay.OpenStates.Contains(c.State)).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var row in cases)
        {
            if (await ArrearsAsync(row.TermId, ledger.Today, cancellationToken).ConfigureAwait(false) > 0m)
            {
                continue;
            }

            var kept = Codes.Parse<DelinquencyState>(row.State) == DelinquencyState.Arranged;
            await ResolveAsync(row, kept ? "ARRANGEMENT_KEPT" : "PAID", DelinquencyResolvedV1.OutcomeValue.Paid, stopClock: true, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The waiting period elapsed: arrears above tolerance request a cancellation, otherwise the case resolves (idempotent on the clock).</summary>
    public async Task ClockElapsedAsync(Guid clockInstanceId, CancellationToken cancellationToken)
    {
        var row = await db.DelinquencyCases.FirstOrDefaultAsync(c => c.ClockInstanceId == clockInstanceId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return;
        }

        var state = Codes.Parse<DelinquencyState>(row.State);
        if (state is DelinquencyState.Resolved or DelinquencyState.CancellationRequested)
        {
            return;
        }

        if (state is DelinquencyState.OnHold or DelinquencyState.Arranged)
        {
            // D-SL6-04: a vulnerable-customer flag or a hold is an exception to cancellation; a person decides in the PSR queue.
            await billing.RaiseAsync(
                Nonpay.HoldActivity, row.CaseId.ToString("D"), "ELAPSED-ON-HOLD",
                $"The non-payment waiting period of case {row.CaseId} elapsed while the case is {row.State}; no cancellation was requested.", cancellationToken).ConfigureAwait(false);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var arrears = await ArrearsAsync(row.TermId, ledger.Today, cancellationToken).ConfigureAwait(false);
        if (arrears <= Options.DelinquencyTolerance)
        {
            await ResolveAsync(row, "TOLERANCE", DelinquencyResolvedV1.OutcomeValue.Tolerance, stopClock: false, cancellationToken).ConfigureAwait(false);
            return;
        }

        var now = clock.Now;
        row.State = Codes.Of(DelinquencyState.CancellationRequested);
        row.OverdueAmount = arrears;
        row.CancellationRequestId = row.CaseId; // POL is idempotent on requestId
        row.ResolvedAt = null;
        Touch(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var deadlineDate = (row.Deadline ?? now).ToBusinessDate(Options.Zone);
        Publish(
            CancellationForNonPaymentRequestedV1.Descriptor,
            new CancellationForNonPaymentRequestedV1
            {
                RequestId = row.CaseId,
                TermId = row.TermId,
                OverdueAmount = MoneyOf(row, arrears),
                NoticeDocumentId = row.NoticeDocumentId ?? throw new InvalidOperationException("A running clock implies a notice document."),
                ProofRef = $"DOC:{row.NoticeDocumentId.Value.Value:D}:{row.NotificationDate}",
                ClockInstanceId = clockInstanceId,
                RequestedEffectiveDate = deadlineDate.AddDays(1), // cancellation takes effect at the start of the next local day (D-SL6-04)
                Motor = row.ProductCode.Contains("MOTOR", StringComparison.OrdinalIgnoreCase),
            },
            row,
            BusinessKeys.Empty.With("requestId", row.CaseId.ToString()).With("policyTermId", row.TermId.Value.ToString())
                .With("noticeDocumentId", row.NoticeDocumentId.Value.Value.ToString()).With("clockInstanceId", clockInstanceId.ToString()));
    }

    /// <summary>A promise to pay pauses the clock (until the promise date).</summary>
    public async Task<Result<DelinquencyCaseRow>> ArrangeAsync(Guid caseId, BusinessDate promiseDate, string? note, CancellationToken cancellationToken)
    {
        var row = await FindOpenAsync(caseId, cancellationToken).ConfigureAwait(false);
        if (row.IsFailure)
        {
            return row;
        }

        var current = row.Value;
        if (Codes.Parse<DelinquencyState>(current.State) is DelinquencyState.Arranged or DelinquencyState.OnHold)
        {
            return StateError(current);
        }

        await PauseAsync(current, $"Promise to pay by {promiseDate}", cancellationToken).ConfigureAwait(false);
        current.State = Codes.Of(DelinquencyState.Arranged);
        current.PromiseDate = promiseDate;
        AddContact(current, "ARRANGEMENT", "PROMISE_TO_PAY", note ?? $"Promise to pay by {promiseDate}");
        Touch(current);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return current;
    }

    /// <summary>Places (<paramref name="resume"/> false) or lifts a vulnerable-customer hold; a hold pauses the clock and goes to the PSR queue.</summary>
    public async Task<Result<DelinquencyCaseRow>> HoldAsync(Guid caseId, bool resume, string? reason, CancellationToken cancellationToken)
    {
        var row = await FindOpenAsync(caseId, cancellationToken).ConfigureAwait(false);
        if (row.IsFailure)
        {
            return row;
        }

        var current = row.Value;
        var state = Codes.Parse<DelinquencyState>(current.State);
        if (resume)
        {
            if (state is not (DelinquencyState.OnHold or DelinquencyState.Arranged))
            {
                return StateError(current);
            }

            await ResumeAsync(current, reason ?? "Hold released", cancellationToken).ConfigureAwait(false);
            return current;
        }

        if (state == DelinquencyState.OnHold)
        {
            return StateError(current);
        }

        await PauseAsync(current, reason ?? "Vulnerable customer", cancellationToken).ConfigureAwait(false);
        current.State = Codes.Of(DelinquencyState.OnHold);
        current.HoldReason = reason ?? "Vulnerable customer";
        AddContact(current, "HOLD", "HOLD_PLACED", reason);
        Touch(current);
        await billing.RaiseAsync(
            Nonpay.HoldActivity, current.CaseId.ToString("D"), "HOLD",
            $"Delinquency case {current.CaseId} is on hold ({current.HoldReason}); cancellation for non-payment is stopped until a person decides (D-SL6-04).", cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return current;
    }

    public async Task<Result<DelinquencyCaseRow>> RecordContactAsync(Guid caseId, string channel, string outcome, string? note, CancellationToken cancellationToken)
    {
        var row = await db.DelinquencyCases.FirstOrDefaultAsync(c => c.CaseId == caseId && c.LegalEntityId == ledger.LegalEntityId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return BillingErrors.NotFound("delinquency case");
        }

        AddContact(row, channel, outcome, note);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return row;
    }

    /// <summary>
    /// Open amount of the term's invoices due on or before <paramref name="dueOnOrBefore"/> (amount − allocations − credit applied, per item, never negative).
    /// </summary>
    public async Task<decimal> ArrearsAsync(PolicyTermId termId, BusinessDate dueOnOrBefore, CancellationToken cancellationToken)
    {
        var invoices = await db.Invoices.AsNoTracking().Where(i => i.TermId == termId && i.Total > 0m && i.DueDate <= dueOnOrBefore)
            .Select(i => i.InvoiceId).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (invoices.Count == 0)
        {
            return 0m;
        }

        var cancelled = new[] { Codes.Of(InvoiceItemState.Cancelled), Codes.Of(InvoiceItemState.WrittenOff) };
        var items = await db.InvoiceItems.AsNoTracking().Where(i => invoices.Contains(i.InvoiceId) && !cancelled.Contains(i.State))
            .Select(i => new { i.InvoiceItemId, i.Amount }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var ids = items.Select(i => i.InvoiceItemId).ToList();
        var allocated = (await db.Allocations.AsNoTracking().Where(a => ids.Contains(a.InvoiceItemId))
            .GroupBy(a => a.InvoiceItemId).Select(g => new { Item = g.Key, Sum = g.Sum(a => a.Amount) }).ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(a => a.Item, a => a.Sum);
        var credited = (await db.CreditApplications.AsNoTracking().Where(c => c.TargetInvoiceItemId != null && ids.Contains(c.TargetInvoiceItemId.Value))
            .GroupBy(c => c.TargetInvoiceItemId!.Value).Select(g => new { Item = g.Key, Sum = g.Sum(c => c.Amount) }).ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(c => c.Item, c => c.Sum);
        return items.Sum(i => Math.Max(0m, i.Amount - allocated.GetValueOrDefault(i.InvoiceItemId) - credited.GetValueOrDefault(i.InvoiceItemId)));
    }

    private async Task ResolveAsync(DelinquencyCaseRow row, string outcome, DelinquencyResolvedV1.OutcomeValue eventOutcome, bool stopClock, CancellationToken cancellationToken)
    {
        if (stopClock && row.ClockInstanceId is { } instance)
        {
            await services.GetRequiredService<IComplianceClockService>().StopAsync(
                new ClockStopRequest
                {
                    InstanceId = instance,
                    Key = $"nonpay-stop-{row.CaseId:N}",
                    Outcome = ClockStopRequest.OutcomeValue.Met,
                    StopTime = clock.Now,
                },
                CommandOptions.New(),
                cancellationToken).ConfigureAwait(false);
        }

        var now = clock.Now;
        row.State = Codes.Of(DelinquencyState.Resolved);
        row.Outcome = outcome;
        row.ResolvedAt = now;
        Touch(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        Publish(
            DelinquencyResolvedV1.Descriptor,
            new DelinquencyResolvedV1 { ProcessId = row.CaseId, TermId = row.TermId, Outcome = outcome == "ARRANGEMENT_KEPT" ? DelinquencyResolvedV1.OutcomeValue.ArrangementKept : eventOutcome },
            row,
            BusinessKeys.Empty.With("processId", row.CaseId.ToString()).With("policyTermId", row.TermId.Value.ToString()));
    }

    private async Task PauseAsync(DelinquencyCaseRow row, string reason, CancellationToken cancellationToken)
    {
        if (row.ClockInstanceId is { } instance)
        {
            await services.GetRequiredService<IComplianceClockService>().PauseAsync(
                new ClockPauseRequest { InstanceId = instance, Key = $"nonpay-pause-{row.CaseId:N}-{row.RecordVersion}", Reason = reason },
                CommandOptions.New(),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ResumeAsync(DelinquencyCaseRow row, string reason, CancellationToken cancellationToken)
    {
        if (row.ClockInstanceId is { } instance)
        {
            await services.GetRequiredService<IComplianceClockService>().ResumeAsync(
                new ClockResumeRequest { InstanceId = instance, Key = $"nonpay-resume-{row.CaseId:N}-{row.RecordVersion}", Reason = reason },
                CommandOptions.New(),
                cancellationToken).ConfigureAwait(false);
            row.State = Codes.Of(DelinquencyState.ClockRunning);
        }
        else
        {
            row.State = Codes.Of(DelinquencyState.NoticeRequested);
        }

        row.HoldReason = null;
        row.PromiseDate = null;
        AddContact(row, "SYSTEM", "RESUMED", reason);
        Touch(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<DelinquencyCaseRow>> FindOpenAsync(Guid caseId, CancellationToken cancellationToken)
    {
        var row = await db.DelinquencyCases.FirstOrDefaultAsync(c => c.CaseId == caseId && c.LegalEntityId == ledger.LegalEntityId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return BillingErrors.NotFound("delinquency case");
        }

        return Nonpay.OpenStates.Contains(row.State) ? row : StateError(row);
    }

    private static DomainError StateError(DelinquencyCaseRow row) =>
        DomainError.Of(ModuleCode.BIL, "DELINQUENCY-STATE", $"The delinquency case is {row.State}; the action is not allowed in that state.");

    private void AddContact(DelinquencyCaseRow row, string channel, string outcome, string? note) =>
        db.DelinquencyContacts.Add(new DelinquencyContactRow
        {
            ContactId = Guid.CreateVersion7(),
            LegalEntityId = row.LegalEntityId,
            CaseId = row.CaseId,
            Channel = channel,
            Outcome = outcome,
            Note = note,
            RecordedBy = context.Actor.ToString(),
            RecordedAt = clock.Now,
        });

    private void Touch(DelinquencyCaseRow row)
    {
        row.UpdatedAt = clock.Now;
        row.RecordVersion++;
    }

    private static Money MoneyOf(DelinquencyCaseRow row, decimal amount) => new(amount, Currency.FromCode(row.Currency));

    private void Publish(CoreIns.Platform.Contracts.Events.EventContract contract, object payload, DelinquencyCaseRow row, BusinessKeys keys)
    {
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(contract), "BillingAccount", row.BillingAccountId.Value.ToString(), payload,
            keys.With("billingAccountId", row.BillingAccountId.Value.ToString()).With("policyId", row.PolicyId.Value.ToString())) { OccurredAt = clock.Now });
    }
}
