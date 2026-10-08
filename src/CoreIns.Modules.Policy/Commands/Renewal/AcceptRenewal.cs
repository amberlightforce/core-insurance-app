using System.Text.Json;
using CoreIns.Modules.Market.Contracts;
using CoreIns.Modules.Policy.Contracts;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Modules.Policy.Contracts.Events;
using CoreIns.Modules.Policy.Domain;
using CoreIns.Modules.Policy.Domain.Servicing;
using CoreIns.Modules.Policy.Persistence;
using CoreIns.Modules.Policy.Services;
using CoreIns.Modules.Product.Contracts;
using CoreIns.Modules.Underwriting.Contracts;
using CoreIns.Modules.Underwriting.Contracts.Api;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts.Common;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CoreIns.Modules.Policy.Commands.Renewal;

/// <summary><c>pol.Renewal.accept</c>: records the explicit acceptance and binds term n+1 in the same command.</summary>
internal sealed record AcceptRenewal(RenewalAcceptRequest Request) : ICommand<RenewalAcceptResponse>;

internal sealed class AcceptRenewalValidator : AbstractValidator<AcceptRenewal>
{
    public AcceptRenewalValidator() => RuleFor(c => c.Request.TermId).NotNull().WithErrorCode("TERM_REQUIRED");
}

