using System.Text.Json;
using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Claims.Authority;
using CoreIns.Modules.Claims.Contracts.Api;
using CoreIns.Modules.Claims.Domain;
using CoreIns.Modules.Claims.Persistence;
using CoreIns.Modules.Claims.Queries;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Authority;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Contracts.Api;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using AuthorityCheckRequest = CoreIns.Platform.Authority.AuthorityCheckRequest;
using Check = CoreIns.Modules.Claims.Contracts.Api.TransactionSetSubmitResponse.AuthorityCheckItem;

namespace CoreIns.Modules.Claims.Commands;

/// <summary><c>clm.TransactionSet.submit</c> as a command of the platform pipeline.</summary>
internal sealed record SubmitTransactionSet(TransactionSetSubmitRequest Request) : ICommand<TransactionSetSubmitResponse>;

/// <summary>Shape rules.</summary>
internal sealed class SubmitTransactionSetValidator : AbstractValidator<SubmitTransactionSet>
{
    public SubmitTransactionSetValidator() => RuleFor(c => c.Request.SetId).NotEmpty();
}

/// <summary>
/// Submits a Draft set (REQ-CLM-108, -109, -112): re-validates it against what it was built on (else CLM-ERR-SET-STALE),
/// checks every transaction's authority with <see cref="IAuthorityService"/> (CLM.RESERVE / CLM.PAYMENT on the amount and
/// cost type; D-SL2-03 illustrative grants). All ALLOW → Approved at once (no four-eyes) with its events and the payment
/// handed to BIL. Any REFER → PendingApproval with one PLT approval request (maker = the submitter, referral role from the
/// check): its subject is the set's payment when it carries one (type CLM.CLAIM_PAYMENT, hash = the disbursement content
/// hash BIL verifies, D-SL2-10 d), else the set (CLM.TRANSACTION_SET, hash = the set content hash). Any DENY →
/// CLM-ERR-AUTHORITY. Racing submits serialise on the claim and set locks; the loser sees a non-Draft set.
/// </summary>
internal sealed class SubmitTransactionSetHandler(
    ClaimsDbContext db,
    RequestContext context,
    IClock clock,
    ClaimProtection protection,
    IAuthorityService authority,
    IPlatformApprovalService approvals,
    SetLifecycle lifecycle,
    FinancialsReader reader) : ICommandHandler<SubmitTransactionSet, TransactionSetSubmitResponse>
{
    public async Task<Result<TransactionSetSubmitResponse>> HandleAsync(SubmitTransactionSet command, CancellationToken cancellationToken)
    {
        var legalEntity = protection.Current(context);
        var setId = new ClaimTransactionSetId(command.Request.SetId);
        if (await FinancialSupport.ClaimOfSetAsync(db, legalEntity, setId, cancellationToken).ConfigureAwait(false) is not { } claimId)
        {
            return ClaimSupport.NotFound("transaction set");
        }

        var claim = (await ClaimSupport.LoadAsync(db, legalEntity, claimId, cancellationToken).ConfigureAwait(false))!;
        var set = (await FinancialSupport.LockSetAsync(db, legalEntity, setId, cancellationToken).ConfigureAwait(false))!;
        if (set.Status != Codes.Of(SetStatus.Draft))
        {
            return DomainError.Of(ModuleCode.CLM, "ILLEGAL-TRANSITION", $"The set is {set.Status}; only a Draft set can be submitted.");
        }

        var content = await FinancialSupport.ContentAsync(db, set, cancellationToken).ConfigureAwait(false);
        if (await lifecycle.RevalidateAsync(claim, content, cancellationToken).ConfigureAwait(false) is { } stale)
        {
            return FinancialSupport.Stale(stale + " Build the set again (REQ-CLM-112).");
        }

        if (await DuplicateAsync(content, cancellationToken).ConfigureAwait(false) is { } duplicate)
        {
            return duplicate;
        }

        // REQ-CLM-108: one authority check per transaction.
        var now = clock.Now;
        var checks = new List<(FinancialTransactionRow Txn, AuthorityCheckResult Check)>();
        foreach (var t in content.Transactions)
        {
            var line = content.Lines[t.ReserveLineId];
            var check = await authority.CheckAsync(
                new AuthorityCheckRequest(
                    context.Actor,
                    context.Roles,
                    TypeOf(t),
                    Dimensions(Math.Abs(t.Amount), t.Currency, line.CostType),
                    new ObjectRef(ModuleCode.CLM, "ClaimFinancialTransaction", t.TxnId.ToString("D")),
                    now),
                cancellationToken).ConfigureAwait(false);
            context.AuthorityChecks.Add(check);
            checks.Add((t, check));
        }

        if (checks.FirstOrDefault(c => c.Check.Decision == AuthorityDecision.Deny) is { Txn: not null } denied)
        {
            return new DomainError(ErrorCode.For(ModuleCode.CLM, "AUTHORITY"), $"{denied.Txn.TxnNumber}: {denied.Check.Type} {denied.Check.ReasonCode}.")
            {
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["txnNumber"] = denied.Txn.TxnNumber, ["authorityCheckId"] = denied.Check.CheckId.ToString(), ["reasonCode"] = denied.Check.ReasonCode,
                },
            };
        }

        set.Submitter = context.Actor.ToString();
        set.SubmittedAt = now;
        set.AuthorityCheckIds = [.. checks.Select(c => c.Check.CheckId.Value)];
        set.UpdatedAt = now;
        var referred = checks.Where(c => c.Check.Decision == AuthorityDecision.Refer).ToList();
        if (referred.Count == 0)
        {
            await lifecycle.ApproveAsync(claim, content, context.Actor.ToString(), UserIdOf(context.Actor), fourEyes: false, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await ReferAsync(claim, content, referred, cancellationToken).ConfigureAwait(false);
        }

        set.RecordVersion++;
        var view = FinancialsReader.View(set, content.Transactions, content.Lines, content.Payments.ToDictionary(p => p.ClaimPaymentId));
        return new TransactionSetSubmitResponse
        {
            SetId = set.SetId.Value,
            Status = set.Status,
            ApprovalRequestId = set.ApprovalRequestId is { } request ? new ApprovalRequestId(request) : null,
            AuthorityChecks = [.. checks.Select(c => new Check
            {
                CheckId = c.Check.CheckId.Value,
                Decision = c.Check.Decision switch { AuthorityDecision.Allow => Check.DecisionValue.Allow, AuthorityDecision.Refer => Check.DecisionValue.Refer, _ => Check.DecisionValue.Deny },
                TxnId = c.Txn.TxnId,
                Type = c.Check.Type.Value,
                ReferralRole = c.Check.ReferralTargets.Count > 0 ? c.Check.ReferralTargets[0].Id : null,
            })],
            Set = view,
            Payments = [.. content.Payments.Select(FinancialsReader.Payment)],
        };
    }

    /// <summary>
    /// PendingApproval with one PLT request (REQ-CLM-109). The request carries the largest referred authority (a payment
    /// first on equal amounts); the slice's grants are the same per type (D-SL2-03), so a checker allowed that amount is
    /// allowed every referred transaction of the set.
    /// </summary>
    private async Task ReferAsync(ClaimRow claim, SetContent content, List<(FinancialTransactionRow Txn, AuthorityCheckResult Check)> referred, CancellationToken cancellationToken)
    {
        var set = content.Set;
        var lead = referred.OrderByDescending(r => Math.Abs(r.Txn.Amount)).ThenByDescending(r => r.Txn.Kind == Codes.Of(TransactionKind.Payment)).First();
        var leadLine = content.Lines[lead.Txn.ReserveLineId];
        var payment = content.Payments.SingleOrDefault();
        var (type, subject, hash) = payment is null
            ? (ClaimApprovals.TransactionSet, ClaimApprovals.SetSubject(set.SetId), Sha256Hash.Parse(set.ContentHash))
            : (DisbursementApproval.ClaimPaymentType, DisbursementApproval.ClaimPaymentSubject(payment.ClaimPaymentId.Value.ToString("D")), Sha256Hash.Parse(payment.DisbursementContentHash));
        var role = lead.Check.ReferralTargets.FirstOrDefault(t => t.Kind == "ROLE")?.Id ?? lead.Check.ReferralTargets[0].Id;
        set.Status = Codes.Of(SetStatus.PendingApproval);
        set.ApprovalType = type;
        set.ApprovalSubjectType = subject.Type;
        set.ApprovalSubjectId = subject.Id;
        set.ApprovalPayloadHash = hash.Value;
        set.ApprovalAuthorityType = lead.Check.Type.Value;
        set.ApprovalAuthorityAmount = Math.Abs(lead.Txn.Amount);
        set.ApprovalAuthorityCostType = leadLine.CostType;
        set.ReferralRole = role;
        foreach (var p in content.Payments)
        {
            p.Status = Codes.Of(PaymentStatus.Pending);
        }

        if (context.DryRun)
        {
            return; // a dry run shows the referral without creating the PLT request
        }

        var approved = await reader.ApprovedAsync(claim.ClaimId, null, cancellationToken).ConfigureAwait(false);
        var diff = JsonSerializer.SerializeToElement(new
        {
            setId = set.SetId.Value,
            claimId = claim.ClaimId.Value,
            claimNumber = claim.ClaimNumber.Value,
            contentHash = set.ContentHash,
            transactions = content.Transactions.Select(t => new
            {
                txnNumber = t.TxnNumber, kind = t.Kind, line = content.KeyOf(t).ToString(), amount = SetHashing.Fixed(t.Amount), t.ReasonCode, t.Proposed,
            }),
            lines = content.Lines.Values.Select(l =>
            {
                var before = approved.GetValueOrDefault(l.ReserveLineId);
                var after = content.Transactions.Where(t => t.ReserveLineId == l.ReserveLineId)
                    .Aggregate(before, (a, t) => a.Apply(Codes.Parse<TransactionKind>(t.Kind), t.Amount, t.Eroding ?? false));
                return new
                {
                    line = SetLifecycle.Key(l).ToString(),
                    openReserveBefore = SetHashing.Fixed(before.OpenReserve), openReserveAfter = SetHashing.Fixed(after.OpenReserve),
                    paidBefore = SetHashing.Fixed(before.Paid), paidAfter = SetHashing.Fixed(after.Paid),
                };
            }),
        });
        var response = await approvals.RequestAsync(
            new ApprovalRequestRequest
            {
                Type = type,
                ObjectRef = subject,
                PayloadHash = hash,
                Authority = new ApprovalAuthority
                {
                    Type = lead.Check.Type.Value,
                    Amount = ClaimMoney.Of(Math.Abs(lead.Txn.Amount), lead.Txn.Currency),
                    Codes = new Dictionary<string, string>(StringComparer.Ordinal) { [ClaimsAuthorityTypes.CostTypeDimension] = leadLine.CostType },
                },
                ReferralRole = role,
                Reason = $"Claim {claim.ClaimNumber.Value}: {referred.Count} transaction(s) above the submitter's authority ({lead.Check.ReasonCode}).",
                Diff = diff,
            },
            new CommandOptions(IdempotencyKey.From(set.SetId.Value)),
            cancellationToken).ConfigureAwait(false);
        set.ApprovalRequestId = response.Request.RequestId;
    }

    private async Task<DomainError?> DuplicateAsync(SetContent content, CancellationToken cancellationToken)
    {
        foreach (var payment in content.Payments)
        {
            var rejected = Codes.Of(PaymentStatus.Rejected);
            var draft = Codes.Of(SetStatus.Draft);
            var exists = await db.ClaimPayments.AsNoTracking()
                .Where(p => p.ClaimId == payment.ClaimId && p.SetId != payment.SetId && p.PayeeAccountId == payment.PayeeAccountId && p.Amount == payment.Amount && p.Status != rejected)
                .Join(db.TransactionSets.AsNoTracking().Where(s => s.Status != draft), p => p.SetId, s => s.SetId, (p, s) => p.ClaimPaymentId)
                .AnyAsync(cancellationToken).ConfigureAwait(false);
            if (exists)
            {
                return DomainError.Of(ModuleCode.CLM, "DUPLICATE-PAYMENT", "A payment of the same amount to the same account exists on this claim (REQ-CLM-129).");
            }
        }

        return null;
    }

    public static AuthorityTypeCode TypeOf(FinancialTransactionRow t) =>
        t.Kind == Codes.Of(TransactionKind.Payment) ? ClaimsAuthorityTypes.Payment : ClaimsAuthorityTypes.Reserve;

    public static Dictionary<string, DimensionValue> Dimensions(decimal amount, string currency, string costType) => new(StringComparer.Ordinal)
    {
        [ClaimsAuthorityTypes.AmountDimension] = DimensionValue.Of(ClaimMoney.Of(amount, currency)),
        [ClaimsAuthorityTypes.CostTypeDimension] = DimensionValue.OfCodes(costType),
    };

    /// <summary>The person's directory object id when the actor is a user with a GUID id (TransactionSetApproved approverUserIds).</summary>
    public static Guid? UserIdOf(ActorRef actor) => actor.Kind == ActorKind.User && Guid.TryParse(actor.Id, out var id) && id != Guid.Empty ? id : null;
}

