using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Claims.Contracts.Api;
using CoreIns.Modules.Claims.Domain;
using CoreIns.Modules.Claims.Persistence;
using CoreIns.Modules.Claims.Queries;
using CoreIns.Modules.Claims.Services;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Authority;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Item = CoreIns.Modules.Claims.Contracts.Api.TransactionSetBuildRequest.TransactionItem;

namespace CoreIns.Modules.Claims.Commands;

/// <summary><c>clm.TransactionSet.build</c> as a command of the platform pipeline.</summary>
internal sealed record BuildTransactionSet(TransactionSetBuildRequest Request, ClaimFinancialEvidence? Evidence = null) : ICommand<TransactionSetBuildResponse>;

/// <summary>Shape rules (CLM-ERR-VALIDATION): EUR only (D-SL2-06), minor units, codes, at most 20 transactions.</summary>
internal sealed class BuildTransactionSetValidator : AbstractValidator<BuildTransactionSet>
{
    public BuildTransactionSetValidator()
    {
        RuleFor(c => c.Request.Transactions).NotEmpty().Must(t => t is null || t.Count <= 20).WithMessage("At most 20 transactions per set.");
        RuleForEach(c => c.Request.Transactions).ChildRules(item =>
        {
            item.RuleFor(t => t.Kind).IsInEnum();
            item.RuleFor(t => t.CostType).NotEmpty().Matches("^[A-Z][A-Z0-9_]{0,63}$").WithErrorCode("CODE");
            item.RuleFor(t => t.CostCategory).NotEmpty().Matches("^[A-Z][A-Z0-9_]{0,63}$").WithErrorCode("CODE");
            item.RuleFor(t => t.Amount).Must(a => a.Currency == Currency.EUR).WithErrorCode("CURRENCY").WithMessage("Claim money is EUR only (D-SL2-06).");
            item.RuleFor(t => t.Amount).Must(a => a.IsRoundedToMinorUnits).WithErrorCode("AMOUNT-ROUNDING").WithMessage("The amount must be rounded to minor units.");
            item.RuleFor(t => t.Amount).Must(a => !a.IsZero).WithErrorCode("AMOUNT").WithMessage("The amount cannot be zero.");
            item.RuleFor(t => t.Amount).Must(a => a.IsPositive).When(t => t.Kind == Item.KindValue.Payment).WithErrorCode("AMOUNT").WithMessage("A payment is positive.");
            item.RuleFor(t => t.Amount).Must(a => a.IsPositive).When(t => t.Kind == Item.KindValue.Recovery).WithErrorCode("AMOUNT").WithMessage("Received recovery cash is positive.");
            item.RuleFor(t => t.Reason).Matches("^[A-Z][A-Z0-9_]{0,63}$").When(t => t.Reason is not null).WithErrorCode("CODE");
            item.RuleFor(t => t.PayeePartyId).NotNull().When(t => t.Kind == Item.KindValue.Payment).WithErrorCode("PAYEE_REQUIRED");
            item.RuleFor(t => t.PayeeAccountId).NotNull().When(t => t.Kind == Item.KindValue.Payment).WithErrorCode("PAYEE_ACCOUNT_REQUIRED");
        });
        RuleFor(c => c.Request.Transactions).Must(t => t is null || t.Count(x => x.Kind == Item.KindValue.Payment) <= 1)
            .WithErrorCode("ONE_PAYMENT").WithMessage("The slice builds at most one payment per set.");
    }
}

