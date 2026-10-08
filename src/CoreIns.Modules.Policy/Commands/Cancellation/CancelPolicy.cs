using System.Globalization;
using CoreIns.Modules.Market.Contracts;
using CoreIns.Modules.Market.Contracts.Api;
using CoreIns.Modules.Market.Contracts.Spi;
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

namespace CoreIns.Modules.Policy.Commands.Cancellation;

internal sealed class CancelPolicyValidator : AbstractValidator<CancelPolicy>
{
    public CancelPolicyValidator()
    {
        RuleFor(c => c.Request.PolicyId.Value).NotEmpty().WithErrorCode("POLICY_REQUIRED");
        RuleFor(c => c.Request.Source).NotEmpty().MaximumLength(64).WithErrorCode("SOURCE_REQUIRED");
        RuleFor(c => c.Request.ReasonCode).NotEmpty().MaximumLength(64).WithErrorCode("REASON_REQUIRED");
        RuleFor(c => c.Request.Kind).Must(k => CancellationText.TryKind(k, out _)).WithErrorCode("KIND_UNKNOWN");
    }
}

/// <summary>
/// <c>pol.Cancellation.create</c>: policyholder cancellation "now" (REQ-POL-205, -208, -209) or flat cancellation of a Scheduled
/// term (REQ-POL-217). The quote and the bind are the same computation (dry run equals real): with <c>?dryRun=true</c> the
/// whole command runs and rolls back, so the preview is exactly what a real call writes and nothing survives (REQ-POL-071).
/// <para>
/// Real run, in one database transaction, in this order (D-SL3-03 lock-then-stamp):
/// <list type="number">
/// <item>lock the policy and take the single record time <c>t</c> (<see cref="PolicyWriteLock"/>);</item>
/// <item>under the lock: the term is not cancelled or expired (<c>POL-ERR-ILLEGAL-TRANSITION</c>), the effective time is
/// not earlier than the term's latest bound transaction (<c>POL-ERR-OUT-OF-SEQUENCE</c>, in-sequence check);</item>
/// <item>the servicing engine ends cover at the effective time and returns NET credit deltas per element × coverage × charge
/// type (REQ-POL-214); every tax or levy line goes through <c>TaxCalculator.treatment</c> (REQ-POL-215): an IPT line kept by
/// the authority is a 0.00 delta, provisional until the D2 opinion; any other treatment, a missing rule or a refused
/// provisional value fails the whole cancellation closed;</item>
/// <item>the current term version and the covering segments close at <c>t</c>; a new term version (Cancelled, <c>cancelled_at</c>),
/// a Cancellation transaction, the shortened segment and the charge lines are written, all stamped exactly <c>t</c>;</item>
/// <item><c>PolicyCancelled</c> and the complete <c>ChargeDeltaEmitted</c> set go through the outbox in the same transaction.</item>
/// </list>
/// </para>
/// </summary>
internal sealed class CancelPolicyHandler(
    PolicyDbContext db,
    RequestContext context,
    ILegalEntityDirectory legalEntities,
    IClock clock,
    INumberingService numbering,
    IEventPublisher events,
    Dependency<IMarketRoundingService> rounding,
    Dependency<ITaxCalculator> taxCalculator,
    ICancellationRefundMethods refundMethods,
    IOptions<PolicyOptions> options) : ICommandHandler<CancelPolicy, CancellationResponse>
{
    private const string BusinessBasis = "ESTABLISHMENT";

    public async Task<Result<CancellationResponse>> HandleAsync(CancelPolicy command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var zone = options.Value.Zone;
        var now = PolicyWriteLock.Truncate(clock.Now);
        CancellationText.TryKind(request.Kind, out var kind);

        // ---- request checks that need no data (fail closed) ----------------------------------------------------------
        if (!CancellationSources.All.Contains(request.Source, StringComparer.Ordinal))
        {
            return Validation("source", "SOURCE_UNKNOWN", $"'{request.Source}' is not a cancellation source of the code list.");
        }

        if (refundMethods.Resolve(request.Source) is not { } artefactMethod)
        {
            return Validation("source", "SOURCE_NOT_AVAILABLE", $"Cancellation by source {request.Source} is not available in this release (no refund method is declared for it).");
        }

        if (request.Source != CancellationSources.Policyholder)
        {
            return Validation("source", "SOURCE_NOT_AVAILABLE", $"Cancellation by source {request.Source} is not available in this release.");
        }

        if (kind == CancellationKind.Standard && request.EffectiveAt is { } requested)
        {
            if (requested > now)
            {
                return DomainError.Of(ModuleCode.POL, PolicyErrorNames.EffdateLimit, "Scheduled cancellation is not available in this release: cancel now, or leave the effective time empty (REQ-POL-209).");
            }

            if (requested < now)
            {
                return DomainError.Of(ModuleCode.POL, PolicyErrorNames.EffdateLimit, "A cancellation cannot be backdated: the effective time is the request time (0 days back).");
            }
        }

        var legalEntity = JobSupport.LegalEntity(context, legalEntities);

        // ---- the policy lock and the record time ---------------------------------------------------------------------
        var recordTime = await PolicyWriteLock.AcquireAsync(db, clock, options.Value.LockWait, legalEntity, request.PolicyId, cancellationToken).ConfigureAwait(false);
        if (recordTime.IsFailure)
        {
            return recordTime.Error!;
        }

        var t = recordTime.Value;

        // ---- the head of the policy, under the lock ------------------------------------------------------------------
        var policy = await db.Policies.AsNoTracking().SingleAsync(p => p.PolicyId == request.PolicyId && p.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        var terms = await db.Terms.Where(x => x.PolicyId == request.PolicyId && x.LegalEntityId == legalEntity && x.RecordedTo == null).ToListAsync(cancellationToken).ConfigureAwait(false);
        var picked = PickTerm(terms, kind, now);
        if (picked.IsFailure)
        {
            return picked.Error!;
        }

        var (term, effective) = picked.Value;
        var applied = kind == CancellationKind.Flat ? RefundMethod.FullRefund : artefactMethod;

        var storedState = Codes.Parse<PolicyTermState>(term.State);
        var derived = DerivedState(storedState, term, effective);
        var fired = PolicyTermStateModel.Machine.Fire(derived, PolicyTermTrigger.Cancel);
        if (fired.IsFailure)
        {
            return DomainError.Of(ModuleCode.POL, CancellationErrors.IllegalTransition, $"A {derived} term cannot be cancelled.");
        }

        var transactions = await db.Transactions.AsNoTracking().Where(x => x.PolicyId == request.PolicyId).OrderBy(x => x.Sequence).ToListAsync(cancellationToken).ConfigureAwait(false);
        var termTransactions = transactions.Where(x => x.TermId == term.TermId).ToList();
        var lines = await db.ChargeLines.AsNoTracking().Where(x => x.TermId == term.TermId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var currency = Currency.FromCode(term.Currency.Trim());
        var engine = new ServicingEngine(new ReferenceProration(), Rounding(currency, effective.ToBusinessDate(zone)));

        var servicingTerm = new ServicingTerm(term.ValidFrom, term.ValidTo, currency, DayCountConvention.TermRatio, zone);
        var transactionId = PolicyTransactionId.New();
        var correlation = new DeltaCorrelation(transactionId.Value.ToString(), transactionId.Value.ToString());

        ServicingResult result;
        ServicingState state;
        try
        {
            var head = TermHead.Reconstruct(engine, servicingTerm, termTransactions, lines, correlation);
            if (head.IsFailure)
            {
                return head.Error!;
            }

            state = head.Value;
            result = engine.Apply(state, new EndCoverIntent(effective, applied), correlation);
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }

        if (!result.IsAccepted)
        {
            return Refusal(result);
        }

        // ---- tax and levy lines: the MKT treatment decides (REQ-POL-215) -----------------------------------------------
        var taxLines = await TreatTaxLinesAsync(policy, lines, request.Source, effective, zone, cancellationToken).ConfigureAwait(false);
        if (taxLines.IsFailure)
        {
            return taxLines.Error!;
        }

        var premiumDeltas = result.Deltas;
        var treated = taxLines.Value;

        // ---- the rows --------------------------------------------------------------------------------------------------
        var actor = context.Actor.ToString();
        var booking = t.ToBusinessDate(zone);
        var termEndDate = DayCount.Date(term.ValidTo, zone);
        var effectiveDate = DayCount.Date(effective, zone);
        var includeTax = termEndDate > effectiveDate;
        var taxToWrite = includeTax ? treated : [];
        var count = premiumDeltas.Count + taxToWrite.Count;
        var premium = premiumDeltas.Sum(d => d.Amount);
        var taxes = taxToWrite.Sum(x => x.Amount);
        var sequence = (transactions.Count == 0 ? 0 : transactions[^1].Sequence) + 1;

        // First close what is superseded (the exclusion constraints forbid overlapping record periods), then insert.
        var currentSegments = await db.Segments.Where(s => s.TermId == term.TermId && s.RecordedTo == null).ToListAsync(cancellationToken).ConfigureAwait(false);
        term.RecordedTo = t;
        foreach (var segment in currentSegments)
        {
            segment.RecordedTo = t;
        }

        var closed = await SaveAsync(true, cancellationToken).ConfigureAwait(false);
        if (closed.IsFailure)
        {
            return closed.Error!;
        }

        var cancelledVersion = new PolicyTermRow
        {
            TermVersionId = Guid.CreateVersion7(), TermId = term.TermId, PolicyId = term.PolicyId, LegalEntityId = term.LegalEntityId, TermNumber = term.TermNumber,
            ValidFrom = term.ValidFrom, ValidTo = term.ValidTo, RecordedFrom = t, State = Codes.Of(fired.Value),
            ProductVersion = term.ProductVersion, ArtefactHash = term.ArtefactHash, RatingArtefactHash = term.RatingArtefactHash,
            ResolutionHash = term.ResolutionHash, ConfigurationHash = term.ConfigurationHash, Currency = term.Currency,
            ProducerCode = term.ProducerCode, PaymentPlanRef = term.PaymentPlanRef, WrittenDate = term.WrittenDate,
            HeadTransactionId = transactionId, CreatedBy = actor, PredecessorTermId = term.PredecessorTermId, CancelledAt = effective,
        };
        db.Terms.Add(cancelledVersion);

        var jobId = JobId.New();
        var jobNumber = await numbering.NextAsync(new NumberRequest(NumberingSchemes.Job, now.ToBusinessDate(zone)), cancellationToken).ConfigureAwait(false);
        var preview = Preview(currency, lines, premiumDeltas, treated, includeTax);
        var intent = JobSupport.Json(new
        {
            kind = Codes.Of(kind), source = request.Source, reasonCode = request.ReasonCode, refundMethod = CancellationText.Code(applied),
            effectiveAt = effective, notices = CancellationText.NoticesNotSent, refundDue = preview.RefundDue,
        });
        db.Transactions.Add(new PolicyTransactionRow
        {
            TransactionId = transactionId, PolicyId = term.PolicyId, TermId = term.TermId, JobId = jobId, LegalEntityId = legalEntity,
            Kind = Codes.Of(PolicyTransactionKind.Cancellation), Sequence = sequence, EffectiveAt = effective, RecordedAt = t,
            ConfigurationHash = term.ConfigurationHash, ArtefactHash = term.ArtefactHash, RatingArtefactHash = term.RatingArtefactHash,
            ResolutionHash = term.ResolutionHash, Intent = intent, Premium = premium, Taxes = taxes, Total = premium + taxes,
            Currency = term.Currency, Actor = actor, CorrelationId = context.CorrelationId.ToString(), Origin = context.Origin.ToCode(),
        });

        // The risk snapshot is unchanged by a cancellation; cover simply stops at the effective time.
        string? snapshotJson = null;
        foreach (var segment in currentSegments.OrderBy(s => s.ValidFrom))
        {
            snapshotJson ??= segment.Snapshot;
            if (segment.ValidFrom >= effective)
            {
                continue;
            }

            db.Segments.Add(new SegmentRow
            {
                SegmentId = SegmentId.New(), TermId = term.TermId, PolicyId = term.PolicyId, TransactionId = transactionId, LegalEntityId = legalEntity,
                ValidFrom = segment.ValidFrom, ValidTo = Instant.Min(segment.ValidTo, effective), RecordedFrom = t,
                SnapshotHash = segment.SnapshotHash, Snapshot = segment.Snapshot, WorksheetId = segment.WorksheetId,
            });
        }

        var index = 0;
        var frozen = new List<ChargeLine>();
        foreach (var delta in premiumDeltas)
        {
            index++;
            var chargeId = ChargeId.New();
            var credit = new Money(delta.Amount, currency);
            db.ChargeLines.Add(new ChargeLineRow
            {
                ChargeId = chargeId, TransactionId = transactionId, TermId = term.TermId, PolicyId = term.PolicyId, LegalEntityId = legalEntity,
                ElementLocator = delta.Key.ElementLocator, CoverageCode = delta.Key.CoverageCode, ChargeType = delta.Key.ChargeType,
                ChargeCategory = delta.ChargeCategory, DeltaKind = DeltaKinds.Net, AnnualRate = RateOf(state, delta.Key), Amount = delta.Amount,
                Currency = term.Currency, ValidFrom = new BusinessDate(delta.DateFrom), ValidTo = new BusinessDate(delta.DateTo), BookingDate = booking,
                CorrelationKey = transactionId.Value.ToString(), SetIndex = index, SetSize = count, RecordedAt = t,
                TransactionKind = Codes.Of(Domain.TaxTransactionKind.Cancellation), CancellationSource = request.Source,
            });
            frozen.Add(new ChargeLine
            {
                ChargeId = chargeId, TransactionId = transactionId, ElementLocator = delta.Key.ElementLocator, CoverageCode = delta.Key.CoverageCode,
                ChargeType = delta.Key.ChargeType, ChargeCategory = delta.ChargeCategory, AnnualRate = RateOf(state, delta.Key), Amount = credit,
            });
            Emit(policy, term, transactionId, chargeId, delta.Key, delta.ChargeCategory, credit, new DateRange(new BusinessDate(delta.DateFrom), new BusinessDate(delta.DateTo)), booking, null, index, count, t);
        }

        foreach (var line in taxToWrite)
        {
            index++;
            var chargeId = ChargeId.New();
            var treatmentRef = $"{line.Treatment.RuleId}/{line.Treatment.RuleVersion}/{line.Treatment.Action}";
            db.ChargeLines.Add(new ChargeLineRow
            {
                ChargeId = chargeId, TransactionId = transactionId, TermId = term.TermId, PolicyId = term.PolicyId, LegalEntityId = legalEntity,
                ElementLocator = line.Key.ElementLocator, CoverageCode = line.Key.CoverageCode, ChargeType = line.Key.ChargeType,
                ChargeCategory = line.ChargeCategory, DeltaKind = DeltaKinds.Net, AnnualRate = line.AnnualRate, Amount = line.Amount,
                Currency = term.Currency, ValidFrom = new BusinessDate(effectiveDate), ValidTo = new BusinessDate(termEndDate), BookingDate = booking,
                CorrelationKey = transactionId.Value.ToString(), SetIndex = index, SetSize = count, RecordedAt = t, TaxTreatmentRef = treatmentRef,
                LegalStatus = line.Treatment.LegalStatus, Provisional = line.Treatment.Provisional,
                TransactionKind = Codes.Of(Domain.TaxTransactionKind.Cancellation), CancellationSource = request.Source,
                TreatmentRuleId = line.Treatment.RuleId, TreatmentRuleVersion = line.Treatment.RuleVersion,
            });
            frozen.Add(new ChargeLine
            {
                ChargeId = chargeId, TransactionId = transactionId, ElementLocator = line.Key.ElementLocator, CoverageCode = line.Key.CoverageCode,
                ChargeType = line.Key.ChargeType, ChargeCategory = line.ChargeCategory, AnnualRate = line.AnnualRate,
                Amount = new Money(line.Amount, currency), LegalStatus = line.Treatment.LegalStatus, Provisional = line.Treatment.Provisional,
            });
            Emit(
                policy, term, transactionId, chargeId, line.Key, line.ChargeCategory, new Money(line.Amount, currency),
                new DateRange(new BusinessDate(effectiveDate), new BusinessDate(termEndDate)), booking,
                (treatmentRef, line.Treatment.LegalStatus, line.Treatment.Provisional), index, count, t);
        }

        events.Publish(new OutgoingEvent(
            EventDescriptor.From(PolicyCancelledV1.Descriptor), "Policy", policy.PolicyId.Value.ToString(),
            new PolicyCancelledV1
            {
                TransactionId = transactionId, TermId = term.TermId, Source = request.Source, Reason = request.ReasonCode,
                EffectiveDate = effective.ToBusinessDate(zone), RefundMethod = CancellationText.Code(applied),
            },
            BusinessKeys.Empty.With("policyId", policy.PolicyId.Value.ToString()).With("policyTermId", term.TermId.Value.ToString())
                .With("transactionId", transactionId.Value.ToString()).With("jobId", jobId.Value.ToString()))
        {
            OccurredAt = t,
        });

        // ---- the job: Draft → Quoted → Bound in one step (the quote is deterministic) --------------------------------------
        var draft = JobState.Draft;
        var quotedState = JobStateModel.Machine.Fire(draft, JobTrigger.Quote).Value;
        var boundState = JobStateModel.Machine.Fire(quotedState, JobTrigger.Bind).Value;
        db.Jobs.Add(new JobRow
        {
            JobId = jobId, LegalEntityId = legalEntity, Jurisdiction = policy.Jurisdiction.Trim(), JobNumber = JobNumber.Parse(jobNumber.Value),
            JobType = Codes.Of(JobType.Cancellation), State = Codes.Of(boundState), PolicyId = policy.PolicyId, PolicyholderPartyId = policy.PolicyholderPartyId,
            AccountId = policy.AccountId, ProductCode = policy.ProductCode, ProductVersion = term.ProductVersion, ArtefactHash = term.ArtefactHash,
            RatingArtefactHash = term.RatingArtefactHash, ResolutionHash = term.ResolutionHash, Channel = context.Channel ?? "STAFF",
            ProducerCode = term.ProducerCode, QuoteType = "FULL", EffectiveAt = effective, ExpirationAt = term.ValidTo, Currency = term.Currency,
            CurrentVersionNo = 1, BoundTransactionId = transactionId, RecordVersion = 1, CreatedAt = t, CreatedBy = actor, Participants = [actor], UpdatedAt = t,
            TargetTermId = term.TermId, CancellationSource = request.Source, CancellationKind = Codes.Of(kind), RefundMethod = Codes.Of(applied),
            ReasonCode = request.ReasonCode, BaseTransactionId = termTransactions.Count == 0 ? null : termTransactions[^1].TransactionId,
        });
        db.QuoteVersions.Add(new QuoteVersionRow
        {
            QuoteId = QuoteId.New(), JobId = jobId, LegalEntityId = legalEntity, VersionNo = 1, State = Codes.Of(QuoteState.Quoted), DraftVersion = 0,
            RiskTree = snapshotJson ?? JobSupport.Json(RiskTrees.Empty), Bindable = true, Charges = JobSupport.Json(frozen), Premium = premium,
            Taxes = taxes, Total = premium + taxes, QuotedAt = t, ValidUntil = t, RecordVersion = 1, CreatedAt = t, UpdatedAt = t,
        });

        var saved = await SaveAsync(true, cancellationToken).ConfigureAwait(false);
        if (saved.IsFailure)
        {
            return saved.Error!;
        }

        return new CancellationResponse
        {
            JobId = jobId, JobNumber = jobNumber.Value, State = Codes.Api(boundState).ToString(), PolicyId = policy.PolicyId, TermId = term.TermId,
            TermNumber = term.TermNumber, TermState = Codes.Api(fired.Value).ToString(), TransactionId = transactionId, Source = request.Source,
            ReasonCode = request.ReasonCode, Kind = Codes.Of(kind), RefundMethod = CancellationText.Code(applied), EffectiveAt = effective, RecordedAt = t,
            RefundPreview = preview, Notices = CancellationText.NoticesNotSent,
        };
    }

    /// <summary>The annual rate of the key's last segment.</summary>
    private static decimal RateOf(ServicingState state, ChargeKey key) =>
        state.Segments.Where(s => s.Key == key).OrderBy(s => s.From).Last().Rate.AnnualRate;

    private void Emit(
        PolicyRow policy, PolicyTermRow term, PolicyTransactionId transactionId, ChargeId chargeId, ChargeKey key, string category, Money amount,
        DateRange period, BusinessDate booking, (string Ref, string Status, bool Provisional)? treatment, int index, int count, Instant t) =>
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(ChargeDeltaEmittedV1.Descriptor), "Policy", policy.PolicyId.Value.ToString(),
            new ChargeDeltaEmittedV1
            {
                ChargeId = chargeId, TermId = term.TermId, ElementLocator = key.ElementLocator, CoverageCode = key.CoverageCode, ChargeType = key.ChargeType,
                ChargeCategory = category, DeltaKind = DeltaKinds.Net, NetAmount = amount, ValidPeriod = period, BookingDate = booking,
                TransactionId = transactionId, CorrelationKey = transactionId.Value.ToString(),
                TaxTreatmentRef = treatment?.Ref, LegalStatus = treatment?.Status, Provisional = treatment?.Provisional,
            },
            BusinessKeys.Empty.With("policyId", policy.PolicyId.Value.ToString()).With("chargeId", chargeId.Value.ToString())
                .With("policyTermId", term.TermId.Value.ToString()).With("transactionId", transactionId.Value.ToString()))
        {
            OccurredAt = t,
            Set = new EventSet(transactionId.Value, count, index),
        });

    /// <summary>The term a cancellation applies to and its effective instant.</summary>
    private static Result<(PolicyTermRow Term, Instant Effective)> PickTerm(IReadOnlyList<PolicyTermRow> terms, CancellationKind kind, Instant now)
    {
        if (kind == CancellationKind.Flat)
        {
            // REQ-POL-217: flat cancellation = from the term start, only while the term is Scheduled (not yet started).
            var scheduled = terms.Where(x => Codes.Parse<PolicyTermState>(x.State) == PolicyTermState.Scheduled && x.ValidFrom > now).OrderBy(x => x.ValidFrom).FirstOrDefault();
            return scheduled is null
                ? DomainError.Of(ModuleCode.POL, CancellationErrors.IllegalTransition, "A flat cancellation needs a Scheduled term that has not started (REQ-POL-217).")
                : (scheduled, scheduled.ValidFrom);
        }

        var covering = terms.Where(x => x.ValidFrom <= now && now < x.ValidTo).OrderBy(x => x.TermNumber).FirstOrDefault();
        if (covering is not null)
        {
            return (covering, now);
        }

        if (terms.Any(x => x.ValidFrom > now))
        {
            return DomainError.Of(ModuleCode.POL, PolicyErrorNames.EffdateLimit, "The term has not started: cancel it flat (kind Flat) from its start.");
        }

        return DomainError.Of(ModuleCode.POL, CancellationErrors.IllegalTransition, "The policy has no term in force: its last term has expired.");
    }

    /// <summary>The state the lifecycle sees: Scheduled/InForce are derived from the period, Expired from the end (REQ-POL-131).</summary>
    private static PolicyTermState DerivedState(PolicyTermState stored, PolicyTermRow term, Instant at)
    {
        if (stored is PolicyTermState.Scheduled or PolicyTermState.InForce)
        {
            stored = at < term.ValidFrom ? PolicyTermState.Scheduled : PolicyTermState.InForce;
        }

        return stored == PolicyTermState.InForce && at >= term.ValidTo ? PolicyTermState.Expired : stored;
    }

    private async Task<Result<IReadOnlyList<TreatedTaxLine>>> TreatTaxLinesAsync(
        PolicyRow policy, IReadOnlyList<ChargeLineRow> lines, string source, Instant effective, TimeZoneInfo zone, CancellationToken cancellationToken)
    {
        var keys = lines.Where(l => l.ChargeCategory != ChargeCategories.Premium)
            .GroupBy(l => new ChargeKey(l.ElementLocator, l.CoverageCode, l.ChargeType))
            .OrderBy(g => g.Key)
            .ToList();
        var treated = new List<TreatedTaxLine>();
        if (keys.Count == 0)
        {
            return treated;
        }

        var calculator = taxCalculator.Value;
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        foreach (var group in keys)
        {
            var first = group.First();
            TaxTreatmentResult treatment;
            try
            {
                treatment = await calculator.TreatmentAsync(
                    new TaxTreatmentRequest
                    {
                        LegalEntityId = legalEntity.Value, RiskJurisdiction = policy.Jurisdiction.Trim(), TaxPointDate = DayCount.Date(effective, zone),
                        ChargeType = group.Key.ChargeType, Category = first.ChargeCategory == ChargeCategories.Levy ? TaxCategory.Levy : TaxCategory.Tax,
                        ChargeOrigin = ChargeOrigin.Pol, TransactionKind = Market.Contracts.Spi.TaxTransactionKind.Cancellation, CancellationSource = source,
                        PolicyholderType = PolicyholderType.Consumer, BusinessBasis = BusinessBasis,
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (SpiException ex)
            {
                return DomainError.Of(
                    ModuleCode.POL, "GATE-FAILED",
                    $"The cancellation cannot be priced: no tax treatment is available for {group.Key.ChargeType} on a {source} cancellation ({ex.Error.Code}). Nothing was changed.");
            }
            catch (DomainException ex) when (ex.Error.Code.Module != ModuleCode.POL)
            {
                // E.g. MKT refuses a value that is not Settled in Production (D-REG-02): the cancellation fails closed with MKT's own error.
                return ex.Error;
            }

            if (treatment.Action != TreatmentAction.KeepNotReduced)
            {
                return DomainError.Of(
                    ModuleCode.POL, "GATE-FAILED",
                    $"The tax treatment {treatment.Action} of {group.Key.ChargeType} is not available in this release; only KEEP_NOT_REDUCED is implemented. Nothing was changed.");
            }

            var provisional = treatment.LegalStatus != TreatmentLegalStatus.Settled;
            treated.Add(new TreatedTaxLine(
                group.Key, first.ChargeCategory, first.AnnualRate, 0m, group.Sum(l => l.Amount),
                new TreatmentView
                {
                    Action = "KEEP_NOT_REDUCED", RuleId = treatment.RuleId, RuleVersion = treatment.RuleVersion,
                    LegalStatus = provisional ? "PendingOpinion" : "Settled", Provisional = provisional,
                }));
        }

        return treated;
    }

    private static RefundPreview Preview(
        Currency currency, IReadOnlyList<ChargeLineRow> lines, IReadOnlyList<ServicingDelta> premiumDeltas, IReadOnlyList<TreatedTaxLine> tax, bool includeTax)
    {
        var rows = new List<RefundPreviewLine>();
        foreach (var group in lines.Where(l => l.ChargeCategory == ChargeCategories.Premium)
                     .GroupBy(l => new ChargeKey(l.ElementLocator, l.CoverageCode, l.ChargeType)).OrderBy(g => g.Key))
        {
            var delta = premiumDeltas.FirstOrDefault(d => d.Key == group.Key);
            rows.Add(new RefundPreviewLine
            {
                ElementLocator = group.Key.ElementLocator, CoverageCode = group.Key.CoverageCode, ChargeType = group.Key.ChargeType,
                ChargeCategory = ChargeCategories.Premium, Written = group.Sum(l => l.Amount), Amount = delta?.Amount ?? 0m,
                Days = delta?.FractionNumerator ?? 0, Basis = delta?.FractionDenominator ?? 0,
            });
        }

        var notes = new List<string>();
        foreach (var line in tax)
        {
            rows.Add(new RefundPreviewLine
            {
                ElementLocator = line.Key.ElementLocator, CoverageCode = line.Key.CoverageCode, ChargeType = line.Key.ChargeType,
                ChargeCategory = line.ChargeCategory, Written = line.Written, Amount = includeTax ? line.Amount : 0m, Days = 0, Basis = 0, Treatment = line.Treatment,
            });
            notes.Add(line.ChargeCategory == ChargeCategories.Tax
                ? line.Treatment.Provisional ? "IPT not refunded (provisional)" : "IPT not refunded"
                : $"{line.Key.ChargeType} not refunded{(line.Treatment.Provisional ? " (provisional)" : string.Empty)}");
        }

        var premium = rows.Where(r => r.ChargeCategory == ChargeCategories.Premium).Sum(r => r.Amount);
        var taxes = rows.Where(r => r.ChargeCategory != ChargeCategories.Premium).Sum(r => r.Amount);
        return new RefundPreview
        {
            Currency = currency.Code, Lines = rows, PremiumCredit = premium, TaxCredit = taxes, RefundDue = -(premium + taxes), Notes = [.. notes.Distinct()],
        };
    }

    private static DomainError Refusal(ServicingResult result) => result.Refusal switch
    {
        ServicingRefusal.OutOfSequence => DomainError.Of(ModuleCode.POL, PolicyErrorNames.OutOfSequence, result.Message ?? "The effective time precedes the term's latest bound transaction."),
        ServicingRefusal.EffectiveOutsideTerm => DomainError.Of(ModuleCode.POL, PolicyErrorNames.EffdateLimit, result.Message ?? "The effective time is outside the term."),
        ServicingRefusal.AfterCancellation => DomainError.Of(ModuleCode.POL, CancellationErrors.IllegalTransition, result.Message ?? "The term is already cancelled."),
        _ => DomainError.Of(ModuleCode.POL, PolicyErrorNames.Validation, result.Message ?? "The cancellation cannot be computed."),
    };

    private static DomainError Validation(string field, string code, string detail) =>
        new(ErrorCode.For(ModuleCode.POL, PolicyErrorNames.Validation), detail) { FieldErrors = [new FieldError(field, code, code)] };

    /// <summary>MKT rounding of a premium amount (purpose <c>charge.line</c>), cached per amount; the engine's rule port is synchronous.</summary>
    private PremiumRounding Rounding(Currency currency, BusinessDate validAt)
    {
        var cache = new Dictionary<decimal, decimal>();
        return amount =>
        {
            if (cache.TryGetValue(amount, out var hit))
            {
                return hit;
            }

            var response = rounding.Value.ApplyAsync(
                new RoundingApplyRequest
                {
                    Amount = new Money(amount, currency), Currency = currency, Purpose = "charge.line",
                    Context = new RoundingApplyRequest.ContextDetail { LegalEntity = context.LegalEntity!.Value.Value, ValidAt = validAt },
                }).GetAwaiter().GetResult();
            var rounded = response.AmountAfterRounding is { } money && money.Currency == currency
                ? money.Amount
                : throw new DomainException(DomainError.Of(ModuleCode.POL, "RATING", "MKT rounding returned no amount for a cancellation credit."));
            cache[amount] = rounded;
            return rounded;
        };
    }

    private async Task<Result<bool>> SaveAsync(bool value, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return value;
        }
        catch (DbUpdateConcurrencyException)
        {
            return JobSupport.Stale();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.ExclusionViolation } pg)
        {
            // A second open cancellation of the term, or an overlapping record: another writer got there first.
            return pg.SqlState == PostgresErrorCodes.ExclusionViolation || pg.TableName is "segment" or "policy_term"
                ? DomainError.Of(ModuleCode.POL, "SEGMENT-INVARIANT", $"The cancellation would break the policy timeline ({pg.ConstraintName}).")
                : JobSupport.Stale();
        }
    }
}

/// <summary>A tax or levy line after the MKT treatment: the delta is 0.00 for KEEP_NOT_REDUCED.</summary>
internal sealed record TreatedTaxLine(ChargeKey Key, string ChargeCategory, decimal AnnualRate, decimal Amount, decimal Written, TreatmentView Treatment);

/// <summary>Audit facts of <c>pol.Cancellation.create</c>: the job, the policy and lineage keys (no personal data).</summary>
internal sealed class CancelPolicyAuditor : ICommandAuditor<CancelPolicy, CancellationResponse>
{
    public CommandAuditFacts Describe(CancelPolicy command, Result<CancellationResponse>? result)
    {
        if (result is not { IsSuccess: true } success)
        {
            return new CommandAuditFacts { ObjectRef = ObjectRef.For(ModuleCode.POL, "Policy", command.Request.PolicyId) };
        }

        var r = success.Value;
        return new CommandAuditFacts
        {
            ObjectRef = ObjectRef.For(ModuleCode.POL, "Policy", r.PolicyId),
            ObjectNumber = r.JobNumber,
            BusinessKeys = BusinessKeys.Empty.With("policyId", r.PolicyId.Value.ToString()).With("jobId", r.JobId.Value.ToString())
                .With("transactionId", r.TransactionId.Value.ToString()).With("policyTermId", r.TermId.Value.ToString()),
            Changes = AuditDiff.Compute(null, new { source = r.Source, reasonCode = r.ReasonCode, kind = r.Kind, refundMethod = r.RefundMethod, refundDue = r.RefundPreview.RefundDue.ToString(CultureInfo.InvariantCulture) }),
        };
    }
}
