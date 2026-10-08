using CoreIns.Modules.Policy.Contracts;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Modules.Policy.Domain;
using CoreIns.Modules.Policy.Domain.Servicing;
using CoreIns.Modules.Policy.Persistence;
using CoreIns.Modules.Policy.Services;
using CoreIns.Modules.Product.Contracts;
using CoreIns.Modules.Rating.Contracts;
using CoreIns.Modules.Rating.Contracts.Api;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Policy.Commands.Change;

/// <summary>A change job's term as of now: history, base risk, the engine for the term and the replayed state.</summary>
internal sealed record ChangeContext(TermHistory History, SegmentRow BaseSegment, RiskTree BaseTree, ChangeEngine Engine, ServicingState State);

/// <summary>
/// Loads what every step of a change job needs (quote, preview, bind) with the same checks in the same order: the cover is not
/// cancelled (G1, REQ-POL-104), the term is Scheduled or InForce, the base transaction is still the term's head
/// (<c>POL-ERR-PREEMPTED</c>, D-SL3-11) and the effective time is in sequence (D-SL3-02).
/// </summary>
internal sealed class ChangeContextLoader(PolicyDbContext db, ChangeHistory history, ChangeEngineFactory engines, IOptions<PolicyOptions> options)
{
    public async Task<Result<ChangeContext>> LoadAsync(JobRow job, bool tracked, CancellationToken cancellationToken)
    {
        if (job.TargetTermId is not { } termId || job.BaseTransactionId is not { } baseTransaction)
        {
            return DomainError.Of(ModuleCode.POL, "VALIDATION", "The change job has no target term.");
        }

        var loaded = await history.LoadAsync(job.LegalEntityId, termId, tracked, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var term = loaded.Value;
        var refused = ChangeHistory.GuardState(term, job.EffectiveAt);
        if (refused is not null)
        {
            return refused;
        }

        if (term.Term.HeadTransactionId != baseTransaction)
        {
            return DomainError.Of(
                ModuleCode.POL, PolicyErrorNames.Preempted, "Another transaction was bound on the term since this change was started; start the change again.");
        }

        refused = ChangeHistory.GuardSequence(term, job.EffectiveAt);
        if (refused is not null)
        {
            return refused;
        }

        var segments = tracked ? db.Segments : db.Segments.AsNoTracking();
        var segment = await segments.SingleOrDefaultAsync(
            s => s.TermId == termId && s.RecordedTo == null && s.ValidFrom <= job.EffectiveAt && job.EffectiveAt < s.ValidTo, cancellationToken).ConfigureAwait(false);
        if (segment is null)
        {
            return DomainError.Of(ModuleCode.POL, "SEGMENT-INVARIANT", "The term has no current segment at the effective date.");
        }

        var zone = options.Value.Zone;
        var engine = await engines.CreateAsync(term.Term, job.EffectiveAt, zone, cancellationToken).ConfigureAwait(false);
        if (engine.IsFailure)
        {
            return engine.Error!;
        }

        var state = await history.ReplayAsync(term, engine.Value.Engine, engine.Value.Convention, zone, cancellationToken).ConfigureAwait(false);
        return state.IsFailure ? state.Error! : new ChangeContext(term, segment, JobSupport.FromJson<RiskTree>(segment.Snapshot), engine.Value, state.Value);
    }
}

/// <summary>A change job's version priced: the engine and tax result, the quote lines, RAT's response (when it was rated) and MKT's rounding.</summary>
internal sealed record PricedVersion(ChangePricing Pricing, List<ChargeLine> Lines, RateRateResponse? Rating, Currency Currency, PremiumRounding Rounding);

/// <summary>
/// <c>pol.Job.quote</c> for a change job (REQ-POL-192, -193, -195, -093; routed here by <c>QuoteJobHandler</c>). Validates the edit
/// against what a mid-term change may touch, rates the post-change risk once with RAT in ENDORSEMENT mode under the term's pinned
/// rating artefact, prices the remainder of the term with the servicing engine and the tax port and stores the result on the
/// quote version: one line per premium rate (new annual rate and the prorated NET delta) and per tax line. A quote creates no
/// transaction, delta, number or event (REQ-POL-129); a dry run runs the same path and rolls back.
/// </summary>
internal sealed class ChangeQuoteService(
    PolicyDbContext db,
    RequestContext context,
    ILegalEntityDirectory legalEntities,
    IClock clock,
    ChangeContextLoader loader,
    ChangePricer pricer,
    RatingInput ratingInput,
    Dependency<IRatingRateService> ratingService,
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

    public async Task<Result<JobQuoteResponse>> QuoteAsync(JobQuoteRequest request, CancellationToken cancellationToken)
    {
        var now = clock.Now;
        var loaded = await JobSupport.LoadAsync(db, JobSupport.LegalEntity(context, legalEntities), request.JobId, request.VersionNo, cancellationToken).ConfigureAwait(false);
        if (loaded is not var (job, version))
        {
            return JobSupport.NotFound("job or quote version");
        }

        if (version.VersionNo != job.CurrentVersionNo)
        {
            return JobSupport.Stale();
        }

        var quoted = JobSupport.Fire(job, JobTrigger.Quote);
        var versionQuoted = JobSupport.Fire(version, QuoteTrigger.Quote);
        if (quoted.IsFailure || versionQuoted.IsFailure)
        {
            return (quoted.IsFailure ? quoted.Error : versionQuoted.Error)!;
        }

        var priced = await PriceVersionAsync(job, version, rate: true, cancellationToken).ConfigureAwait(false);
        if (priced.IsFailure)
        {
            return priced.Error!;
        }

        var (pricing, lines, rating, _, rounding) = priced.Value;
        var validUntil = now.Plus(TimeSpan.FromDays(options.Value.QuoteValidityDays));
        version.WorksheetId = rating!.WorksheetId.Value;
        version.WorksheetHash = rating.WorksheetHash.Value;
        version.Bindable = true;
        version.Charges = JobSupport.Json(lines);
        version.Premium = pricing.Premium.Amount;
        version.Taxes = pricing.Taxes.Amount;
        version.Total = pricing.Total.Amount;
        version.Issues = JobSupport.Json(Array.Empty<UwIssue>());
        version.ConfigurationHash = (rating.ConfigurationHash ?? context.ConfigurationHash)?.Hash.Value;
        version.RecordVersion++;
        version.UpdatedAt = now;
        version.QuotedAt = now;
        version.ValidUntil = validUntil;
        version.State = Codes.Of(versionQuoted.Value);
        job.State = Codes.Of(quoted.Value);
        job.Referred = false;
        JobSupport.AddParticipant(job, context.Actor);
        job.RecordVersion++;
        job.UpdatedAt = now;

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return JobSupport.Stale();
        }

        return new JobQuoteResponse
        {
            JobId = job.JobId,
            QuoteId = version.QuoteId,
            VersionNo = version.VersionNo,
            State = Codes.Api(Codes.Parse<JobState>(job.State)),
            Decision = JobQuoteResponse.DecisionValue.Accept,
            Referred = false,
            Bindable = true,
            Premium = pricing.Premium,
            Taxes = pricing.Taxes,
            Total = pricing.Total,
            Charges = lines,
            WorksheetId = rating.WorksheetId,
            Issues = [],
            Warnings = QuoteWarnings.From([.. (rating.Warnings ?? []).Select(w => w.Code)], context.Language),
            ValidUntil = validUntil,
            ServicingPreview = pricing.ToPreview(rounding, (rating.ConfigurationHash ?? context.ConfigurationHash)?.Hash is { } hash ? new ConfigurationHash(hash) : null),
        };
    }