/// <summary>
/// Builds a Draft transaction set on one open claim (REQ-CLM-003, -107): every transaction on a reserve line (exposure ×
/// cost type × illustrative cost category × EUR, created on first use, REQ-CLM-093), a reason for every manual reserve
/// change (REQ-CLM-098), payments only on an open, covered exposure of a policy in force to a payee account captured on the
/// claim (REQ-CLM-130, -123), no second live payment of the same amount to the same account (REQ-CLM-129, D-SL2-10 a). An
/// eroding payment above the open reserve adds the difference as a reserve increase (REQ-CLM-097, auto-adjust on by
/// default) and a FINAL payment proposes the release of the remainder (REQ-CLM-099), both in the same set. The transactions
/// are written once, immutable and sealed to this transaction (D-ARC-34); numbers per claim (D-SL2-07). Returns the
/// balances before → after per line. A dry run returns the same preview and stores nothing.
/// </summary>
internal sealed class BuildTransactionSetHandler(
    ClaimsDbContext db,
    RequestContext context,
    IClock clock,
    ClaimProtection protection,
    FinancialsReader reader,
    IAuthorityService authority,
    IOptions<ClaimsOptions> options) : ICommandHandler<BuildTransactionSet, TransactionSetBuildResponse>
{
    private sealed record Draft(Item? Source, TransactionKind Kind, LineKey Line, decimal Amount, PaymentType? PaymentType, string? Reason, bool Proposed);

    public async Task<Result<TransactionSetBuildResponse>> HandleAsync(BuildTransactionSet command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = protection.Current(context);
        var claim = await ClaimSupport.LoadAsync(db, legalEntity, request.ClaimId, cancellationToken).ConfigureAwait(false);
        if (claim is null)
        {
            return ClaimSupport.NotFound("claim");
        }

        if (claim.Status != ClaimStates.Open)
        {
            return DomainError.Of(ModuleCode.CLM, "ILLEGAL-TRANSITION", "Financial transactions need an open claim.");
        }

        var settings = options.Value.Financials;
        var exposures = await db.Exposures.AsNoTracking().Where(e => e.ClaimId == claim.ClaimId).ToDictionaryAsync(e => e.ExposureId, cancellationToken).ConfigureAwait(false);
        var drafts = new List<Draft>();
        foreach (var (item, index) in request.Transactions.Select((t, i) => (t, i)))
        {
            var field = $"transactions[{index}]";
            if (item.Kind == Item.KindValue.Recovery && command.Evidence is null)
            {
                return DomainError.Of(ModuleCode.CLM, "NOT-AVAILABLE", "Received cash can only be recorded from authoritative evidence in a system set.");
            }

            if (!exposures.TryGetValue(item.ExposureId, out var exposure))
            {
                return ClaimSupport.NotFound("exposure");
            }

            if (exposure.Status != ClaimStates.Open && item.Kind != Item.KindValue.RecoveryReserve)
            {
                return DomainError.Of(ModuleCode.CLM, "ILLEGAL-TRANSITION", $"Exposure {exposure.ExposureNumber} is not open.");
            }

            if (!Enum.GetValues<CostType>().Any(c => Codes.Of(c) == item.CostType) || !settings.Allows(item.CostType, item.CostCategory))
            {
                return FnolAssessment.Invalid(field + ".costCategory", "COST_CATEGORY", $"{item.CostType}/{item.CostCategory} is not a configured cost type and category (D-SL2-04).");
            }

            var kind = item.Kind switch { Item.KindValue.Payment => TransactionKind.Payment, Item.KindValue.RecoveryReserve => TransactionKind.RecoveryReserve, Item.KindValue.Recovery => TransactionKind.Recovery, _ => TransactionKind.Reserve };
            if ((kind == TransactionKind.Reserve || kind == TransactionKind.RecoveryReserve) && string.IsNullOrWhiteSpace(item.Reason))
            {
                return DomainError.Of(ModuleCode.CLM, "RESERVE-REASON", "A reserve change needs a reason code (REQ-CLM-098).");
            }

            if (kind is TransactionKind.RecoveryReserve or TransactionKind.Recovery)
            {
                if (item.RecoveryId is not { } recoveryId || !await db.Recoveries.AnyAsync(r => r.RecoveryId == recoveryId && r.ClaimId == claim.ClaimId
                        && r.LegalEntityId == legalEntity && (r.ExposureId == null || r.ExposureId == item.ExposureId)
                        && r.Status != "CLOSED" && r.Status != "WRITTEN_OFF", cancellationToken).ConfigureAwait(false))
                {
                    return ClaimSupport.NotFound("open recovery of the claim and exposure");
                }
            }

            if (kind == TransactionKind.Payment)
            {
                // REQ-CLM-058 / D-SL3-03 d: cover removed (or never established) and no coverage decision yet → no new payment.
                if (CoverageGuard.IsInQuestion(claim, exposure))
                {
                    return DomainError.Of(ModuleCode.CLM, "COVERAGE-IN-QUESTION", $"The cover of exposure {exposure.ExposureNumber} is in question; no payment until a coverage decision.");
                }

                var reasons = new List<string>();
                if (claim.PolicyTermId is null || !claim.PolicyInForceAtLoss)
                {
                    reasons.Add("POLICY_NOT_IN_FORCE");
                }

                if (exposure.CoverageIndication != Codes.Of(CoverageIndicationCode.Covered))
                {
                    reasons.Add("EXPOSURE_NOT_COVERED");
                }

                if (!await db.PayeeAccounts.AnyAsync(
                        a => a.ClaimId == claim.ClaimId && a.PayeeAccountId == item.PayeeAccountId!.Value && a.PartyId == item.PayeePartyId!.Value, cancellationToken)
                    .ConfigureAwait(false))
                {
                    reasons.Add("PAYEE_ACCOUNT_NOT_CAPTURED");
                }

                if (reasons.Count > 0)
                {
                    return FinancialSupport.NotPayable([.. reasons]);
                }
            }
            else if (claim.PolicyTermId is null)
            {
                return FinancialSupport.NotPayable("POLICY_NOT_IN_FORCE");
            }

            var paymentType = kind == TransactionKind.Payment
                ? item.PaymentType == Item.PaymentTypeValue.Final ? PaymentType.Final : PaymentType.Partial
                : (PaymentType?)null;
            drafts.Add(new Draft(item, kind, new LineKey(item.ExposureId, item.CostType, item.CostCategory, item.Amount.Currency.Code), item.Amount.Amount, paymentType, item.Reason, false));
        }

        // REQ-CLM-129 / D-SL2-10 a: a live payment of the same amount to the same account on this claim is a duplicate.
        if (drafts.SingleOrDefault(d => d.Kind == TransactionKind.Payment) is { } paymentDraft)
        {
            var account = paymentDraft.Source!.PayeeAccountId!.Value;
            var rejected = Codes.Of(PaymentStatus.Rejected);
            var duplicate = await db.ClaimPayments.AsNoTracking()
                .Where(p => p.ClaimId == claim.ClaimId && p.PayeeAccountId == account && p.Amount == paymentDraft.Amount && p.Status != rejected)
                .Join(db.TransactionSets.AsNoTracking().Where(s => s.Status != Codes.Of(SetStatus.Draft)), p => p.SetId, s => s.SetId, (p, s) => p.ClaimPaymentId)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (duplicate != default)
            {
                return new DomainError(ErrorCode.For(ModuleCode.CLM, "DUPLICATE-PAYMENT"), "A payment of the same amount to the same account exists on this claim (REQ-CLM-129).")
                {
                    Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["existingClaimPaymentId"] = duplicate.Value.ToString() },
                };
            }
        }

        // Balances before, then the set applied; top-up (REQ-CLM-097) and final release (REQ-CLM-099) proposed in the set.
        var existingLines = await db.ReserveLines.Where(l => l.ClaimId == claim.ClaimId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var approved = await reader.ApprovedAsync(claim.ClaimId, null, cancellationToken).ConfigureAwait(false);
        LineAmounts BeforeOf(LineKey key) =>
            existingLines.FirstOrDefault(l => SetLifecycle.Key(l) == key) is { } row ? approved.GetValueOrDefault(row.ReserveLineId) : LineAmounts.Zero;

        var keys = drafts.Select(d => d.Line).Distinct().ToList();
        LineAmounts AfterOf(LineKey key) => drafts.Where(d => d.Line == key).Aggregate(BeforeOf(key), (a, d) => a.Apply(d.Kind, d.Amount, d.Kind == TransactionKind.Payment));

        foreach (var payment in drafts.Where(d => d.Kind == TransactionKind.Payment).ToList())
        {
            var shortfall = -AfterOf(payment.Line).RawOpen;
            if (shortfall > 0m)
            {
                if (!settings.AutoAdjustReserve)
                {
                    return DomainError.Of(ModuleCode.CLM, "PAYMENT-EXCEEDS-RESERVE", $"The payment exceeds the open reserve of its line by {SetHashing.Fixed(shortfall)} (REQ-CLM-097).");
                }

                drafts.Add(new Draft(null, TransactionKind.Reserve, payment.Line, shortfall, null, FinancialReasons.AutoAdjust, true));
            }

            if (payment.PaymentType == PaymentType.Final && AfterOf(payment.Line).RawOpen is var remainder and > 0m)
            {
                drafts.Add(new Draft(null, TransactionKind.Reserve, payment.Line, -remainder, null, FinancialReasons.FinalRelease, true));
            }
        }

        if (keys.FirstOrDefault(k => AfterOf(k).RawOpen < 0m) is { CostType: not null } negative)
        {
            return FnolAssessment.Invalid("transactions", "RESERVE_NEGATIVE", $"The open reserve of line {negative} would fall below zero (REQ-CLM-095).");
        }

        if (drafts.Where(d => d.Kind == TransactionKind.RecoveryReserve && d.Amount < 0m).Any(d => AfterOf(d.Line).RecoveryReserved < BeforeOf(d.Line).Recovered))
        {
            return FnolAssessment.Invalid("transactions", "RECOVERY_RESERVE_NEGATIVE", "A recovery reserve release cannot exceed its open balance.");
        }

        // Rows: lines on first use, the set (header first: it seals the lines, D-ARC-34), transactions and the payment.
        var now = clock.Now;
        var actor = context.Actor.ToString();
        var today = options.Value.DateOf(now);
        var lines = new Dictionary<LineKey, ReserveLineRow>();
        foreach (var key in keys)
        {
            var row = existingLines.FirstOrDefault(l => SetLifecycle.Key(l) == key);
            if (row is null)
            {
                row = new ReserveLineRow
                {
                    ReserveLineId = ReserveLineId.New(), ClaimId = claim.ClaimId, ExposureId = key.ExposureId, CostType = key.CostType, CostCategory = key.CostCategory,
                    Currency = key.Currency, LegalEntityId = legalEntity, Jurisdiction = claim.Jurisdiction, CreatedAt = now, CreatedBy = actor, UpdatedAt = now,
                };
                db.ReserveLines.Add(row);
            }

            lines[key] = row;
        }

        var set = new TransactionSetRow
        {
            SetId = ClaimTransactionSetId.New(), ClaimId = claim.ClaimId, Status = Codes.Of(SetStatus.Draft), LegalEntityId = legalEntity, Jurisdiction = claim.Jurisdiction,
            CreatedAt = now, CreatedBy = actor, UpdatedAt = now,
            EvidenceKind = command.Evidence?.Kind, EvidenceRef = command.Evidence?.Reference,
        };
        var transactions = new List<FinancialTransactionRow>();
        var payments = new List<ClaimPaymentRow>();
        foreach (var draft in drafts)
        {
            claim.LastTransactionSequence++;
            var line = lines[draft.Line];
            ClaimPaymentRow? payment = null;
            if (draft.Kind == TransactionKind.Payment)
            {
                var source = draft.Source!;
                var view = await db.PayeeAccounts.AsNoTracking()
                    .FirstAsync(a => a.ClaimId == claim.ClaimId && a.PayeeAccountId == source.PayeeAccountId!.Value, cancellationToken).ConfigureAwait(false);
                var id = ClaimPaymentId.New();
                var amount = ClaimMoney.Of(draft.Amount, draft.Line.Currency);
                payment = new ClaimPaymentRow
                {
                    ClaimPaymentId = id, ClaimId = claim.ClaimId, SetId = set.SetId, ExposureId = draft.Line.ExposureId, PayeePartyId = source.PayeePartyId!.Value,
                    PayeeAccountId = source.PayeeAccountId!.Value, MaskedAccount = view.MaskedIban, Method = DisbursementCodes.SepaCreditTransfer,
                    PaymentType = Codes.Of(draft.PaymentType!.Value), Amount = draft.Amount, Currency = draft.Line.Currency, Status = Codes.Of(PaymentStatus.Pending),
                    DisbursementContentHash = DisbursementContent.Hash(DisbursementCodes.ClaimPayment, id.Value.ToString("D"), source.PayeePartyId!.Value, source.PayeeAccountId!.Value, amount).Value,
                    LegalEntityId = legalEntity, Jurisdiction = claim.Jurisdiction, CreatedAt = now, CreatedBy = actor, UpdatedAt = now,
                };
                payments.Add(payment);
            }

            transactions.Add(new FinancialTransactionRow
            {
                TxnId = Guid.CreateVersion7(), SetId = set.SetId, ClaimId = claim.ClaimId, ReserveLineId = line.ReserveLineId, ExposureId = draft.Line.ExposureId,
                Sequence = claim.LastTransactionSequence, TxnNumber = $"{claim.ClaimNumber.Value}-T{claim.LastTransactionSequence:D4}", Kind = Codes.Of(draft.Kind),
                Amount = draft.Amount, Currency = draft.Line.Currency, FunctionalAmount = draft.Amount, FunctionalCurrency = draft.Line.Currency,
                GroupAmount = draft.Amount, GroupCurrency = draft.Line.Currency, Eroding = draft.Kind == TransactionKind.Payment ? true : null,
                PaymentType = draft.PaymentType is { } type ? Codes.Of(type) : null, ClaimPaymentId = payment?.ClaimPaymentId, ReasonCode = draft.Reason,
                Proposed = draft.Proposed, TransactionDate = today, LegalEntityId = legalEntity, Jurisdiction = claim.Jurisdiction, CreatedAt = now, CreatedBy = actor,
                RecoveryId = draft.Source?.RecoveryId,
            });
        }

        var content = new SetContent(set, transactions, lines.Values.ToDictionary(l => l.ReserveLineId), payments);
        set.ContentHash = SetHashing.Content(set.SetId, claim.ClaimId, content.Canonical()).Value;
        set.BasisHash = SetLifecycle.Basis(
            claim,
            keys.Select(k => k.ExposureId).Distinct().Select(e => (e, exposures[e].Status)),
            existingLines.Select(l => (SetLifecycle.Key(l), approved.GetValueOrDefault(l.ReserveLineId)))).Value;
        db.TransactionSets.Add(set);
        db.FinancialTransactions.AddRange(transactions);
        db.ClaimPayments.AddRange(payments);
        claim.UpdatedAt = now;

        List<AuthorityPreviewItem>? authorityPreview = null;
        if (context.DryRun)
        {
            authorityPreview = [];
            foreach (var requirement in SetAuthority.Requirements(content, existingLines, approved))
            {
                var check = await authority.CheckAsync(new AuthorityCheckRequest(context.Actor, context.Roles, requirement.Type,
                    SetAuthority.Dimensions(requirement), ClaimApprovals.SetSubject(set.SetId), now), cancellationToken).ConfigureAwait(false);
                check = SetAuthority.RequireFourEyes(requirement, check);
                authorityPreview.Add(new AuthorityPreviewItem
                {
                    Type = requirement.Type.Value, CostType = requirement.CostType, Amount = SetAuthority.MoneyOf(requirement),
                    Basis = Enum.Parse<AuthorityPreviewItem.BasisValue>(requirement.Basis.Replace("_", string.Empty, StringComparison.Ordinal), true),
                    Outcome = check.Decision switch { AuthorityDecision.Allow => AuthorityPreviewItem.OutcomeValue.Within, AuthorityDecision.Refer => AuthorityPreviewItem.OutcomeValue.Refer, _ => AuthorityPreviewItem.OutcomeValue.Deny },
                    Role = check.ReferralTargets.FirstOrDefault()?.Id,
                });
            }
        }

        return new TransactionSetBuildResponse
        {
            SetId = set.SetId.Value,
            Status = set.Status,
            ContentHash = Sha256Hash.Parse(set.ContentHash),
            Preview = [.. keys.Select(k => new TransactionSetBuildResponse.PreviewItem
            {
                ExposureId = k.ExposureId, CostType = k.CostType, CostCategory = k.CostCategory,
                Before = ClaimMoney.Of(BeforeOf(k).OpenReserve, k.Currency), After = ClaimMoney.Of(AfterOf(k).OpenReserve, k.Currency),
                PaidBefore = ClaimMoney.Of(BeforeOf(k).Paid, k.Currency), PaidAfter = ClaimMoney.Of(AfterOf(k).Paid, k.Currency),
            })],
            Checks = [],
            AuthorityPreview = authorityPreview,
            Set = FinancialsReader.View(set, transactions, content.Lines, payments.ToDictionary(p => p.ClaimPaymentId)),
        };
    }
}

