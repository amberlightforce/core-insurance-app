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
        if (set.EvidenceRef is not null && context.Actor != ActorRef.Service("clm-financial-engine"))
        {
            return DomainError.Of(ModuleCode.CLM, "NOT-AVAILABLE", "System sets may only be submitted by the evidence engine.");
        }
        if (set.Status != Codes.Of(SetStatus.Draft))
        {
            return FinancialSupport.Stale($"The set is {set.Status}; only a Draft set can be submitted.");
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

        // REQ-CLM-108 / D-SL2-13: the set's authority requirements (exposure total reserve, manual decreases, each payment and
        // the claim's cumulative paid), each checked for the submitter.
        var now = clock.Now;
        var claimLines = await reader.LinesAsync(claim.ClaimId, cancellationToken).ConfigureAwait(false);
        var approved = await reader.ApprovedAsync(claim.ClaimId, null, cancellationToken).ConfigureAwait(false);
        var checks = new List<(AuthorityRequirement Requirement, AuthorityCheckResult Check)>();
        foreach (var requirement in SetAuthority.Requirements(content, claimLines, approved))
        {
            var check = await authority.CheckAsync(
                new AuthorityCheckRequest(context.Actor, set.EvidenceRef is null ? context.Roles : ["Staff.ClaimsManager"], requirement.Type, SetAuthority.Dimensions(requirement), ClaimApprovals.SetSubject(set.SetId), now),
                cancellationToken).ConfigureAwait(false);
            check = SetAuthority.RequireFourEyes(requirement, check);
            if (set.EvidenceRef is not null && check.Decision == AuthorityDecision.Allow)
            {
                check = check with { Decision = AuthorityDecision.Refer, ReasonCode = "SYSTEM_SET_REQUIRES_APPROVER", ReferralTargets = [new ReferralTarget("ROLE", "Staff.ClaimsManager")] };
            }
            context.AuthorityChecks.Add(check);
            checks.Add((requirement, check));
        }

        if (checks.FirstOrDefault(c => c.Check.Decision == AuthorityDecision.Deny) is { Requirement: not null } denied)
        {
            return new DomainError(
                ErrorCode.For(ModuleCode.CLM, "AUTHORITY"),
                $"{denied.Check.Type} on {denied.Requirement.Basis} {SetHashing.Fixed(denied.Requirement.Amount)}: {denied.Check.ReasonCode}.")
            {
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["basis"] = denied.Requirement.Basis, ["amount"] = SetHashing.Fixed(denied.Requirement.Amount),
                    ["authorityCheckId"] = denied.Check.CheckId.ToString(), ["reasonCode"] = denied.Check.ReasonCode,
                },
            };
        }

        set.Submitter = context.Actor.ToString();
        set.SubmittedAt = now;
        set.AuthorityCheckIds = [.. checks.Select(c => c.Check.CheckId.Value)];
        set.UpdatedAt = now;
        var referred = checks.Where(c => c.Check.Decision == AuthorityDecision.Refer).ToList();
        var requests = new List<SetApprovalRow>();
        if (referred.Count == 0)
        {
            await lifecycle.ApproveAsync(claim, content, context.Actor.ToString(), UserIdOf(context.Actor), fourEyes: false, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            requests = await ReferAsync(claim, content, referred, approved, cancellationToken).ConfigureAwait(false);
        }

        set.RecordVersion++;
        var view = FinancialsReader.View(set, content.Transactions, content.Lines, content.Payments.ToDictionary(p => p.ClaimPaymentId), requests);
        return new TransactionSetSubmitResponse
        {
            SetId = set.SetId.Value,
            Status = set.Status,
            ApprovalRequestId = set.ApprovalRequestId is { } request ? new ApprovalRequestId(request) : null,
            AuthorityChecks = [.. checks.Select(c => new Check
            {
                CheckId = c.Check.CheckId.Value,
                Decision = c.Check.Decision switch { AuthorityDecision.Allow => Check.DecisionValue.Allow, AuthorityDecision.Refer => Check.DecisionValue.Refer, _ => Check.DecisionValue.Deny },
                Type = c.Check.Type.Value,
                CostType = c.Requirement.CostType,
                Basis = Enum.Parse<Check.BasisValue>(c.Requirement.Basis.Replace("_", string.Empty, StringComparison.Ordinal), ignoreCase: true),
                Amount = SetAuthority.MoneyOf(c.Requirement),
                ReferralRole = c.Check.ReferralTargets.Count > 0 ? c.Check.ReferralTargets[0].Id : null,
            })],
            Set = view,
            Payments = [.. content.Payments.Select(FinancialsReader.Payment)],
        };
    }

    /// <summary>
    /// PendingApproval with one PLT request per referred (authority type, cost type) (D-SL2-13; REQ-CLM-109): each carries
    /// the largest referred amount of its type and cost type (the grants are monotone in the amount, so it dominates the
    /// smaller ones). The payment's request has the payment as subject and the disbursement content hash (BIL verifies it,
    /// D-SL2-10 d); a reserve request has the set (per type and cost type) as subject and the set content hash. The set
    /// executes only when every request is approved.
    /// </summary>
    private async Task<List<SetApprovalRow>> ReferAsync(
        ClaimRow claim, SetContent content, List<(AuthorityRequirement Requirement, AuthorityCheckResult Check)> referred,
        IReadOnlyDictionary<ReserveLineId, LineAmounts> approved, CancellationToken cancellationToken)
    {
        var set = content.Set;
        set.Status = Codes.Of(SetStatus.PendingApproval);
        foreach (var p in content.Payments)
        {
            p.Status = Codes.Of(PaymentStatus.Pending);
        }

        var diff = JsonSerializer.SerializeToElement(new ApprovalDiff
        {
            ContentHash = Sha256Hash.Parse(set.ContentHash),
            Lines = [.. content.Transactions.GroupBy(t => (t.ReserveLineId, t.Kind, t.RecoveryId)).Select(g =>
            {
                var line = content.Lines[g.Key.ReserveLineId];
                var before = approved.GetValueOrDefault(line.ReserveLineId);
                var after = content.Transactions.Where(t => t.ReserveLineId == line.ReserveLineId)
                    .Aggregate(before, (a, t) => a.Apply(Codes.Parse<TransactionKind>(t.Kind), t.Amount, t.Eroding ?? false));
                var recovery = g.Key.Kind == Codes.Of(TransactionKind.RecoveryReserve) || g.Key.Kind == Codes.Of(TransactionKind.Recovery);
                return new ApprovalDiffLine
                {
                    Kind = Codes.Map<TransactionKind, ApprovalDiffLine.KindValue>(Codes.Parse<TransactionKind>(g.Key.Kind)),
                    ExposureId = line.ExposureId, CostType = line.CostType, CostCategory = line.CostCategory, RecoveryId = g.Key.RecoveryId,
                    Before = ClaimMoney.Of(recovery ? before.OpenRecoveryReserve : before.OpenReserve, line.Currency),
                    After = ClaimMoney.Of(recovery ? after.OpenRecoveryReserve : after.OpenReserve, line.Currency),
                    PaidBefore = ClaimMoney.Of(before.Paid, line.Currency), PaidAfter = ClaimMoney.Of(after.Paid, line.Currency),
                    Delta = ClaimMoney.Of(g.Sum(t => t.Amount), line.Currency),
                };
            })],
        }, JsonSerializerOptions.Web);
        var rows = new List<SetApprovalRow>();
        var payment = content.Payments.SingleOrDefault();
        var now = clock.Now;
        foreach (var bucket in SetAuthority.Dominant(referred.Select(r => r.Requirement)))
        {
            var check = referred.Where(r => r.Requirement.Bucket == bucket.Bucket).MaxBy(r => r.Requirement.Amount).Check;
            var role = check.ReferralTargets.FirstOrDefault(t => t.Kind == "ROLE")?.Id ?? check.ReferralTargets[0].Id;
            var (type, subject, hash) = SubjectOf(set, payment, bucket);
            var row = new SetApprovalRow
            {
                SetId = set.SetId, ApprovalType = type, SubjectType = subject.Type, SubjectId = subject.Id, PayloadHash = hash.Value,
                AuthorityType = bucket.Type.Value, AuthorityCostType = bucket.CostType, AuthorityAmount = bucket.Amount, Currency = bucket.Currency,
                Status = "PENDING", LegalEntityId = set.LegalEntityId, Jurisdiction = set.Jurisdiction, CreatedAt = now, CreatedBy = context.Actor.ToString(),
            };
            if (!context.DryRun)
            {
                var response = await approvals.RequestAsync(
                    new ApprovalRequestRequest
                    {
                        Type = type,
                        ObjectRef = subject,
                        PayloadHash = hash,
                        Authority = new ApprovalAuthority
                        {
                            Type = bucket.Type.Value,
                            Amount = SetAuthority.MoneyOf(bucket),
                            Codes = new Dictionary<string, string>(StringComparer.Ordinal) { [ClaimsAuthorityTypes.CostTypeDimension] = bucket.CostType },
                        },
                        ReferralRole = role,
                        Reason = $"Claim {claim.ClaimNumber.Value}: {bucket.Type} {bucket.CostType} {bucket.Basis} {SetHashing.Fixed(bucket.Amount)} is above the submitter's authority ({check.ReasonCode}).",
                        Diff = diff,
                    },
                    CommandOptions.New(),
                    cancellationToken).ConfigureAwait(false);
                row.ApprovalRequestId = response.Request.RequestId;
                db.SetApprovals.Add(row);
            }

            rows.Add(row);
        }

        // The set's primary binding: the payment's request when it has one (BIL's approval evidence), else the first.
        var primary = rows[0];
        set.ApprovalRequestId = context.DryRun ? null : primary.ApprovalRequestId;
        set.ApprovalType = primary.ApprovalType;
        set.ApprovalSubjectType = primary.SubjectType;
        set.ApprovalSubjectId = primary.SubjectId;
        set.ApprovalPayloadHash = primary.PayloadHash;
        set.ApprovalAuthorityType = primary.AuthorityType;
        set.ApprovalAuthorityAmount = primary.AuthorityAmount;
        set.ApprovalAuthorityCostType = primary.AuthorityCostType;
        set.ReferralRole = referred[0].Check.ReferralTargets.FirstOrDefault(t => t.Kind == "ROLE")?.Id;
        return rows;
    }

    /// <summary>The PLT subject and bound hash of one approval bucket (shared with the execution check).</summary>
    public static (string Type, ObjectRef Subject, Sha256Hash Hash) SubjectOf(TransactionSetRow set, ClaimPaymentRow? payment, AuthorityRequirement bucket) =>
        bucket.Type == ClaimsAuthorityTypes.Payment && payment is not null
            ? (DisbursementApproval.ClaimPaymentType, DisbursementApproval.ClaimPaymentSubject(payment.ClaimPaymentId.Value.ToString("D")), Sha256Hash.Parse(payment.DisbursementContentHash))
            : (ClaimApprovals.TransactionSet, new ObjectRef(ModuleCode.CLM, "TransactionSet", $"{set.SetId.Value:D}/{bucket.Type.Value}/{bucket.CostType}"), Sha256Hash.Parse(set.ContentHash));

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