    /// <summary>
    /// The shared pricing step of quote and preview: the guards, the edit check, the rates (from RAT when <paramref name="rate"/>,
    /// else from the stored quote lines) and the engine plus tax pricing.
    /// </summary>
    internal async Task<Result<PricedVersion>> PriceVersionAsync(
        JobRow job, QuoteVersionRow version, bool rate, CancellationToken cancellationToken)
    {
        var zone = options.Value.Zone;
        var currency = Currency.FromCode(job.Currency);
        var context0 = await loader.LoadAsync(job, tracked: false, cancellationToken).ConfigureAwait(false);
        if (context0.IsFailure)
        {
            return context0.Error!;
        }

        var change = context0.Value;
        var tree = JobSupport.Tree(version);
        var edit = Check(change.BaseTree, tree);
        if (edit.IsFailure)
        {
            return edit.Error!;
        }

        IReadOnlyList<ChargeRate> rates;
        RateRateResponse? rating = null;
        if (rate)
        {
            var view = await ratingInput.BuildAsync(tree, job.EffectiveAt, cancellationToken).ConfigureAwait(false);
            if (view.IsFailure)
            {
                return view.Error!;
            }

            var rated = await RateAsync(job, version, view.Value, currency, zone, cancellationToken).ConfigureAwait(false);
            if (rated.IsFailure)
            {
                return rated.Error!;
            }

            (rating, rates) = rated.Value;
        }
        else
        {
            rates = Rates(JobSupport.FromJson<List<ChargeLine>>(version.Charges ?? "[]"));
        }

        var pricing = await pricer.PriceAsync(change.Engine.Engine, change.State, rates, job.EffectiveAt, job.JobId.Value.ToString(), job.Jurisdiction, zone, cancellationToken)
            .ConfigureAwait(false);
        if (pricing.IsFailure)
        {
            return pricing.Error!;
        }

        return new PricedVersion(pricing.Value, Lines(pricing.Value, currency), rating, currency, change.Engine.Rounding);
    }

