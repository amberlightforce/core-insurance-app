using System.Text.Json;
using CoreIns.Modules.Market.Contracts;
using CoreIns.Modules.Market.Contracts.Api;
using CoreIns.Modules.Policy.Commands.Cancellation;
using CoreIns.Modules.Policy.Commands.Suspension;
using CoreIns.Modules.Policy.Contracts;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Modules.Policy.Contracts.Events;
using CoreIns.Modules.Policy.Domain;
using CoreIns.Modules.Policy.Domain.Servicing;
using CoreIns.Modules.Policy.Persistence;
using CoreIns.Modules.Policy.Services;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts.Common;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.Platform.Numbering;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CoreIns.Modules.Policy.Commands.Reinstatement;

/// <summary>The two kinds of reinstatement (SCR-POL-15): with or without a lapse in cover.</summary>
internal static class ReinstatementTypes
{
    public const string NoLapse = "NO_LAPSE";

    public const string WithLapse = "WITH_LAPSE";

    /// <summary>The type of the request, <c>null</c> when it is not one of the two codes.</summary>
    public static string? Parse(JsonElement? type)
    {
        if (type is not { ValueKind: JsonValueKind.String } t)
        {
            return null;
        }

        var code = t.GetString()?.Trim().ToUpperInvariant();
        return code is NoLapse or WithLapse ? code : null;
    }
}

/// <summary><c>pol.Reinstatement.create</c>: re-open a cancelled term inside the reinstatement window, with or without a lapse in cover (BO-POL-07).</summary>
internal sealed record ReinstatePolicy(ReinstatementCreateRequest Request) : ICommand<ReinstatementCreateResponse>;

internal sealed class ReinstatePolicyValidator : AbstractValidator<ReinstatePolicy>
{
    public ReinstatePolicyValidator()
    {
        RuleFor(c => c.Request.TermId).NotNull().WithErrorCode("TERM_REQUIRED");
        RuleFor(c => c.Request.Type).Must(t => ReinstatementTypes.Parse(t) is not null).WithErrorCode("TYPE_UNKNOWN");
        RuleFor(c => c.Request.Reason).Must(SuspensionRequestChecks.IsReason).WithErrorCode("REASON_REQUIRED");
    }
}

