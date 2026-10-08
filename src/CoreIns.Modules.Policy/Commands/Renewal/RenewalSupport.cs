using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using CoreIns.Modules.Market.Contracts;
using CoreIns.Modules.Market.Contracts.Api;
using CoreIns.Modules.Policy.Contracts;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Modules.Policy.Domain;
using CoreIns.Modules.Policy.Persistence;
using CoreIns.Modules.Policy.Services;
using CoreIns.Modules.Product.Contracts;
using CoreIns.Modules.Product.Contracts.Api;
using CoreIns.Modules.Rating.Contracts;
using CoreIns.Modules.Rating.Contracts.Api;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Policy.Commands.Renewal;

/// <summary>
/// Settings of manual renewal (PRD-05 §10.2). <c>pol.renewal.lead_days</c> is a configuration value of MKT's resolver in the
/// PRD; until that resolver serves it, it is read from <c>Policy:Renewal:LeadDays</c>. The default 45 is ILLUSTRATIVE
/// (D-SL3-08), not an approved commercial value.
/// </summary>
internal sealed class RenewalOptions
{
    public const string Section = "Policy:Renewal";

    /// <summary>A renewal can be created when the expiring term ends within this many Athens calendar days (REQ-POL-245).</summary>
    [Range(1, 365)]
    public int LeadDays { get; set; } = 45;
}

/// <summary>Error codes of the renewal commands that no other POL work package registers.</summary>
internal static class RenewalErrors
{
    /// <summary>The expiring term moved since the renewal was created (a change or cancellation was bound): offer again on the new head.</summary>
    public static ErrorDefinition[] Definitions { get; } =
    [
        ErrorDefinition.For(ModuleCode.POL, PolicyErrorNames.RebaseRequired, 409, "Ο όρος άλλαξε μετά τη δημιουργία της ανανέωσης", "The term changed after the renewal was created")
            .Describe(
                "Μετά τη δημιουργία της ανανέωσης δεσμεύτηκε αλλαγή στον τρέχοντα όρο. Κάντε νέα προσφορά· ξαναχτίζεται πάνω στην τελευταία κατάσταση.",
                "A change was bound on the expiring term after the renewal was created. Offer the renewal again; it is rebuilt on the latest state."),
    ];
}

/// <summary>Shared lookups and views of the renewal commands.</summary>
internal static class RenewalSupport
{
    public const string AcceptanceModeExplicit = "EXPLICIT";

    public const string ChannelStaff = "STAFF";

    /// <summary>The producer of record sentinel for a term written without a producer code (the event field is always set, PITFALLS 12).</summary>
    public const string DirectProducer = "DIRECT";