    /// <summary>The risk diff of a version against the base, with the violations of what a change may touch as a validation error.</summary>
    internal Result<RiskDiff> Check(RiskTree baseTree, RiskTree tree)
    {
        var findings = RiskTrees.Findings(tree);
        if (findings.Count > 0)
        {
            return Validation("The draft is not complete.", findings.Select(f => new FieldError(f.Field, f.Code, "pol." + f.Code.ToLowerInvariant(), f.Message)));
        }

        var diff = RiskDiffs.Compute(baseTree, tree, changeOptions.Value.EditableVehicleFields);
        return diff.Violations.Count > 0
            ? Validation("The edit is not a change this endorsement allows.", diff.Violations.Select(v => new FieldError(v.Field, "CHANGE_NOT_ALLOWED", "pol.change_not_allowed", v.Message)))
            : diff;
    }

    /// <summary>The premium rates of a stored change quote (one per premium line; a removed element has the rate 0).</summary>
    internal static List<ChargeRate> Rates(IEnumerable<ChargeLine> lines) =>
        [.. lines.Where(l => l.ChargeCategory == ChargeCategories.Premium).Select(l => new ChargeRate(l.ElementLocator, l.CoverageCode, l.ChargeType, l.ChargeCategory, l.AnnualRate))];

    /// <summary>The quote lines: a premium line for every new rate and every delta (annual rate and prorated NET delta), then the tax lines.</summary>
    internal static List<ChargeLine> Lines(ChangePricing pricing, Currency currency)
    {
        var deltas = pricing.Deltas.ToDictionary(d => d.Key);
        var keys = pricing.NewRates.Select(r => r.Key).Union(deltas.Keys).Order().ToList();
        var rates = pricing.NewRates.ToDictionary(r => r.Key);
        var lines = new List<ChargeLine>();
        foreach (var key in keys)
        {
            deltas.TryGetValue(key, out var delta);
            lines.Add(new ChargeLine
            {
                ElementLocator = key.ElementLocator, CoverageCode = key.CoverageCode, ChargeType = key.ChargeType,
                ChargeCategory = rates.TryGetValue(key, out var rate) ? rate.ChargeCategory : delta!.ChargeCategory,
                AnnualRate = rate?.AnnualRate ?? 0m, Amount = new Money(delta?.Amount ?? 0m, currency),
            });
        }

        lines.AddRange(pricing.TaxLines.Select(t => new ChargeLine
        {
            ElementLocator = t.SourceKey.ElementLocator, CoverageCode = t.CoverageCode, ChargeType = t.ChargeType, ChargeCategory = t.ChargeCategory,
            AnnualRate = t.Rate, Amount = new Money(t.Amount, currency), LegalStatus = t.LegalStatus, Provisional = t.Provisional,
        }));
        return lines;
    }