/// <summary>
/// Accepts an offered renewal and binds the next term (REQ-POL-253 explicit acceptance, REQ-POL-257 channel, actor and time,
/// REQ-POL-005, -033, -263), in one transaction under the policy lock:
/// <list type="number">
/// <item>the expiring term is still renewable (a cancelled one is not) and its head is still the base the renewal was created on,
/// else <c>POL-ERR-REBASE-REQUIRED</c> (409); it has no next term yet (accepting twice never creates two);</item>
/// <item>the offer is the job's current Quoted version, still priced under the current MKT configuration;</item>
/// <item>UW is evaluated again at PRE_BIND with the acceptor as participant: a referral that is still open fails the gate
/// (the job stays Quoted, nothing is bound), exactly as <c>pol.Job.bind</c> does;</item>
/// <item>term n+1 (Scheduled, <c>predecessor_term_id</c>, the product version, artefact hashes and configuration the offer was
/// priced with) with a Renewal transaction and the segment of the copied risk tree; the premium lines come from the servicing
/// engine's <c>NewTerm</c> intent and must equal the offered premium, the tax lines are RAT's, all tagged NEW_BUSINESS
/// (MKT applies the tax: <c>APPLY</c>); the policy number is unchanged (REQ-POL-031);</item>
/// <item><c>RenewalBound</c> and the complete <c>ChargeDeltaEmitted</c> set go through the outbox.</item>
/// </list>
/// Everything is stamped with the one record time of the lock. Dry-run computes the same and rolls back.
/// </summary>
internal sealed class AcceptRenewalHandler(
    PolicyDbContext db,
    RequestContext context,
    ILegalEntityDirectory legalEntities,
    IClock clock,
    IEventPublisher events,
    Dependency<IUnderwritingRulesService> underwriting,
    Dependency<IMarketConfigurationService> marketConfiguration,
    Dependency<IProductArtifactService> artefacts,
    RatingInput ratingInput,
    IOptions<PolicyOptions> options) : ICommandHandler<AcceptRenewal, RenewalAcceptResponse>
{
    private const string Block = "BLOCK";

    public async Task<Result<RenewalAcceptResponse>> HandleAsync(AcceptRenewal command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        if (request.TermId is not { } termId)
        {
            return RenewalSupport.TermRequired();
        }

        var channel = Channel(request.AcceptanceEvidence);
        if (channel is null)
        {
            return RenewalSupport.Validation("acceptanceEvidence.channel", "CHANNEL_NOT_SUPPORTED", "Only explicit acceptance by staff (channel STAFF) is supported.");
        }

        var legalEntity = JobSupport.LegalEntity(context, legalEntities);
        var zone = options.Value.Zone;
        _ = underwriting.Value; // fail fast when UW is not wired
        if (await RenewalSupport.PolicyOfAsync(db, legalEntity, termId, cancellationToken).ConfigureAwait(false) is not { } policyId)
        {
            return JobSupport.NotFound("term");
        }

        var locked = await PolicyWriteLock.AcquireAsync(db, clock, options.Value.LockWait, legalEntity, policyId, cancellationToken).ConfigureAwait(false);
        if (locked.IsFailure)
        {
            return locked.Error!;
        }

        var now = locked.Value;
        var job = await RenewalSupport.LatestJobAsync(db, legalEntity, termId, cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            return JobSupport.NotFound("renewal of the term");
        }

        var state = Codes.Parse<JobState>(job.State);
        if (state != JobState.Quoted)
        {
            return DomainError.Of(
                ModuleCode.POL, "ILLEGAL-TRANSITION",
                state == JobState.Bound ? "The renewal was already accepted; the next term exists." : $"A {state} renewal cannot be accepted.");
        }

        var term = await RenewalSupport.CurrentTermAsync(db, legalEntity, termId, cancellationToken).ConfigureAwait(false);
        if (term is null)
        {
            return JobSupport.NotFound("term");
        }

        if (RenewalSupport.NotRenewable(term) is { } notRenewable)
        {
            return notRenewable;
        }

        if (now >= term.ValidTo)
        {
            return RenewalSupport.Validation("termId", "TERM_ENDED", "The expiring term has ended; it can no longer be renewed.");
        }

        if (term.HeadTransactionId != job.BaseTransactionId)
        {
            return DomainError.Of(
                ModuleCode.POL, PolicyErrorNames.RebaseRequired, "A change was bound on the expiring term after the renewal was created; offer the renewal again.");
        }

        if (await db.Terms.AnyAsync(t => t.PredecessorTermId == termId && t.RecordedTo == null, cancellationToken).ConfigureAwait(false) || job.EffectiveAt != term.ValidTo)
        {
            return DomainError.Of(ModuleCode.POL, "ILLEGAL-TRANSITION", "The term has already been renewed.");
        }

        var version = await RenewalSupport.CurrentVersionAsync(db, job, cancellationToken).ConfigureAwait(false);
        if (Codes.Parse<QuoteState>(version.State) != QuoteState.Quoted || version.ValidUntil is not { } validUntil || validUntil <= now)
        {
            return DomainError.Of(ModuleCode.POL, "QUOTE-STALE", "The renewal offer is no longer valid; offer it again.");
        }

        // Accepted at the price it was offered at, and only under the configuration it was priced with (REQ-POL-088).
        var current = (await marketConfiguration.Value.CurrentHashAsync(cancellationToken).ConfigureAwait(false)).Hash;
        if (current is { } currentHash && !string.Equals(version.ConfigurationHash, currentHash.Value, StringComparison.Ordinal))
        {
            return DomainError.Of(ModuleCode.POL, "QUOTE-STALE", "The configuration changed since the renewal was priced; offer it again.");
        }

        // UW gate at PRE_BIND with the acceptor as a participant (D-UW-01): an open referral fails the gate.
        var tree = JobSupport.Tree(version);
        var view = await ratingInput.BuildAsync(tree, job.EffectiveAt, cancellationToken).ConfigureAwait(false);
        if (view.IsFailure)
        {
            return view.Error!;
        }

        var uw = await UwEvaluation.EvaluateAsync(
            underwriting.Value, context, job, version, view.Value, RulesEvaluateRequest.CheckpointValue.PreBind, zone, cancellationToken).ConfigureAwait(false);
        if (uw.IsFailure)
        {
            return uw.Error!;
        }

        var (evaluation, issues) = uw.Value;
        var blocked = evaluation.Outcome == RulesEvaluateResponse.OutcomeValue.Decline || issues.Any(i => UwOutcome.Blocks(i, BlockingPoint.PreBind));
        version.Issues = JobSupport.Json(issues);
        job.Referred = blocked;
        job.RecordVersion++;
        job.UpdatedAt = now;
        if (blocked)
        {
            job.SubState = null;
            var kept = await SaveAsync(true, cancellationToken).ConfigureAwait(false);
            if (kept.IsFailure)
            {
                return kept.Error!;
            }

            return new RenewalAcceptResponse
            {
                Job = RenewalSupport.JobJson(job, version, new
                {
                    accepted = false,
                    gateResults = new[]
                    {
                        new { gate = "UW_ISSUES", passed = false, severity = Block, reason = evaluation.Outcome == RulesEvaluateResponse.OutcomeValue.Decline ? "UW_DECLINE" : "UW_ISSUES_OPEN" },
                    },
                }),
            };
        }

        if (job.SubState != Codes.Of(JobSubState.Offered))
        {
            return DomainError.Of(ModuleCode.POL, "ILLEGAL-TRANSITION", "The renewal has not been offered; offer it before accepting.");
        }

        var bound = JobSupport.Fire(job, JobTrigger.Bind);
        if (bound.IsFailure)
        {
            return bound.Error!;
        }

        var currency = Currency.FromCode(job.Currency);
        var charges = JobSupport.FromJson<List<ChargeLine>>(version.Charges!);
        var offeredTotals = Charges.Totals(charges, currency);

        // Term n+1 through the servicing engine's NewTerm intent (premium lines); tax lines are RAT's (D-SL3-10).
        var convention = await DayCountAsync(job.ArtefactHash, cancellationToken).ConfigureAwait(false);
        if (convention.IsFailure)
        {
            return convention.Error!;
        }

        var transactionId = PolicyTransactionId.New();
        var termRow = new ServicingTerm(job.EffectiveAt, job.ExpirationAt, currency, convention.Value, zone);
        var premiumLines = charges.Where(c => c.ChargeCategory == ChargeCategories.Premium).ToList();
        var engine = new ServicingEngine(new ReferenceProration(), amount => OfferedAmount(premiumLines, amount));
        var opened = engine.Apply(
            null,
            new NewTermIntent(
                termRow,
                [.. premiumLines.Select(l => new ChargeRate(l.ElementLocator, l.CoverageCode, l.ChargeType, l.ChargeCategory, l.AnnualRate))]),
            new DeltaCorrelation(transactionId.Value.ToString(), transactionId.Value.ToString()));
        if (!opened.IsAccepted)
        {
            return DomainError.Of(ModuleCode.POL, "RATING", $"The servicing engine refused the new term: {opened.Refusal}. {opened.Message}");
        }

        var frozen = new List<FrozenLine>();
        foreach (var delta in opened.Deltas)
        {
            var source = premiumLines.First(l => l.ElementLocator == delta.Key.ElementLocator && l.CoverageCode == delta.Key.CoverageCode && l.ChargeType == delta.Key.ChargeType);
            frozen.Add(new FrozenLine(
                source.ElementLocator, source.CoverageCode, source.ChargeType, delta.ChargeCategory, source.AnnualRate, delta.Amount, source.LegalStatus, source.Provisional));
        }

        frozen.AddRange(charges.Where(c => c.ChargeCategory != ChargeCategories.Premium).Select(c => new FrozenLine(
            c.ElementLocator, c.CoverageCode, c.ChargeType, c.ChargeCategory, c.AnnualRate, c.Amount.Amount, c.LegalStatus, c.Provisional)));
        var premium = Money.Sum(frozen.Where(l => l.ChargeCategory == ChargeCategories.Premium).Select(l => new Money(l.Amount, currency)), currency);
        var taxes = Money.Sum(frozen.Where(l => l.ChargeCategory != ChargeCategories.Premium).Select(l => new Money(l.Amount, currency)), currency);
        if (premium != offeredTotals.Premium || taxes != offeredTotals.Taxes)
        {
            return DomainError.Of(ModuleCode.POL, "RATING", "The new term's charges do not equal the offered charges; offer the renewal again.");
        }

        var total = premium + taxes;
        var configuration = version.ConfigurationHash is { } priced ? ConfigurationHash.Parse(priced)
            : context.ConfigurationHash ?? throw new InvalidOperationException("No configuration hash is pinned for this command (REQ-POL-088).");
        var newTermId = PolicyTermId.New();
        var today = now.ToBusinessDate(zone);
        var termState = PolicyTermStateModel.Machine.Start(job.EffectiveAt > now ? PolicyTermState.Scheduled : PolicyTermState.InForce).Value;
        var actor = context.Actor.ToString();
        var snapshot = JobSupport.Json(tree);
        var policy = await db.Policies.AsNoTracking().SingleAsync(p => p.PolicyId == policyId, cancellationToken).ConfigureAwait(false);
        var sequence = (await db.Transactions.Where(t => t.PolicyId == policyId).MaxAsync(t => (int?)t.Sequence, cancellationToken).ConfigureAwait(false) ?? 0) + 1;

        db.Terms.Add(new PolicyTermRow
        {
            TermVersionId = Guid.CreateVersion7(), TermId = newTermId, PolicyId = policyId, LegalEntityId = legalEntity, TermNumber = term.TermNumber + 1,
            ValidFrom = job.EffectiveAt, ValidTo = job.ExpirationAt, RecordedFrom = now, State = Codes.Of(termState),
            ProductVersion = job.ProductVersion, ArtefactHash = job.ArtefactHash, RatingArtefactHash = job.RatingArtefactHash,
            ResolutionHash = job.ResolutionHash, ConfigurationHash = configuration.Hash.Value, Currency = job.Currency,
            ProducerCode = term.ProducerCode, PaymentPlanRef = term.PaymentPlanRef, WrittenDate = today, HeadTransactionId = transactionId,
            CreatedBy = actor, PredecessorTermId = termId,
        });
        db.Transactions.Add(new PolicyTransactionRow
        {
            TransactionId = transactionId, PolicyId = policyId, TermId = newTermId, JobId = job.JobId, LegalEntityId = legalEntity,
            Kind = Codes.Of(PolicyTransactionKind.Renewal), Sequence = sequence, EffectiveAt = job.EffectiveAt, RecordedAt = now,
            ConfigurationHash = configuration.Hash.Value, ArtefactHash = job.ArtefactHash, RatingArtefactHash = job.RatingArtefactHash,
            ResolutionHash = job.ResolutionHash, WorksheetId = version.WorksheetId,
            Intent = JobSupport.Json(new { quoteId = version.QuoteId, versionNo = version.VersionNo, expiringTermId = termId, riskTree = tree }),
            Premium = premium.Amount, Taxes = taxes.Amount, Total = total.Amount, Currency = job.Currency, Actor = actor,
            CorrelationId = context.CorrelationId.ToString(), Origin = context.Origin.ToCode(),
        });
        db.Segments.Add(new SegmentRow
        {
            SegmentId = SegmentId.New(), TermId = newTermId, PolicyId = policyId, TransactionId = transactionId, LegalEntityId = legalEntity,
            ValidFrom = job.EffectiveAt, ValidTo = job.ExpirationAt, RecordedFrom = now,
            SnapshotHash = CanonicalJson.Hash(snapshot).Value, Snapshot = snapshot, WorksheetId = version.WorksheetId,
        });

        var period = PolicyTime.Dates(job.EffectiveAt, job.ExpirationAt, zone);
        var emitted = new List<ChargeLine>();
        for (var i = 0; i < frozen.Count; i++)
        {
            var line = frozen[i];
            var chargeId = ChargeId.New();
            db.ChargeLines.Add(new ChargeLineRow
            {
                ChargeId = chargeId, TransactionId = transactionId, TermId = newTermId, PolicyId = policyId, LegalEntityId = legalEntity,
                ElementLocator = line.ElementLocator, CoverageCode = line.CoverageCode, ChargeType = line.ChargeType, ChargeCategory = line.ChargeCategory,
                DeltaKind = DeltaKinds.Net, AnnualRate = line.AnnualRate, Amount = line.Amount, Currency = job.Currency,
                ValidFrom = period.Start, ValidTo = period.End!.Value, BookingDate = today, CorrelationKey = transactionId.Value.ToString(),
                SetIndex = i + 1, SetSize = frozen.Count, RecordedAt = now, LegalStatus = line.LegalStatus, Provisional = line.Provisional,
                TransactionKind = Codes.Of(TaxTransactionKind.NewBusiness),
            });
            var emittedLine = new ChargeLine
            {
                ChargeId = chargeId, TransactionId = transactionId, ElementLocator = line.ElementLocator, CoverageCode = line.CoverageCode, ChargeType = line.ChargeType,
                ChargeCategory = line.ChargeCategory, AnnualRate = line.AnnualRate, Amount = new Money(line.Amount, currency), LegalStatus = line.LegalStatus,
                Provisional = line.Provisional,
            };
            emitted.Add(emittedLine);
            events.Publish(new OutgoingEvent(
                EventDescriptor.From(ChargeDeltaEmittedV1.Descriptor), "Policy", policyId.Value.ToString(),
                new ChargeDeltaEmittedV1
                {
                    ChargeId = chargeId, TermId = newTermId, ElementLocator = line.ElementLocator, CoverageCode = line.CoverageCode,
                    ChargeType = line.ChargeType, ChargeCategory = line.ChargeCategory, DeltaKind = DeltaKinds.Net, NetAmount = emittedLine.Amount,
                    ValidPeriod = period, BookingDate = today, TransactionId = transactionId, CorrelationKey = transactionId.Value.ToString(),
                    LegalStatus = line.LegalStatus, Provisional = line.Provisional,
                },
                BusinessKeys.Empty.With("policyId", policyId.Value.ToString()).With("chargeId", chargeId.Value.ToString())
                    .With("policyTermId", newTermId.Value.ToString()).With("transactionId", transactionId.Value.ToString()))
            {
                OccurredAt = now,
                Set = new EventSet(transactionId.Value, frozen.Count, i + 1),
            });
        }

        events.Publish(new OutgoingEvent(
            EventDescriptor.From(RenewalBoundV1.Descriptor), "Policy", policyId.Value.ToString(),
            new RenewalBoundV1
            {
                NewTermId = newTermId, NewTermNumber = term.TermNumber + 1, TransactionId = transactionId, ProductCode = job.ProductCode,
                ProductVersion = ProductVersionNumber.Parse(job.ProductVersion), ArtefactHash = Sha256Hash.Parse(job.ArtefactHash),
                ProducerOfRecord = job.ProducerCode ?? RenewalSupport.DirectProducer,
            },
            BusinessKeys.Empty.With("policyId", policyId.Value.ToString()).With("newTermId", newTermId.Value.ToString())
                .With("transactionId", transactionId.Value.ToString()).With("jobId", job.JobId.Value.ToString()).With("expiringTermId", termId.Value.ToString()))
        {
            OccurredAt = now,
        });

        job.State = Codes.Of(bound.Value);
        job.SubState = Codes.Of(JobSubState.Accepted);
        job.AcceptanceChannel = channel;
        job.AcceptedAt = now;
        job.AcceptedBy = actor;
        job.BoundTransactionId = transactionId;
        var saved = await SaveAsync(true, cancellationToken).ConfigureAwait(false);
        if (saved.IsFailure)
        {
            return saved.Error!;
        }

        return new RenewalAcceptResponse
        {
            Job = RenewalSupport.JobJson(job, version, new
            {
                accepted = true,
                policyNumber = policy.PolicyNumber,
                termId = newTermId,
                termNumber = term.TermNumber + 1,
                termState = Codes.Api(termState).ToString().ToUpperInvariant(),
                transactionId,
                recordedAt = now,
                chargeDeltas = emitted,
            }),
        };
    }

    private static decimal OfferedAmount(List<ChargeLine> premiumLines, decimal annualRate)
    {
        foreach (var line in premiumLines)
        {
            if (line.AnnualRate == annualRate)
            {
                return line.Amount.Amount;
            }
        }

        throw new InvalidOperationException($"No offered premium line has annual rate {annualRate}.");
    }

    private static string? Channel(JsonElement? evidence)
    {
        if (evidence is not { ValueKind: JsonValueKind.Object } value || !value.TryGetProperty("channel", out var channel))
        {
            return RenewalSupport.ChannelStaff;
        }

        return channel.ValueKind == JsonValueKind.String && channel.GetString() == RenewalSupport.ChannelStaff ? RenewalSupport.ChannelStaff : null;
    }

    /// <summary>The day-count convention the artefact declares; unknown or missing conventions fail closed (PITFALLS 10).</summary>
    private async Task<Result<DayCountConvention>> DayCountAsync(string artefactHash, CancellationToken cancellationToken)
    {
        try
        {
            var artefact = await artefacts.Value.GetAsync(artefactHash, null, cancellationToken).ConfigureAwait(false);
            if (artefact.CanonicalJsonArtefact is { ValueKind: JsonValueKind.Object } root
                && root.TryGetProperty("dayCount", out var code) && code.ValueKind == JsonValueKind.String
                && DayCountConventions.TryParse(code.GetString()!.Replace('/', '_'), out var convention))
            {
                return convention;
            }
        }
        catch (DomainException ex) when (ex.Error.Code.Module != ModuleCode.POL)
        {
            return DomainError.Of(ModuleCode.POL, "RATING", $"The product artefact could not be read: {ex.Error.Code}.");
        }

        return DomainError.Of(ModuleCode.POL, "RATING", "The product artefact declares no known day-count convention; the renewal fails closed.");
    }

    private async Task<Result<T>> SaveAsync<T>(T value, CancellationToken cancellationToken)
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
            return pg.SqlState == PostgresErrorCodes.ExclusionViolation || pg.TableName is "segment" or "policy_term"
                ? DomainError.Of(ModuleCode.POL, "SEGMENT-INVARIANT", $"The renewal would break the policy timeline ({pg.ConstraintName}).")
                : JobSupport.Stale();
        }
    }

    private sealed record FrozenLine(
        string ElementLocator, string CoverageCode, string ChargeType, string ChargeCategory, decimal AnnualRate, decimal Amount, string? LegalStatus, bool? Provisional);
}