/// <summary>Audit facts of <c>clm.TransactionSet.submit</c>: status, approval request (the authority checks are recorded by the pipeline).</summary>
internal sealed class SubmitTransactionSetAuditor : ICommandAuditor<SubmitTransactionSet, TransactionSetSubmitResponse>
{
    public CommandAuditFacts Describe(SubmitTransactionSet command, Result<TransactionSetSubmitResponse>? result)
    {
        var subject = ClaimApprovals.SetSubject(new ClaimTransactionSetId(command.Request.SetId));
        if (result is not { IsSuccess: true } ok)
        {
            return new CommandAuditFacts { ObjectRef = subject, BusinessKeys = BusinessKeys.Empty.With("setId", command.Request.SetId.ToString()) };
        }

        return new CommandAuditFacts
        {
            ObjectRef = subject,
            BusinessKeys = BusinessKeys.Empty.With("setId", command.Request.SetId.ToString()).With("claimId", ok.Value.Set!.ClaimId.Value.ToString()),
            Changes = AuditDiff.Compute(new { status = "DRAFT" }, new
            {
                status = ok.Value.Status,
                approvalRequestId = ok.Value.ApprovalRequestId?.Value,
                payments = ok.Value.Payments?.Select(p => new { p.ClaimPaymentId, status = p.Status.ToString(), p.HoldReason, disbursementId = p.DisbursementId?.Value }),
            }),
        };
    }
}