/// <summary>
/// Reinstates a cancelled term. In the command's transaction and under the policy write lock: the term is Cancelled and its head is the
/// cancellation, the reinstatement window (<see cref="PolicyOptions.ReinstatementWindowDays"/> after the cancellation date) has not ended,
/// no successor term exists. The term version is superseded by an InForce one (<c>cancelled_at</c> cleared, same term id: the original term
/// is re-opened), a Reinstatement transaction carries the charge deltas that net the cancellation credits out (reversed in full without
/// lapse; pro rata to the days still covered with a lapse) and a new segment covers the reinstated period. The deltas leave as one
/// complete <c>ChargeDeltaEmitted</c> set with <c>PolicyReinstated</c> through the outbox, once.
/// </summary>
internal sealed class ReinstatePolicyHandler(
    PolicyDbContext db,
    RequestContext context,
    ILegalEntityDirectory legalEntities,
    IClock clock,
    INumberingService numbering,
    IEventPublisher events,
    Dependency<IMarketRoundingService> rounding,
    IOptions<PolicyOptions> options) : ICommandHandler<ReinstatePolicy, ReinstatementCreateResponse>
{
    private static readonly TimeSpan Skew = TimeSpan.FromMinutes(5);

    public async Task<Result<ReinstatementCreateResponse>> HandleAsync(ReinstatePolicy command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var type = ReinstatementTypes.Parse(request.Type)!;
        var reason = SuspensionRequestChecks.ReasonText(request.Reason);
        var zone = options.Value.Zone;
        var termId = request.TermId!.Value;
        var legalEntity = JobSupport.LegalEntity(context, legalEntities);

        var policyIds = await db.Terms.AsNoTracking().Where(t => t.TermId == termId && t.LegalEntityId == legalEntity && t.RecordedTo == null)
            .Select(t => t.PolicyId).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (policyIds.Count == 0)
        {
            return JobSupport.NotFound("term");
        }

        var policyId = policyIds[0];
        var locked = await PolicyWriteLock.AcquireAsync(db, clock, options.Value.LockWait, legalEntity, policyId, cancellationToken).ConfigureAwait(false);
        if (locked.IsFailure)
        {
            return locked.Error!;
        }

        var t = locked.Value;
        var term = await db.Terms.SingleOrDefaultAsync(x => x.TermId == termId && x.LegalEntityId == legalEntity && x.RecordedTo == null, cancellationToken).ConfigureAwait(false);
        if (term is null)
        {
            return JobSupport.NotFound("term");
        }

        var terms = await db.Terms.AsNoTracking().Where(x => x.PolicyId == policyId && x.RecordedTo == null).ToListAsync(cancellationToken).ConfigureAwait(false);
        var transactions = await db.Transactions.AsNoTracking().Where(x => x.PolicyId == policyId).OrderBy(x => x.Sequence).ToListAsync(cancellationToken).ConfigureAwait(false);
        var termTransactions = transactions.Where(x => x.TermId == term.TermId).ToList();
        var storedState = Codes.Parse<PolicyTermState>(term.State);
        var cancellation = termTransactions.LastOrDefault();
        if (storedState != PolicyTermState.Cancelled || term.CancelledAt is not { } cancelledAt
            || cancellation is null || Codes.Parse<PolicyTransactionKind>(cancellation.Kind) != PolicyTransactionKind.Cancellation)
        {
            return DomainError.Of(ModuleCode.POL, "ILLEGAL-TRANSITION", $"A {storedState} term cannot be reinstated: only a cancelled term can.");
        }

        if (terms.Any(x => x.PredecessorTermId == term.TermId))
        {
            return DomainError.Of(ModuleCode.POL, "ILLEGAL-TRANSITION", "A renewal term exists; a cancelled term with a successor cannot be reinstated.");
        }

        var fired = PolicyTermStateModel.Machine.Fire(storedState, PolicyTermTrigger.Reinstate);
        if (fired.IsFailure)
        {
            return DomainError.Of(ModuleCode.POL, "ILLEGAL-TRANSITION", $"A {storedState} term cannot be reinstated.");
        }

        // The window: whole business days (Athens) from the cancellation date to the record time.
        var window = options.Value.ReinstatementWindowDays;
        if (DayCount.Days(cancelledAt, t, zone) > window)
        {
            return DomainError.Of(ModuleCode.POL, "RIGHT-EXPIRED", $"The reinstatement window of {window} days after the cancellation has ended.");
        }

        var lapse = type == ReinstatementTypes.WithLapse;
        var coverFrom = cancelledAt;
        if (lapse)
        {
            if (request.EffectiveAt is not { } given || given <= cancelledAt || given >= term.ValidTo || given > t.Plus(Skew))
            {
                return EffectiveAtRefusal("A reinstatement with a lapse needs an effective time after the cancellation, inside the term and not in the future.");
            }

            coverFrom = given;
        }
        else if (request.EffectiveAt is { } stated && stated != cancelledAt)
        {
            return EffectiveAtRefusal("A reinstatement without a lapse starts at the cancellation time; leave the effective time out or give that time.");
        }

        // ---- the charge deltas that net the cancellation out ---------------------------------------------------------
        var credits = await db.ChargeLines.AsNoTracking().Where(l => l.TransactionId == cancellation.TransactionId).OrderBy(l => l.SetIndex).ToListAsync(cancellationToken).ConfigureAwait(false);
        var currency = Currency.FromCode(term.Currency.Trim());
        var reinstateDate = coverFrom.ToBusinessDate(zone);
        var deltas = new List<(ChargeLineRow Credit, decimal Amount, BusinessDate From)>();
        foreach (var credit in credits.Where(c => c.Amount != 0m))
        {
            var amount = -credit.Amount;
            var validFrom = credit.ValidFrom;
            if (lapse)
            {
                // Pro rata to the days still covered: the credit paid back [ValidFrom, ValidTo); the lapse days stay credited.
                var total = credit.ValidTo.Value.DayNumber - credit.ValidFrom.Value.DayNumber;
                var covered = credit.ValidTo.Value.DayNumber - Math.Max(credit.ValidFrom.Value.DayNumber, reinstateDate.Value.DayNumber);
                if (covered <= 0 || total <= 0)
                {
                    continue;
                }

                var rounded = await RoundAsync(amount * covered / total, currency, reinstateDate, cancellationToken).ConfigureAwait(false);
                if (rounded.IsFailure)
                {
                    return rounded.Error!;
                }

                amount = rounded.Value;
                validFrom = reinstateDate.Value > credit.ValidFrom.Value ? reinstateDate : credit.ValidFrom;
            }

            if (amount != 0m)
            {
                deltas.Add((credit, amount, validFrom));
            }
        }

        // ---- the rows --------------------------------------------------------------------------------------------------
        var policy = await db.Policies.AsNoTracking().SingleAsync(p => p.PolicyId == policyId && p.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        var segments = await db.Segments.AsNoTracking().Where(s => s.TermId == term.TermId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var template = segments.OrderByDescending(s => s.ValidFrom).ThenByDescending(s => s.RecordedFrom).FirstOrDefault();
        if (template is null)
        {
            return DomainError.Of(ModuleCode.POL, "GATE-FAILED", "The term has no segment to reinstate. Nothing was changed.");
        }

        var actor = context.Actor.ToString();
        var booking = t.ToBusinessDate(zone);
        var transactionId = PolicyTransactionId.New();
        var sequence = (transactions.Count == 0 ? 0 : transactions[^1].Sequence) + 1;
        var premium = deltas.Where(d => !TermHead.IsTaxLike(d.Credit.ChargeCategory)).Sum(d => d.Amount);
        var taxes = deltas.Where(d => TermHead.IsTaxLike(d.Credit.ChargeCategory)).Sum(d => d.Amount);

        // First close what is superseded (the exclusion constraints forbid overlapping record periods), then insert.
        term.RecordedTo = t;
        var closed = await SaveAsync(cancellationToken).ConfigureAwait(false);
        if (closed.IsFailure)
        {
            return closed.Error!;
        }

        db.Terms.Add(new PolicyTermRow
        {
            TermVersionId = Guid.CreateVersion7(), TermId = term.TermId, PolicyId = term.PolicyId, LegalEntityId = term.LegalEntityId, TermNumber = term.TermNumber,
            ValidFrom = term.ValidFrom, ValidTo = term.ValidTo, RecordedFrom = t, State = Codes.Of(fired.Value),
            ProductVersion = term.ProductVersion, ArtefactHash = term.ArtefactHash, RatingArtefactHash = term.RatingArtefactHash,
            ResolutionHash = term.ResolutionHash, ConfigurationHash = term.ConfigurationHash, Currency = term.Currency,
            ProducerCode = term.ProducerCode, PaymentPlanRef = term.PaymentPlanRef, WrittenDate = term.WrittenDate,
            HeadTransactionId = transactionId, CreatedBy = actor, PredecessorTermId = term.PredecessorTermId, CancelledAt = null, SuspendedFrom = term.SuspendedFrom,
        });

        var jobId = JobId.New();
        var jobNumber = await numbering.NextAsync(new NumberRequest(NumberingSchemes.Job, booking), cancellationToken).ConfigureAwait(false);
        var intent = JobSupport.Json(new { type, reason, effectiveAt = coverFrom, reinstates = cancellation.TransactionId, cancelledAt });
        db.Transactions.Add(new PolicyTransactionRow
        {
            TransactionId = transactionId, PolicyId = term.PolicyId, TermId = term.TermId, JobId = jobId, LegalEntityId = legalEntity,
            Kind = Codes.Of(PolicyTransactionKind.Reinstatement), Sequence = sequence, EffectiveAt = coverFrom, RecordedAt = t,
            ConfigurationHash = term.ConfigurationHash, ArtefactHash = term.ArtefactHash, RatingArtefactHash = term.RatingArtefactHash,
            ResolutionHash = term.ResolutionHash, Intent = intent, Premium = premium, Taxes = taxes, Total = premium + taxes,
            Currency = term.Currency, Actor = actor, CorrelationId = context.CorrelationId.ToString(), Origin = context.Origin.ToCode(),
        });

        // The cover resumes from the reinstated time to the end of the term, on the risk the cancelled term carried.
        db.Segments.Add(new SegmentRow
        {
            SegmentId = SegmentId.New(), TermId = term.TermId, PolicyId = term.PolicyId, TransactionId = transactionId, LegalEntityId = legalEntity,
            ValidFrom = coverFrom, ValidTo = term.ValidTo, RecordedFrom = t,
            SnapshotHash = template.SnapshotHash, Snapshot = template.Snapshot, WorksheetId = template.WorksheetId,
        });

        var frozen = new List<ChargeLine>();
        for (var i = 0; i < deltas.Count; i++)
        {
            var (credit, amount, validFrom) = deltas[i];
            var chargeId = ChargeId.New();
            var money = new Money(amount, currency);
            db.ChargeLines.Add(new ChargeLineRow
            {
                ChargeId = chargeId, TransactionId = transactionId, TermId = term.TermId, PolicyId = term.PolicyId, LegalEntityId = legalEntity,
                ElementLocator = credit.ElementLocator, CoverageCode = credit.CoverageCode, ChargeType = credit.ChargeType, ChargeCategory = credit.ChargeCategory,
                DeltaKind = DeltaKinds.Net, AnnualRate = credit.AnnualRate, Amount = amount, Currency = term.Currency,
                ValidFrom = validFrom, ValidTo = credit.ValidTo, BookingDate = booking, CorrelationKey = transactionId.Value.ToString(),
                SetIndex = i + 1, SetSize = deltas.Count, RecordedAt = t, TaxTreatmentRef = credit.TaxTreatmentRef, LegalStatus = credit.LegalStatus,
                Provisional = credit.Provisional, TransactionKind = Codes.Of(TaxTransactionKind.Reinstatement),
                TreatmentRuleId = credit.TreatmentRuleId, TreatmentRuleVersion = credit.TreatmentRuleVersion,
            });
            frozen.Add(new ChargeLine
            {
                ChargeId = chargeId, TransactionId = transactionId, ElementLocator = credit.ElementLocator, CoverageCode = credit.CoverageCode,
                ChargeType = credit.ChargeType, ChargeCategory = credit.ChargeCategory, AnnualRate = credit.AnnualRate, Amount = money,
                LegalStatus = credit.LegalStatus, Provisional = credit.Provisional,
            });
            events.Publish(new OutgoingEvent(
                EventDescriptor.From(ChargeDeltaEmittedV1.Descriptor), "Policy", policyId.Value.ToString(),
                new ChargeDeltaEmittedV1
                {
                    ChargeId = chargeId, TermId = term.TermId, ElementLocator = credit.ElementLocator, CoverageCode = credit.CoverageCode, ChargeType = credit.ChargeType,
                    ChargeCategory = credit.ChargeCategory, DeltaKind = DeltaKinds.Net, NetAmount = money,
                    ValidPeriod = new DateRange(validFrom, credit.ValidTo), BookingDate = booking, TransactionId = transactionId,
                    CorrelationKey = transactionId.Value.ToString(), LegalStatus = credit.LegalStatus, Provisional = credit.Provisional,
                    TransactionKind = ChargeDeltaEmittedV1.TransactionKindValue.Reinstatement,
                },
                BusinessKeys.Empty.With("policyId", policyId.Value.ToString()).With("chargeId", chargeId.Value.ToString())
                    .With("policyTermId", term.TermId.Value.ToString()).With("transactionId", transactionId.Value.ToString()))
            {
                OccurredAt = t,
                Set = new EventSet(transactionId.Value, deltas.Count, i + 1),
            });
        }

        events.Publish(new OutgoingEvent(
            EventDescriptor.From(PolicyReinstatedV1.Descriptor), "Policy", policyId.Value.ToString(),
            new PolicyReinstatedV1
            {
                TransactionId = transactionId, TermId = term.TermId, Reason = reason,
                Gap = lapse
                    ? new CoverGap { Kind = CoverGap.KindValue.Period, Period = InstantRange.Of(cancelledAt, coverFrom) }
                    : new CoverGap { Kind = CoverGap.KindValue.None },
            },
            BusinessKeys.Empty.With("policyId", policyId.Value.ToString()).With("policyTermId", term.TermId.Value.ToString())
                .With("transactionId", transactionId.Value.ToString()).With("jobId", jobId.Value.ToString()))
        {
            OccurredAt = t,
        });

        var quoted = JobStateModel.Machine.Fire(JobState.Draft, JobTrigger.Quote).Value;
        var boundState = JobStateModel.Machine.Fire(quoted, JobTrigger.Bind).Value;
        db.Jobs.Add(new JobRow
        {
            JobId = jobId, LegalEntityId = legalEntity, Jurisdiction = policy.Jurisdiction.Trim(), JobNumber = JobNumber.Parse(jobNumber.Value),
            JobType = Codes.Of(JobType.Reinstatement), State = Codes.Of(boundState), PolicyId = policy.PolicyId,
            PolicyholderPartyId = policy.PolicyholderPartyId, AccountId = policy.AccountId, ProductCode = policy.ProductCode, ProductVersion = term.ProductVersion,
            ArtefactHash = term.ArtefactHash, RatingArtefactHash = term.RatingArtefactHash, ResolutionHash = term.ResolutionHash, Channel = context.Channel ?? "STAFF",
            ProducerCode = term.ProducerCode, QuoteType = "FULL", EffectiveAt = coverFrom, ExpirationAt = term.ValidTo, Currency = term.Currency,
            CurrentVersionNo = 1, BoundTransactionId = transactionId, RecordVersion = 1, CreatedAt = t, CreatedBy = actor, Participants = [actor], UpdatedAt = t,
            TargetTermId = term.TermId, ReasonCode = reason.Length > 64 ? reason[..64] : reason, BaseTransactionId = cancellation.TransactionId,
        });
        db.QuoteVersions.Add(new QuoteVersionRow
        {
            QuoteId = QuoteId.New(), JobId = jobId, LegalEntityId = legalEntity, VersionNo = 1, State = Codes.Of(QuoteState.Quoted), DraftVersion = 0,
            RiskTree = template.Snapshot, Bindable = true, Charges = JobSupport.Json(frozen), Premium = premium, Taxes = taxes, Total = premium + taxes,
            QuotedAt = t, ValidUntil = t, RecordVersion = 1, CreatedAt = t, UpdatedAt = t,
        });

        var saved = await SaveAsync(cancellationToken).ConfigureAwait(false);
        return saved.IsFailure ? saved.Error! : new ReinstatementCreateResponse { JobId = jobId };
    }

    private static DomainError EffectiveAtRefusal(string message) =>
        new(ErrorCode.For(ModuleCode.POL, PolicyErrorNames.EffdateLimit), message)
        {
            FieldErrors = [new FieldError("effectiveAt", "EFFECTIVE_AT_INVALID", "EFFECTIVE_AT_INVALID")],
        };

    private async Task<Result<decimal>> RoundAsync(decimal amount, Currency currency, BusinessDate validAt, CancellationToken cancellationToken)
    {
        var response = await rounding.Value.ApplyAsync(
            new RoundingApplyRequest
            {
                Amount = new Money(amount, currency), Currency = currency, Purpose = "charge.line",
                Context = new RoundingApplyRequest.ContextDetail { LegalEntity = context.LegalEntity!.Value.Value, ValidAt = validAt },
            }, cancellationToken).ConfigureAwait(false);
        return response.AmountAfterRounding is { } money && money.Currency == currency
            ? money.Amount
            : DomainError.Of(ModuleCode.POL, "RATING", "MKT rounding returned no amount for a reinstatement charge.");
    }

    private async Task<Result<bool>> SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return JobSupport.Stale();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.ExclusionViolation })
        {
            return JobSupport.Stale();
        }
    }
}

/// <summary>Audit facts of <c>pol.Reinstatement.create</c>.</summary>
internal sealed class ReinstatePolicyAuditor : ICommandAuditor<ReinstatePolicy, ReinstatementCreateResponse>
{
    public CommandAuditFacts Describe(ReinstatePolicy command, Result<ReinstatementCreateResponse>? result)
    {
        var facts = new CommandAuditFacts { ObjectRef = ObjectRef.For(ModuleCode.POL, "Term", command.Request.TermId ?? default) };
        return result is { IsSuccess: true } success
            ? facts with
            {
                BusinessKeys = BusinessKeys.Empty.With("jobId", success.Value.JobId?.Value.ToString() ?? string.Empty).With("termId", command.Request.TermId.ToString() ?? string.Empty),
                Changes = AuditDiff.Compute(null, new { type = ReinstatementTypes.Parse(command.Request.Type), effectiveAt = command.Request.EffectiveAt?.ToString() }),
            }
            : facts;
    }
}