/// <summary>Audit facts of <c>clm.TransactionSet.build</c>: the set, its hash and amounts (no payee data).</summary>
internal sealed class BuildTransactionSetAuditor : ICommandAuditor<BuildTransactionSet, TransactionSetBuildResponse>
{
    public CommandAuditFacts Describe(BuildTransactionSet command, Result<TransactionSetBuildResponse>? result)
    {
        var keys = BusinessKeys.Empty.With("claimId", command.Request.ClaimId.Value.ToString());
        if (result is not { IsSuccess: true } ok)
        {
            return new CommandAuditFacts { ObjectRef = ObjectRef.For(ModuleCode.CLM, "Claim", command.Request.ClaimId), BusinessKeys = keys };
        }

        return new CommandAuditFacts
        {
            ObjectRef = ClaimApprovals.SetSubject(new ClaimTransactionSetId(ok.Value.SetId)),
            BusinessKeys = keys.With("setId", ok.Value.SetId.ToString()),
            Changes = AuditDiff.Compute(null, new
            {
                status = ok.Value.Status,
                contentHash = ok.Value.ContentHash?.Value,
                transactions = ok.Value.Set!.Transactions.Select(t => new { t.TxnNumber, kind = t.Kind.ToString(), t.CostType, t.CostCategory, amount = t.Amount.ToString(), t.ReasonCode, t.Proposed }),
            }),
        };
    }
}
