using CoreIns.Modules.Market.Contracts;
using CoreIns.Modules.Policy.Contracts;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Modules.Policy.Contracts.Events;
using CoreIns.Modules.Policy.Domain;
using CoreIns.Modules.Policy.Domain.Servicing;
using CoreIns.Modules.Policy.Persistence;
using CoreIns.Modules.Policy.Services;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts.Common;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CoreIns.Modules.Policy.Commands.Change;

/// <summary>
/// <c>pol.Job.bind</c> for a change job (routed here by <c>BindJobHandler</c>). Order of work, all in the command transaction:
/// <list type="number">
/// <item>the explicit human confirmation (REQ-POL-181);</item>
/// <item>the policy write lock and the single record time <c>t</c> (D-SL3-03: lock, then stamp);</item>
/// <item>under the lock: the job is claimed, the quote is current, the cover is not cancelled (G1), the base transaction is
/// still the term's head (<c>POL-ERR-PREEMPTED</c>) and the effective time is in sequence (<c>POL-ERR-OUT-OF-SEQUENCE</c>);</item>
/// <item>the engine and tax port price the change again; a result that differs from the quote is <c>POL-ERR-QUOTE-STALE</c>;</item>
/// <item>supersession, all stamped exactly <c>t</c>: the replaced term version and segment close their record period at <c>t</c>,
/// the new term version, the segments (the elapsed part carried over, the remainder with the new risk), the Change transaction and
/// its NET charge lines are inserted; history stays readable at any earlier knownAt;</item>
/// <item><c>ChargeDeltaEmitted</c> (one complete set, none for a no-premium change) and <c>PolicyChanged</c> through the outbox.</item>
/// </list>
/// A dry run takes the same path and rolls back: no delta, number, row or event survives (REQ-POL-129).
/// </summary>
internal sealed class ChangeBindService(
    PolicyDbContext db,
    RequestContext context,
    ILegalEntityDirectory legalEntities,
    IClock clock,
    IEventPublisher events,
    ChangeContextLoader loader,
    ChangePricer pricer,
    Dependency<IMarketConfigurationService> marketConfiguration,
    IOptions<PolicyOptions> options,
    IOptions<ChangeOptions> changeOptions)
{
    /// <summary>True when the job exists in the caller's legal entity and is a change job.</summary>
    public async Task<bool> IsChangeJobAsync(JobId jobId, CancellationToken cancellationToken)
    {
        var legalEntity = JobSupport.LegalEntity(context, legalEntities);
        return await db.Jobs.AsNoTracking().AnyAsync(j => j.JobId == jobId && j.LegalEntityId == legalEntity && j.JobType == ChangeNames.JobType, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Result<JobBindResponse>> BindAsync(JobBindRequest request, CancellationToken cancellationToken)
    {
        var zone = options.Value.Zone;
        if (request.Confirmation != true)
        {
            return DomainError.Of(ModuleCode.POL, "HUMAN-CONFIRMATION-REQUIRED", "The acting user must confirm the change explicitly (REQ-POL-181).");
        }

        var legalEntity = JobSupport.LegalEntity(context, legalEntities);
        var policyIds = await db.Jobs.AsNoTracking().Where(j => j.JobId == request.JobId && j.LegalEntityId == legalEntity && j.JobType == ChangeNames.JobType)
            .Select(j => j.PolicyId).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (policyIds.Count == 0)
        {
            return JobSupport.NotFound("job or quote version");
        }

        var policyId = policyIds[0];

        // Lock, then stamp: nothing is read for decision before the lock is held, and every row below carries exactly t.
        var locked = await PolicyWriteLock.AcquireAsync(db, clock, options.Value.LockWait, legalEntity, policyId, cancellationToken).ConfigureAwait(false);
        if (locked.IsFailure)
        {
            return locked.Error!;
        }

        var t = locked.Value;
        var loaded = await JobSupport.LoadAsync(db, legalEntity, request.JobId, request.VersionNo, cancellationToken).ConfigureAwait(false);
        if (loaded is not var (job, version))
        {
            return JobSupport.NotFound("job or quote version");
        }

        var bound = JobSupport.Fire(job, JobTrigger.Bind);
        if (bound.IsFailure)
        {
            return bound.Error!;
        }

        if (version.VersionNo != job.CurrentVersionNo || Codes.Parse<QuoteState>(version.State) != QuoteState.Quoted || version.Charges is null)
        {
            return DomainError.Of(ModuleCode.POL, "QUOTE-STALE", "Only the job's current quoted version can be bound.");
        }

        if (version.ValidUntil is not { } validUntil || validUntil <= t)
        {
            return DomainError.Of(ModuleCode.POL, "QUOTE-STALE", "The quote's validity has ended; requote it.");
        }

        var current = (await marketConfiguration.Value.CurrentHashAsync(cancellationToken).ConfigureAwait(false)).Hash;
        if (current is { } currentHash && !string.Equals(version.ConfigurationHash, currentHash.Value, StringComparison.Ordinal))
        {
            return DomainError.Of(ModuleCode.POL, "QUOTE-STALE", "The configuration changed since the quote was priced; requote it before binding.");
        }

        var change = await loader.LoadAsync(job, tracked: true, cancellationToken).ConfigureAwait(false);
        if (change.IsFailure)
        {
            return change.Error!;
        }

        var (history, baseSegment, baseTree, engine, state) = change.Value;
        var tree = JobSupport.Tree(version);
        var currency = Currency.FromCode(job.Currency);
        var transactionId = PolicyTransactionId.New();
        var rates = ChangeQuoteService.Rates(JobSupport.FromJson<List<ChargeLine>>(version.Charges));
        var priced = await pricer.PriceAsync(engine, state, rates, job.EffectiveAt, transactionId.Value.ToString(), job.Jurisdiction, zone, cancellationToken).ConfigureAwait(false);
        if (priced.IsFailure)
        {
            return priced.Error!;
        }

        var pricing = priced.Value;
        if (pricing.Premium.Amount != version.Premium || pricing.Taxes.Amount != version.Taxes || pricing.Total.Amount != version.Total)
        {
            return DomainError.Of(ModuleCode.POL, "QUOTE-STALE", "The change prices differently from its quote; requote it before binding.");
        }

        var oldTerm = history.Term;
        var eff = job.EffectiveAt;
        var actor = context.Actor.ToString();
        var today = t.ToBusinessDate(zone);
        var sequence = (await db.Transactions.Where(x => x.PolicyId == policyId).MaxAsync(x => (int?)x.Sequence, cancellationToken).ConfigureAwait(false) ?? 0) + 1;
        var diff = RiskDiffs.Compute(baseTree, tree, changeOptions.Value.EditableVehicleFields);

        // 1. Close the record period of what is superseded, at t (the database accepts no other instant). Done first so that
        //    the one-current-version and non-overlap guarantees hold when the replacements are inserted.
        oldTerm.RecordedTo = t;
        baseSegment.RecordedTo = t;
        var closed = await SaveAsync(cancellationToken).ConfigureAwait(false);
        if (closed.IsFailure)
        {
            return closed.Error!;
        }

        db.Terms.Add(new PolicyTermRow
        {
            TermVersionId = Guid.CreateVersion7(), TermId = oldTerm.TermId, PolicyId = policyId, LegalEntityId = legalEntity, TermNumber = oldTerm.TermNumber,
            ValidFrom = oldTerm.ValidFrom, ValidTo = oldTerm.ValidTo, RecordedFrom = t, State = oldTerm.State, ProductVersion = oldTerm.ProductVersion,
            ArtefactHash = oldTerm.ArtefactHash, RatingArtefactHash = oldTerm.RatingArtefactHash, ResolutionHash = oldTerm.ResolutionHash,
            ConfigurationHash = oldTerm.ConfigurationHash, Currency = oldTerm.Currency, ProducerCode = oldTerm.ProducerCode,
            PaymentPlanRef = oldTerm.PaymentPlanRef, WrittenDate = oldTerm.WrittenDate, HeadTransactionId = transactionId, CreatedBy = oldTerm.CreatedBy,
            PredecessorTermId = oldTerm.PredecessorTermId, CancelledAt = oldTerm.CancelledAt,
        });
        db.Transactions.Add(new PolicyTransactionRow
        {
            TransactionId = transactionId, PolicyId = policyId, TermId = oldTerm.TermId, JobId = job.JobId, LegalEntityId = legalEntity,
            Kind = Codes.Of(PolicyTransactionKind.Change), Sequence = sequence, EffectiveAt = eff, RecordedAt = t,
            ConfigurationHash = version.ConfigurationHash!, ArtefactHash = job.ArtefactHash, RatingArtefactHash = job.RatingArtefactHash,
            ResolutionHash = job.ResolutionHash, WorksheetId = version.WorksheetId, Intent = ChangeJson.Intent(eff, rates, tree),
            Premium = pricing.Premium.Amount, Taxes = pricing.Taxes.Amount, Total = pricing.Total.Amount, Currency = job.Currency, Actor = actor,
            CorrelationId = context.CorrelationId.ToString(), Origin = context.Origin.ToCode(),
        });

        // Segments: the part before the change is carried over unchanged (same content, new row), the remainder holds the new risk.
        if (baseSegment.ValidFrom < eff)
        {
            db.Segments.Add(new SegmentRow
            {
                SegmentId = SegmentId.New(), TermId = oldTerm.TermId, PolicyId = policyId, TransactionId = baseSegment.TransactionId, LegalEntityId = legalEntity,
                ValidFrom = baseSegment.ValidFrom, ValidTo = eff, RecordedFrom = t, SnapshotHash = baseSegment.SnapshotHash, Snapshot = baseSegment.Snapshot,
                WorksheetId = baseSegment.WorksheetId,
            });
        }

        var snapshot = JobSupport.Json(tree);
        db.Segments.Add(new SegmentRow
        {
            SegmentId = SegmentId.New(), TermId = oldTerm.TermId, PolicyId = policyId, TransactionId = transactionId, LegalEntityId = legalEntity,
            ValidFrom = eff, ValidTo = baseSegment.ValidTo, RecordedFrom = t, SnapshotHash = CanonicalJson.Hash(snapshot).Value, Snapshot = snapshot,
            WorksheetId = version.WorksheetId,
        });

        // NET charge deltas: one complete set per transaction (D4: set_id = transaction id); none for a no-premium change.
        var newRates = pricing.NewRates.ToDictionary(r => r.Key);
        var lines = new List<ChargeLine>();
        var total = pricing.Deltas.Count + pricing.TaxLines.Count;
        var index = 0;
        foreach (var delta in pricing.Deltas)
        {
            index++;
            var kind = ChangeJson.Kind(delta.TransactionKind);
            lines.Add(AddCharge(
                transactionId, oldTerm, policyId, legalEntity, today, t, index, total, delta.Key.ElementLocator, delta.Key.CoverageCode, delta.Key.ChargeType, delta.ChargeCategory,
                newRates.TryGetValue(delta.Key, out var rate) ? rate.AnnualRate : 0m, delta.Amount, delta, kind, null, null, null, null, null, currency));
        }

        foreach (var tax in pricing.TaxLines)
        {
            index++;
            var source = pricing.Deltas.First(d => d.Key == tax.SourceKey);
            lines.Add(AddCharge(
                transactionId, oldTerm, policyId, legalEntity, today, t, index, total, tax.SourceKey.ElementLocator, tax.CoverageCode, tax.ChargeType, tax.ChargeCategory,
                tax.Rate, tax.Amount, source, ChangeJson.Kind(source.TransactionKind), tax.Action, tax.RuleId, tax.RuleVersion, tax.LegalStatus, tax.Provisional, currency));
        }

        events.Publish(new OutgoingEvent(
            EventDescriptor.From(PolicyChangedV1.Descriptor), "Policy", policyId.Value.ToString(),
            new PolicyChangedV1
            {
                TransactionId = transactionId,
                Kind = PolicyChangedV1.KindValue.Change,
                EffectiveDate = eff.ToBusinessDate(zone),
                ChangedElements =
                [
                    .. diff.ChangedVehicles.Concat(diff.AddedVehicles).Concat(diff.RemovedVehicles).Order(StringComparer.Ordinal)
                        .Select(locator => new ChangedElement { Locator = locator, ElementType = ChangeNames.VehicleElementType }),
                ],
                VehicleCoverFacts = new VehicleCoverFacts { Added = diff.AddedVehicles, Removed = diff.RemovedVehicles, Suspended = [], Reactivated = [] },
                PriorTerm = false,
            },
            BusinessKeys.Empty.With("policyId", policyId.Value.ToString()).With("policyTermId", oldTerm.TermId.Value.ToString())
                .With("transactionId", transactionId.Value.ToString()).With("jobId", job.JobId.Value.ToString()))
        {
            OccurredAt = t,
        });

        job.State = Codes.Of(bound.Value);
        job.BoundTransactionId = transactionId;
        JobSupport.AddParticipant(job, context.Actor);
        job.RecordVersion++;
        job.UpdatedAt = t;
        var saved = await SaveAsync(cancellationToken).ConfigureAwait(false);
        if (saved.IsFailure)
        {
            return saved.Error!;
        }

        var policyNumber = await db.Policies.AsNoTracking().Where(p => p.PolicyId == policyId).Select(p => p.PolicyNumber).SingleAsync(cancellationToken).ConfigureAwait(false);
        return new JobBindResponse
        {
            JobId = job.JobId,
            State = Codes.Api(bound.Value),
            TransactionId = transactionId,
            TermId = oldTerm.TermId,
            TermNumber = oldTerm.TermNumber,
            TermState = Codes.Api(Codes.Parse<PolicyTermState>(oldTerm.State)),
            PolicyId = policyId,
            PolicyNumber = policyNumber,
            RecordedAt = t,
            ChargeDeltas = lines,
            GateResults =
            [
                Gate("COVER_NOT_ENDED"), Gate("BASE_IS_HEAD"), Gate("IN_SEQUENCE"), Gate("QUOTE_CURRENT"),
            ],
        };
    }

    private ChargeLine AddCharge(
        PolicyTransactionId transactionId, PolicyTermRow term, PolicyId policyId, LegalEntityId legalEntity, BusinessDate today, Instant t, int index, int total,
        string element, string coverage, string chargeType, string category, decimal annualRate, decimal amount, ServicingDelta source, TaxTransactionKind kind,
        string? action, string? ruleId, string? ruleVersion, string? legalStatus, bool? provisional, Currency currency)
    {
        var chargeId = ChargeId.New();
        var period = DateRange.Of(new BusinessDate(source.DateFrom), new BusinessDate(source.DateTo));
        db.ChargeLines.Add(new ChargeLineRow
        {
            ChargeId = chargeId, TransactionId = transactionId, TermId = term.TermId, PolicyId = policyId, LegalEntityId = legalEntity,
            ElementLocator = element, CoverageCode = coverage, ChargeType = chargeType, ChargeCategory = category, DeltaKind = DeltaKinds.Net,
            AnnualRate = annualRate, Amount = amount, Currency = currency.Code, ValidFrom = new BusinessDate(source.DateFrom), ValidTo = new BusinessDate(source.DateTo),
            BookingDate = today, CorrelationKey = transactionId.Value.ToString(), SetIndex = index, SetSize = total, RecordedAt = t,
            TransactionKind = Codes.Of(kind), LegalStatus = legalStatus, Provisional = provisional, TaxTreatmentRef = action,
            TreatmentRuleId = ruleId, TreatmentRuleVersion = ruleVersion,
        });
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(ChargeDeltaEmittedV1.Descriptor), "Policy", policyId.Value.ToString(),
            new ChargeDeltaEmittedV1
            {
                ChargeId = chargeId, TermId = term.TermId, ElementLocator = element, CoverageCode = coverage, ChargeType = chargeType, ChargeCategory = category,
                DeltaKind = DeltaKinds.Net, NetAmount = new Money(amount, currency), ValidPeriod = period, BookingDate = today, TransactionId = transactionId,
                CorrelationKey = transactionId.Value.ToString(), TaxTreatmentRef = action, LegalStatus = legalStatus, Provisional = provisional,
            },
            BusinessKeys.Empty.With("policyId", policyId.Value.ToString()).With("chargeId", chargeId.Value.ToString())
                .With("policyTermId", term.TermId.Value.ToString()).With("transactionId", transactionId.Value.ToString()))
        {
            OccurredAt = t,
            Set = new EventSet(transactionId.Value, total, index),
        });
        return new ChargeLine
        {
            ChargeId = chargeId, TransactionId = transactionId, ElementLocator = element, CoverageCode = coverage, ChargeType = chargeType, ChargeCategory = category,
            AnnualRate = annualRate, Amount = new Money(amount, currency), LegalStatus = legalStatus, Provisional = provisional,
        };
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
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.ExclusionViolation } pg)
        {
            // A racing writer that slipped past the lock would break the timeline (REQ-POL-079); never a 500.
            return pg.SqlState == PostgresErrorCodes.ExclusionViolation || pg.TableName is "segment" or "policy_term" or "policy_transaction"
                ? DomainError.Of(ModuleCode.POL, "SEGMENT-INVARIANT", $"The change would break the policy timeline ({pg.ConstraintName}).")
                : JobSupport.Stale();
        }
    }

    private static JobBindResponse.GateResultItem Gate(string gate) => new()
    {
        Gate = gate, Passed = true, Severity = JobBindResponse.GateResultItem.SeverityValue.Block,
    };
}
