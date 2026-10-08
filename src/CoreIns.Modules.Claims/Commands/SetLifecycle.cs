using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Claims.Contracts.Events;
using CoreIns.Modules.Claims.Domain;
using CoreIns.Modules.Claims.Persistence;
using CoreIns.Modules.Claims.Queries;
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Claims.Commands;

/// <summary>
/// The shared steps of a set's lifecycle (PRD-07 §7.3.2): re-validation against what the set was built on (REQ-CLM-112),
/// approval (state, line flags, <c>TransactionSetApproved</c> and one <c>ReserveChanged</c> per reserve-line delta in the
/// claim's event order, then each payment Approved → Submitted through <c>bil.Disbursement.request</c>, REQ-CLM-004) and
/// rejection (<c>TransactionSetRejected</c>). All in the caller's transaction; the caller holds the claim and set locks.
/// </summary>
internal sealed partial class SetLifecycle(
    ClaimsDbContext db,
    RequestContext context,
    IClock clock,
    IEventPublisher events,
    FinancialsReader reader,
    IOptions<ClaimsOptions> options,
    IServiceProvider services,
    ILogger<SetLifecycle> logger)
{
    /// <summary>Null when nothing changed since the set was built; otherwise the stale reason (REQ-CLM-112).</summary>
    public async Task<string?> RevalidateAsync(ClaimRow claim, SetContent content, CancellationToken cancellationToken)
    {
        if (claim.Status != ClaimStates.Open)
        {
            return "The claim is not open any more.";
        }

        var exposureIds = content.Transactions.Select(t => t.ExposureId).Distinct().ToList();
        var exposures = await db.Exposures.AsNoTracking().Where(e => exposureIds.Contains(e.ExposureId)).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (exposures.Any(e => e.Status != ClaimStates.Open))
        {
            return "An exposure of the set is not open any more.";
        }

        var contentHash = SetHashing.Content(content.Set.SetId, claim.ClaimId, content.Canonical());
        if (contentHash.Value != content.Set.ContentHash)
        {
            return "The set content does not match its content hash.";
        }

        var approved = await reader.ApprovedAsync(claim.ClaimId, null, cancellationToken).ConfigureAwait(false);
        var basis = Basis(claim, exposures.Select(e => (e.ExposureId, e.Status)), (await reader.LinesAsync(claim.ClaimId, cancellationToken).ConfigureAwait(false)).Select(l => (Key(l), approved.GetValueOrDefault(l.ReserveLineId))));
        return basis.Value == content.Set.BasisHash ? null : "The balances of the set's lines changed since it was built.";
    }

    public static Sha256Hash Basis(ClaimRow claim, IEnumerable<(ExposureId, string)> exposures, IEnumerable<(LineKey, LineAmounts)> lines) =>
        SetHashing.Basis(claim.Status, exposures, lines);

    public static LineKey Key(ReserveLineRow line) => new(line.ExposureId, line.CostType, line.CostCategory, line.Currency);

    /// <summary>Approves the set: state, line flags, events, then the disbursement request of each payment (skipped in a dry run).</summary>
    public async Task ApproveAsync(ClaimRow claim, SetContent content, string approver, Guid? approverUserId, bool fourEyes, CancellationToken cancellationToken)
    {
        var set = content.Set;
        var now = clock.Now;
        var accountingDate = options.Value.DateOf(now);
        var before = await reader.ApprovedAsync(claim.ClaimId, null, cancellationToken).ConfigureAwait(false);

        set.Status = Codes.Of(SetStatus.Approved);
        set.ApprovedAt = now;
        set.DecidedAt = now;
        set.Approver = approver;
        set.ApproverUserId = approverUserId;
        set.FourEyes = fourEyes;
        set.UpdatedAt = now;
        set.RecordVersion++;
        foreach (var payment in content.Payments)
        {
            payment.Status = Codes.Of(PaymentStatus.Approved);
            payment.UpdatedAt = now;
            payment.RecordVersion++;
        }

        // Line states (PRD-07 §7.3.9): a final payment makes the line FinalLine; a reserve increase reopens it.
        foreach (var (lineId, line) in content.Lines)
        {
            var own = content.Transactions.Where(t => t.ReserveLineId == lineId).ToList();
            var final = own.Any(t => t.PaymentType == Codes.Of(PaymentType.Final))
                        || (line.FinalFlag && !own.Any(t => t.Kind == Codes.Of(TransactionKind.Reserve) && t.Amount > 0m));
            if (final != line.FinalFlag)
            {
                line.FinalFlag = final;
                line.UpdatedAt = now;
                line.RecordVersion++;
            }
        }

        var claimKey = claim.ClaimId.Value.ToString();
        var reserves = content.Transactions.Where(t => t.Kind == Codes.Of(TransactionKind.Reserve)).ToList();
        var paymentsTotal = content.Transactions.Where(t => t.Kind == Codes.Of(TransactionKind.Payment)).Sum(t => t.Amount);
        List<KindTotal> totals = [];
        if (reserves.Count > 0)
        {
            totals.Add(new KindTotal { Kind = Codes.Of(TransactionKind.Reserve), Amount = ClaimMoney.Eur(reserves.Sum(t => t.Amount)) });
        }

        if (paymentsTotal != 0m)
        {
            totals.Add(new KindTotal { Kind = Codes.Of(TransactionKind.Payment), Amount = ClaimMoney.Eur(paymentsTotal) });
        }

        events.Publish(new OutgoingEvent(
            EventDescriptor.From(TransactionSetApprovedV1.Descriptor),
            ClaimEvents.AggregateType,
            claimKey,
            new TransactionSetApprovedV1
            {
                SetId = set.SetId.Value,
                TransactionIds = [.. content.Transactions.Select(t => new PolicyTransactionId(t.TxnId))],
                TotalsByKind = totals,
                ApproverUserIds = approverUserId is { } user ? [UserId.From(user)] : [],
                AuthorityCheckIds = [.. set.AuthorityCheckIds.Select(c => new AuthorityCheckId(c))],
                FourEyes = fourEyes,
            },
            BusinessKeys.Empty.With("claimId", claimKey).With("setId", set.SetId.Value.ToString())));

        // One ReserveChanged per reserve-line delta (REQ-CLM-005); newOpenAmount is the line's open reserve after the whole set.
        foreach (var group in reserves.GroupBy(t => t.ReserveLineId).OrderBy(g => g.Min(t => t.Sequence)))
        {
            var line = content.Lines[group.Key];
            var after = content.Transactions.Where(t => t.ReserveLineId == group.Key)
                .Aggregate(before.GetValueOrDefault(group.Key), (a, t) => a.Apply(Codes.Parse<TransactionKind>(t.Kind), t.Amount, t.Eroding ?? false));
            var delta = group.Sum(t => t.Amount);
            if (delta == 0m)
            {
                continue;
            }

            events.Publish(new OutgoingEvent(
                EventDescriptor.From(ReserveChangedV1.Descriptor),
                ClaimEvents.AggregateType,
                claimKey,
                new ReserveChangedV1
                {
                    ClaimId = claim.ClaimId,
                    ExposureId = line.ExposureId,
                    ReserveLineId = line.ReserveLineId.Value,
                    ReserveLine = new ReserveLineKey { CostType = line.CostType, Category = line.CostCategory },
                    Kind = ReserveChangedV1.KindValue.Reserve,
                    Delta = Three(delta, line.Currency),
                    NewOpenAmount = Three(after.OpenReserve, line.Currency),
                    SetId = set.SetId.Value,
                    AccidentDate = claim.LossDate,
                    PolicyTermId = claim.PolicyTermId ?? throw new InvalidOperationException("A claim with financials has a policy term."),
                    ProductCode = claim.ProductCode,
                    SiiLob = options.Value.UnmappedSiiLob,
                    Ifrs17GroupRef = null,
                    CatCode = null,
                    HandlingSegment = claim.HandlingSegment,
                    AccountingDate = accountingDate,
                },
                BusinessKeys.Empty.With("claimId", claimKey).With("exposureId", line.ExposureId.Value.ToString())
                    .With("setId", set.SetId.Value.ToString()).With("policyTermId", claim.PolicyTermId.Value.Value.ToString())));
        }

        if (!context.DryRun)
        {
            foreach (var payment in content.Payments)
            {
                await RequestDisbursementAsync(claim, set, payment, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Rejects the set (reason code), its payments with it, and publishes <c>TransactionSetRejected</c>.</summary>
    public void Reject(ClaimRow claim, SetContent content, string reason)
    {
        var now = clock.Now;
        var set = content.Set;
        set.Status = Codes.Of(SetStatus.Rejected);
        set.RejectionReason = reason;
        set.DecidedAt = now;
        set.UpdatedAt = now;
        set.RecordVersion++;
        foreach (var payment in content.Payments)
        {
            payment.Status = Codes.Of(PaymentStatus.Rejected);
            payment.UpdatedAt = now;
            payment.RecordVersion++;
        }

        events.Publish(new OutgoingEvent(
            EventDescriptor.From(TransactionSetRejectedV1.Descriptor),
            ClaimEvents.AggregateType,
            claim.ClaimId.Value.ToString(),
            new TransactionSetRejectedV1 { SetId = set.SetId.Value, Reason = reason },
            BusinessKeys.Empty.With("claimId", claim.ClaimId.Value.ToString()).With("setId", set.SetId.Value.ToString())));
    }

    /// <summary>
    /// Approved → Submitted: <c>bil.Disbursement.request</c> in process (REQ-CLM-004), idempotent on the claim payment id,
    /// source id = the claim payment id (= <c>PaymentIssued.paymentId</c>, D-SL2-12 b), evidence = the PLT approval request
    /// when referred, else the set; the content hash BIL recomputes (<see cref="DisbursementContent.Hash"/>). A BIL refusal
    /// (sanctions, VoP, cooling-off, duplicate, approval) puts the payment On hold with the reason; never a 500.
    /// </summary>
    private async Task RequestDisbursementAsync(ClaimRow claim, TransactionSetRow set, ClaimPaymentRow payment, CancellationToken cancellationToken)
    {
        var now = clock.Now;
        var evidence = set.ApprovalRequestId is { } requestId && set.ApprovalType == DisbursementApproval.ClaimPaymentType
            ? ClaimApprovals.EvidenceOf(new ApprovalRequestId(requestId))
            : ClaimApprovals.EvidenceOf(set.SetId);
        payment.ApprovalEvidenceRef = evidence;
        var sourceId = payment.ClaimPaymentId.Value.ToString("D");
        var amount = ClaimMoney.Of(payment.Amount, payment.Currency);
        var request = new DisbursementRequestRequest
        {
            SourceType = DisbursementCodes.ClaimPayment,
            SourceId = sourceId,
            PayeePartyId = payment.PayeePartyId,
            PayeeAccountId = payment.PayeeAccountId,
            Amount = amount,
            Method = payment.Method,
            ApprovalEvidenceRef = evidence,
            ApprovalContentHash = DisbursementContent.Hash(DisbursementCodes.ClaimPayment, sourceId, payment.PayeePartyId, payment.PayeeAccountId, amount),
            ClaimId = claim.ClaimId,
            PurposeText = $"Claim {claim.ClaimNumber.Value}",
        };

        if (services.GetService<IBillingDisbursementService>() is not { } disbursements)
        {
            Hold(payment, "DISBURSEMENT_UNAVAILABLE", now);
            return;
        }

        try
        {
            var response = await disbursements.RequestAsync(request, new CommandOptions(IdempotencyKey.From(payment.ClaimPaymentId.Value)), cancellationToken)
                .ConfigureAwait(false);
            payment.Status = Codes.Of(PaymentStatus.Submitted);
            payment.DisbursementId = response.DisbursementId;
            payment.SubmittedAt = now;
            payment.HoldReason = null;
            payment.UpdatedAt = now;
        }
        catch (DomainException ex)
        {
            LogRefused(logger, payment.ClaimPaymentId.Value, ex.Error.Code.Value);
            Hold(payment, HoldReasonOf(ex.Error.Code), now);
        }
    }

    private static void Hold(ClaimPaymentRow payment, string reason, Instant now)
    {
        payment.Status = Codes.Of(PaymentStatus.OnHold);
        payment.HoldReason = reason;
        payment.UpdatedAt = now;
    }

    /// <summary>BIL refusals as hold reasons (REQ-CLM-133, -134, -131; D-SL2-10 a).</summary>
    public static string HoldReasonOf(ErrorCode code) => code.Name switch
    {
        "PAYEE-BLOCKED" => "SANCTIONS",
        "SCREENING-UNAVAILABLE" => "LISTS_STALE",
        "VOP-HOLD" => "VOP",
        "COOLING-OFF" => "COOLING_OFF",
        "DUPLICATE" => "DUPLICATE",
        "APPROVAL-MISMATCH" => "APPROVAL_MISMATCH",
        _ => code.Name.Replace('-', '_'),
    };

    public static MoneyByCurrency3 Three(decimal amount, string currency)
    {
        var money = ClaimMoney.Of(amount, currency);
        return new MoneyByCurrency3 { Transaction = money, Functional = money, Group = money };
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "BIL refused the disbursement of claim payment {ClaimPaymentId} ({Code}); the payment is on hold.")]
    private static partial void LogRefused(ILogger logger, Guid claimPaymentId, string code);
}