    private async Task<Result<(RateRateResponse Response, List<ChargeRate> Rates)>> RateAsync(
        JobRow job, QuoteVersionRow version, RatingView view, Currency currency, TimeZoneInfo zone, CancellationToken cancellationToken)
    {
        RateRateResponse response;
        try
        {
            // ENDORSEMENT mode under the term's pinned rating artefact, never a floating one (REQ-POL-093, REQ-PFC-221).
            response = await ratingService.Value.RateAsync(
                new RateRateRequest
                {
                    Envelope = new RateRateRequest.EnvelopeDetail
                    {
                        LegalEntity = context.LegalEntity!.Value.Value,
                        Jurisdiction = job.Jurisdiction,
                        ProductCode = job.ProductCode,
                        ProductArtefactHash = Sha256Hash.Parse(job.ArtefactHash),
                        ProductVersion = ProductVersionNumber.Parse(job.ProductVersion),
                        RatingArtefactHash = job.RatingArtefactHash is null ? null : Sha256Hash.Parse(job.RatingArtefactHash),
                        Mode = RateRateRequest.EnvelopeDetail.ModeValue.Endorsement,
                        TransactionType = "Endorsement",
                        RatingBasisDate = job.EffectiveAt.ToBusinessDate(zone),
                        Currency = currency,
                        Channel = job.Channel,
                        ProducerCode = job.ProducerCode,
                        Lineage = new RateRateRequest.EnvelopeDetail.LineageDetail { QuoteId = version.QuoteId, JobId = job.JobId },
                        Origin = RateRateRequest.EnvelopeDetail.OriginValue.Live,
                    },
                    Segments =
                    [
                        new RateRateRequest.SegmentItem
                        {
                            SegmentId = version.QuoteId.Value.ToString("D"),
                            ValidPeriod = PolicyTime.Dates(job.EffectiveAt, job.ExpirationAt, zone),
                            RiskTree = view.Input,
                        },
                    ],
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException ex) when (ex.Error.Code.Module != ModuleCode.POL)
        {
            return DomainError.Of(ModuleCode.POL, "RATING", $"Rating failed: {ex.Error.Code} {ex.Error.Detail}");
        }

        if (job.RatingArtefactHash is null || response.RatingArtefactHash?.Value != job.RatingArtefactHash)
        {
            return DomainError.Of(ModuleCode.POL, "RATING", "RAT did not rate with the term's pinned rating artefact.");
        }

        var drafts = Charges.FromRating(response, currency, view.VehicleLocator);
        if (drafts.IsFailure)
        {
            return drafts.Error!;
        }

        return (response, [.. drafts.Value.Where(d => d.ChargeCategory == ChargeCategories.Premium)
            .Select(d => new ChargeRate(d.ElementLocator, d.CoverageCode, d.ChargeType, d.ChargeCategory, d.AnnualRate))]);
    }

    private static DomainError Validation(string message, IEnumerable<FieldError> errors) =>
        new(ErrorCode.For(ModuleCode.POL, "VALIDATION"), message) { FieldErrors = [.. errors] };
}

/// <summary>
/// The read side of a change (REQ-POL-192, -193): the diff of the edit against the base risk and, once a version is Quoted, the
/// servicing preview. It runs the code the bind runs (POL P8) and writes nothing.
/// </summary>
internal sealed class ChangePreviewService(PolicyDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, ChangeQuoteService quotes, ChangeContextLoader loader)
{
    public async Task<Result<(ServicingPreview? Preview, IReadOnlyList<DiffEntry> Diff)>> GetAsync(JobId jobId, int? versionNo, CancellationToken cancellationToken)
    {
        var legalEntity = JobSupport.LegalEntity(context, legalEntities);
        var job = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(j => j.JobId == jobId && j.LegalEntityId == legalEntity && j.JobType == ChangeNames.JobType, cancellationToken)
            .ConfigureAwait(false);
        if (job is null)
        {
            return JobSupport.NotFound("change job");
        }

        var version = await db.QuoteVersions.AsNoTracking().SingleOrDefaultAsync(v => v.JobId == jobId && v.VersionNo == (versionNo ?? job.CurrentVersionNo), cancellationToken)
            .ConfigureAwait(false);
        if (version is null)
        {
            return JobSupport.NotFound("quote version");
        }

        if (Codes.Parse<JobState>(job.State) == JobState.Bound)
        {
            return DomainError.Of(ModuleCode.POL, "ILLEGAL-TRANSITION", "The change is bound; there is nothing left to preview.");
        }

        var change = await loader.LoadAsync(job, tracked: false, cancellationToken).ConfigureAwait(false);
        if (change.IsFailure)
        {
            return change.Error!;
        }

        var diff = quotes.Check(change.Value.BaseTree, JobSupport.Tree(version));
        if (diff.IsFailure)
        {
            return diff.Error!;
        }

        if (Codes.Parse<QuoteState>(version.State) != QuoteState.Quoted)
        {
            return (null, diff.Value.Entries);
        }

        var priced = await quotes.PriceVersionAsync(job, version, rate: false, cancellationToken).ConfigureAwait(false);
        if (priced.IsFailure)
        {
            return priced.Error!;
        }

        var hash = version.ConfigurationHash is { } text ? ConfigurationHash.Parse(text) : (ConfigurationHash?)null;
        return (priced.Value.Pricing.ToPreview(priced.Value.Rounding, hash), diff.Value.Entries);
    }
}