/// <summary>Audit facts of <c>pol.Renewal.accept</c>: the job, the new term and transaction, the acceptance.</summary>
internal sealed class AcceptRenewalAuditor : ICommandAuditor<AcceptRenewal, RenewalAcceptResponse>
{
    public CommandAuditFacts Describe(AcceptRenewal command, Result<RenewalAcceptResponse>? result)
    {
        if (result is not { IsSuccess: true } success || success.Value.Job is not { } job)
        {
            return new CommandAuditFacts();
        }

        var jobId = new JobId(Guid.Parse(job.GetProperty("jobId").GetString()!));
        var keys = BusinessKeys.Empty.With("jobId", jobId.Value.ToString()).With("expiringTermId", command.Request.TermId?.Value.ToString() ?? string.Empty);
        var accepted = job.TryGetProperty("result", out var detail) && detail.ValueKind == JsonValueKind.Object && detail.TryGetProperty("accepted", out var flag) && flag.GetBoolean();
        if (accepted)
        {
            keys = keys.With("policyTermId", detail.GetProperty("termId").GetString()!).With("transactionId", detail.GetProperty("transactionId").GetString()!);
        }

        return new CommandAuditFacts
        {
            ObjectRef = ObjectRef.For(ModuleCode.POL, "Job", jobId),
            ObjectNumber = job.GetProperty("jobNumber").GetString(),
            BusinessKeys = keys,
            Changes = AuditDiff.Compute(null, new { state = job.GetProperty("state").GetString(), accepted }),
        };
    }
}