    /// <summary>The policy of a term in the caller's legal entity (read before the lock; null when unknown).</summary>
    public static async Task<PolicyId?> PolicyOfAsync(PolicyDbContext db, LegalEntityId legalEntity, PolicyTermId termId, CancellationToken cancellationToken)
    {
        var rows = await db.Terms.AsNoTracking().Where(t => t.TermId == termId && t.LegalEntityId == legalEntity).Select(t => t.PolicyId).Take(1)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary>The current (not end-dated) version of a term, tracked.</summary>
    public static Task<PolicyTermRow?> CurrentTermAsync(PolicyDbContext db, LegalEntityId legalEntity, PolicyTermId termId, CancellationToken cancellationToken) =>
        db.Terms.SingleOrDefaultAsync(t => t.TermId == termId && t.LegalEntityId == legalEntity && t.RecordedTo == null, cancellationToken);

    /// <summary>The renewal job <paramref name="jobId"/> of the expiring term <paramref name="termId"/>, tracked; null when it is not one of this term's.</summary>
    public static Task<JobRow?> RenewalJobAsync(PolicyDbContext db, LegalEntityId legalEntity, JobId jobId, PolicyTermId termId, CancellationToken cancellationToken) =>
        db.Jobs.SingleOrDefaultAsync(
            j => j.JobId == jobId && j.LegalEntityId == legalEntity && j.JobType == Codes.Of(JobType.Renewal) && j.ExpiringTermId == termId, cancellationToken);

    /// <summary>
    /// The risk tree valid at the end of the expiring term (half-open: the current segment that ends at the expiry), as known now;
    /// null when the term has none.
    /// </summary>
    public static async Task<string?> RiskTreeAtExpiryAsync(PolicyDbContext db, PolicyTermId termId, Instant expiry, CancellationToken cancellationToken)
    {
        var segments = await db.Segments.AsNoTracking()
            .Where(s => s.TermId == termId && s.RecordedTo == null && s.ValidFrom < expiry && s.ValidTo >= expiry)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return segments.OrderByDescending(s => s.ValidFrom).FirstOrDefault()?.Snapshot;
    }

    /// <summary>The quote version a renewal job currently points to, tracked.</summary>
    public static Task<QuoteVersionRow> CurrentVersionAsync(PolicyDbContext db, JobRow job, CancellationToken cancellationToken) =>
        db.QuoteVersions.SingleAsync(v => v.JobId == job.JobId && v.VersionNo == job.CurrentVersionNo, cancellationToken);

    /// <summary>A term can be renewed while it is Scheduled or InForce; anything else (cancelled, pending cancellation, voided, expired…) cannot.</summary>
    public static DomainError? NotRenewable(PolicyTermRow term)
    {
        var state = Codes.Parse<PolicyTermState>(term.State);
        return state is PolicyTermState.Scheduled or PolicyTermState.InForce
            ? null
            : DomainError.Of(ModuleCode.POL, "ILLEGAL-TRANSITION", $"A {state} term cannot be renewed.");
    }

    public static DomainError Validation(string field, string code, string message) =>
        new(ErrorCode.For(ModuleCode.POL, PolicyErrorNames.Validation), message) { FieldErrors = [new FieldError(field, code, "pol." + code.ToLowerInvariant())] };

    }

/// <summary>
/// Validation and rating of a renewal's risk tree (the renewal analogue of <c>pol.Job.quote</c>, REQ-POL-249): local
/// findings, question-set knock-outs and <c>pfc.PolicyDraft.validate</c> block the offer; <c>rat.Rate.rate</c> rates the
/// copied risk tree for transaction type Renewal under the rating artefact PFC resolved for the new term start, and the
/// premium lines are rounded by MKT (<c>charge.line</c>). Rates cross the RAT boundary, taxes come rounded from RAT.
/// </summary>
internal sealed partial class RenewalPricing(
    RequestContext context,
    Dependency<IProductQuestionSetService> questionSetService,
    Dependency<IProductPolicyDraftService> draftService,
    Dependency<IRatingRateService> ratingService,
    Dependency<IMarketRoundingService> roundingService,
    ILogger<RenewalPricing> logger,
    IOptions<PolicyOptions> options)
{
    public async Task<DomainError?> ValidateAsync(RiskTree tree, Sha256Hash artefact, CancellationToken cancellationToken)
    {
        var findings = RiskTrees.Findings(tree);
        if (findings.Count > 0)
        {
            return Invalid("The renewal's risk data is not complete.", findings.Select(f => new FieldError(f.Field, f.Code, "pol." + f.Code.ToLowerInvariant(), f.Message)));
        }

        foreach (var (set, index) in tree.QuestionSets.Select((s, i) => (s, i)))
        {
            var outcome = await questionSetService.Value.EvaluateAsync(
                new QuestionSetEvaluateRequest { Hash = artefact, Set = set.QuestionSetCode, Answers = AnswerTexts(set.Answers) }, cancellationToken).ConfigureAwait(false);
            if (outcome.KnockOuts.Count > 0)
            {
                return Invalid("A knock-out answer blocks the renewal.", [new FieldError($"riskTree.questionSets[{index}]", "KNOCK_OUT", "pol.knock_out")]);
            }

            if (outcome.MissingRequired.Count > 0)
            {
                return Invalid(
                    "Required questions of the renewal product version are not answered.",
                    outcome.MissingRequired.Select(q => new FieldError($"riskTree.questionSets[{index}].answers.{q}", "QUESTION_REQUIRED", "pol.question_required")));
            }
        }

        var drafts = draftService.TryValue;
        if (drafts is null)
        {
            if (!options.Value.AllowMissingDraftValidation)
            {
                return DomainError.Of(ModuleCode.POL, "DEPENDENCY-UNAVAILABLE", "pfc.PolicyDraft.validate is not available; the offer fails closed (REQ-POL-295).");
            }

            LogDraftValidationSkipped(logger);
            return null;
        }

        var validated = await drafts.ValidateAsync(
            new PolicyDraftValidateRequest
            {
                Hash = artefact,
                Checkpoint = JsonSerializer.SerializeToElement("QUOTE", SharedKernelJson.Options),
                Draft = JsonSerializer.SerializeToElement(tree, SharedKernelJson.Options),
            },
            cancellationToken).ConfigureAwait(false);
        return validated.Errors is { Count: > 0 } errors
            ? Invalid("The product rejects the renewal.", errors.Select((e, i) => new FieldError($"riskTree[{i}]", "PRODUCT_RULE", "pol.product_rule", e.GetRawText())))
            : null;
    }

    public async Task<Result<(RateRateResponse Response, IReadOnlyList<ChargeLine> Charges)>> RateAsync(
        JobRow job, QuoteVersionRow version, RatingView view, Currency currency, CancellationToken cancellationToken)
    {
        var zone = options.Value.Zone;
        RateRateResponse response;
        try
        {
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
                        Mode = RateRateRequest.EnvelopeDetail.ModeValue.Full,
                        TransactionType = "Renewal",
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

        var pinned = response.RatingArtefactHash?.Value ?? job.RatingArtefactHash;
        if (pinned is null || (job.RatingArtefactHash is not null && pinned != job.RatingArtefactHash))
        {
            return DomainError.Of(ModuleCode.POL, "RATING", "RAT did not rate with the renewal job's rating artefact.");
        }

        job.RatingArtefactHash = pinned;
        var drafts = Charges.FromRating(response, currency, view.VehicleLocator);
        if (drafts.IsFailure)
        {
            return drafts.Error!;
        }

        var lines = new List<ChargeLine>();
        foreach (var draft in drafts.Value)
        {
            var amount = draft.Amount;
            if (amount is null)
            {
                var rounded = await RoundAsync(draft, currency, job, zone, cancellationToken).ConfigureAwait(false);
                if (rounded.IsFailure)
                {
                    return rounded.Error!;
                }

                amount = rounded.Value;
            }

            lines.Add(new ChargeLine
            {
                ElementLocator = draft.ElementLocator, CoverageCode = draft.CoverageCode, ChargeType = draft.ChargeType,
                ChargeCategory = draft.ChargeCategory, AnnualRate = draft.AnnualRate, Amount = amount.Value,
                LegalStatus = draft.LegalStatus, Provisional = draft.Provisional,
            });
        }

        if (response.GrossTotal is { } gross && Charges.Totals(lines, currency).Total != gross)
        {
            return DomainError.Of(ModuleCode.POL, "RATING", $"The charge lines do not add up to RAT's gross total {gross}.");
        }

        return (response, lines);
    }

    private async Task<Result<Money>> RoundAsync(ChargeDraft draft, Currency currency, JobRow job, TimeZoneInfo zone, CancellationToken cancellationToken)
    {
        RoundingApplyResponse rounded;
        try
        {
            rounded = await roundingService.Value.ApplyAsync(
                new RoundingApplyRequest
                {
                    Amount = new Money(draft.AnnualRate, currency), Currency = currency, Purpose = "charge.line",
                    Context = new RoundingApplyRequest.ContextDetail { LegalEntity = context.LegalEntity!.Value.Value, ValidAt = job.EffectiveAt.ToBusinessDate(zone) },
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException ex) when (ex.Error.Code.Module != ModuleCode.POL)
        {
            return DomainError.Of(ModuleCode.POL, "RATING", $"Rounding failed: {ex.Error.Code}.");
        }

        return rounded.AmountAfterRounding is { } amount && amount.Currency == currency
            ? amount
            : DomainError.Of(ModuleCode.POL, "RATING", $"MKT rounding returned no {currency} amount for {draft.ChargeType}.");
    }

    private static Dictionary<string, string> AnswerTexts(JsonElement answers) =>
        answers.ValueKind != JsonValueKind.Object
            ? []
            : answers.EnumerateObject().ToDictionary(
                p => p.Name,
                p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText(),
                StringComparer.Ordinal);

    private static DomainError Invalid(string message, IEnumerable<FieldError> errors) =>
        new(ErrorCode.For(ModuleCode.POL, PolicyErrorNames.Validation), message) { FieldErrors = [.. errors] };

    [LoggerMessage(Level = LogLevel.Warning, Message = "Offering a renewal without pfc.PolicyDraft.validate: Policy:AllowMissingDraftValidation is set (Development and tests only).")]
    private static partial void LogDraftValidationSkipped(ILogger logger);
}
