using System.Text.Json;
using CoreIns.Modules.Market.Contracts;
using CoreIns.Modules.Market.Contracts.Api;
using CoreIns.Modules.Policy.Contracts;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Modules.Policy.Contracts.Events;
using CoreIns.Modules.Policy.Domain;
using CoreIns.Modules.Policy.Persistence;
using CoreIns.Modules.Policy.Services;
using CoreIns.Modules.Product.Contracts;
using CoreIns.Modules.Product.Contracts.Api;
using CoreIns.Modules.Rating.Contracts;
using CoreIns.Modules.Rating.Contracts.Api;
using CoreIns.Modules.Underwriting.Contracts;
using CoreIns.Modules.Underwriting.Contracts.Api;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Policy.Commands;

/// <summary><c>pol.Job.quote</c>: validates, rates through RAT and evaluates UW at PRE_QUOTE (Draft → Quoted).</summary>
internal sealed record QuoteJob(JobQuoteRequest Request) : ICommand<JobQuoteResponse>;

internal sealed class QuoteJobValidator : AbstractValidator<QuoteJob>
{
    public QuoteJobValidator() => RuleFor(c => c.Request.VersionNo).GreaterThanOrEqualTo(1);
}

/// <summary>
/// Quotes a draft version (REQ-POL-011, REQ-POL-157):
/// <list type="number">
/// <item>local findings, PFC question-set knock-outs (REQ-POL-149) and <c>pfc.PolicyDraft.validate</c> (REQ-POL-295) block the quote;</item>
/// <item>RAT rates the risk tree once (<c>rat.Rate.rate</c>, rates not amounts, REQ-POL-115); premium, tax and levy lines
/// become charge lines rounded through <c>mkt.Rounding.apply</c> (REQ-POL-123, REQ-POL-124);</item>
/// <item>UW evaluates at PRE_QUOTE (<c>uw.Rules.evaluate</c>): open issues blocking PRE_QUOTE keep the job in Draft with
/// the Referred flag; issues blocking later points let the quote stand with the flag (bind gate); otherwise accept;</item>
/// <item>Draft → Quoted with a validity end (<c>pol.quote.validity_days</c>, REQ-POL-152) and <c>QuoteIssued</c> (REQ-POL-169).</item>
/// </list>
/// A quote never emits charge deltas, numbers or policy rows (REQ-POL-129). Dry-run runs the same path and rolls back.
/// </summary>
internal sealed partial class QuoteJobHandler(
    PolicyDbContext db,
    RequestContext context,
    ILegalEntityDirectory legalEntities,
    IClock clock,
    IEventPublisher events,
    Dependency<IProductQuestionSetService> questionSetService,
    Dependency<IProductPolicyDraftService> draftService,
    Dependency<IRatingRateService> ratingService,
    Dependency<IMarketRoundingService> roundingService,
    Dependency<IUnderwritingRulesService> underwritingService,
    RatingInput ratingInput,
    ILogger<QuoteJobHandler> logger,
    IOptions<PolicyOptions> options) : ICommandHandler<QuoteJob, JobQuoteResponse>
{
    public async Task<Result<JobQuoteResponse>> HandleAsync(QuoteJob command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var now = clock.Now;
        // Fail fast (POL-ERR-DEPENDENCY-UNAVAILABLE) before any work when a module is not wired yet.
        _ = (questionSetService.Value, ratingService.Value, roundingService.Value, underwritingService.Value);
        var loaded = await JobSupport.LoadAsync(db, JobSupport.LegalEntity(context, legalEntities), request.JobId, request.VersionNo, cancellationToken)
            .ConfigureAwait(false);
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

        var tree = JobSupport.Tree(version);
        var artefact = Sha256Hash.Parse(job.ArtefactHash);

        // 1. Validation: local findings, question-set knock-outs, PFC draft validation.
        var invalid = await ValidateAsync(tree, artefact, cancellationToken).ConfigureAwait(false);
        if (invalid is not null)
        {
            return invalid;
        }

        // 2. The rating input (with the driver's date of birth from PTY, never stored in POL), rating and charge lines.
        var view = await ratingInput.BuildAsync(tree, job.EffectiveAt, cancellationToken).ConfigureAwait(false);
        if (view.IsFailure)
        {
            return view.Error!;
        }

        var currency = Currency.FromCode(job.Currency);
        var rated = await RateAsync(job, version, view.Value, currency, cancellationToken).ConfigureAwait(false);
        if (rated.IsFailure)
        {
            return rated.Error!;
        }

        var (ratingResponse, charges) = rated.Value;
        var (premium, taxes, total) = Charges.Totals(charges, currency);

        // 3. Underwriting at PRE_QUOTE.
        var evaluated = await UwEvaluation.EvaluateAsync(
            underwritingService.Value, context, job, version, view.Value, RulesEvaluateRequest.CheckpointValue.PreQuote, options.Value.Zone, cancellationToken)
            .ConfigureAwait(false);
        if (evaluated.IsFailure)
        {
            return evaluated.Error!;
        }

        var (evaluation, issues) = evaluated.Value;
        var declined = evaluation.Outcome == RulesEvaluateResponse.OutcomeValue.Decline;
        var blockedAtQuote = declined || issues.Any(i => UwOutcome.Blocks(i, BlockingPoint.PreQuote));
        var referred = blockedAtQuote || UwOutcome.BlocksLater(issues) || evaluation.Outcome == RulesEvaluateResponse.OutcomeValue.Refer;

        // 4. Persist the version and move the job.
        var validUntil = now.Plus(TimeSpan.FromDays(options.Value.QuoteValidityDays));
        version.WorksheetId = ratingResponse.WorksheetId.Value;
        version.WorksheetHash = ratingResponse.WorksheetHash.Value;
        version.Bindable = ratingResponse.Bindable && job.QuoteType == "FULL";
        version.Charges = JobSupport.Json(charges);
        version.Premium = premium.Amount;
        version.Taxes = taxes.Amount;
        version.Total = total.Amount;
        version.Issues = JobSupport.Json(issues);
        version.UwEvaluationId = evaluation.EvaluationId;
        version.ConfigurationHash = (ratingResponse.ConfigurationHash ?? context.ConfigurationHash)?.Hash.Value;
        version.RecordVersion++;
        version.UpdatedAt = now;
        job.Referred = referred;
        job.RecordVersion++;
        job.UpdatedAt = now;

        if (!blockedAtQuote)
        {
            job.State = Codes.Of(quoted.Value);
            version.State = Codes.Of(versionQuoted.Value);
            version.QuotedAt = now;
            version.ValidUntil = validUntil;
            events.Publish(new OutgoingEvent(
                EventDescriptor.From(QuoteIssuedV1.Descriptor), "Policy", job.PolicyId.Value.ToString(),
                new QuoteIssuedV1
                {
                    JobId = job.JobId, JobType = job.JobType, QuoteVersion = version.VersionNo, ProductCode = job.ProductCode,
                    ProductVersion = ProductVersionNumber.Parse(job.ProductVersion),
                    PremiumSummary = new PremiumSummary { Premium = premium, Taxes = taxes, Total = total },
                    ValidUntil = validUntil.ToBusinessDate(options.Value.Zone), Referred = referred,
                },
                BusinessKeys.Empty.With("policyId", job.PolicyId.Value.ToString()).With("jobId", job.JobId.Value.ToString())
                    .With("quoteId", version.QuoteId.Value.ToString())));
        }

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
            Decision = declined ? JobQuoteResponse.DecisionValue.Decline
                : referred ? JobQuoteResponse.DecisionValue.Refer : JobQuoteResponse.DecisionValue.Accept,
            Referred = referred,
            Bindable = version.Bindable == true,
            Premium = premium,
            Taxes = taxes,
            Total = total,
            Charges = charges,
            WorksheetId = ratingResponse.WorksheetId,
            Issues = issues,
            Warnings = QuoteWarnings.From([.. (ratingResponse.Warnings ?? []).Select(w => w.Code), .. evaluation.Warnings ?? []], context.Language),
            ValidUntil = blockedAtQuote ? null : validUntil,
        };
    }

    private async Task<DomainError?> ValidateAsync(RiskTree tree, Sha256Hash artefact, CancellationToken cancellationToken)
    {
        var findings = RiskTrees.Findings(tree);
        if (findings.Count > 0)
        {
            return Validation("The draft is not complete.", findings.Select(f => new FieldError(f.Field, f.Code, "pol." + f.Code.ToLowerInvariant(), f.Message)));
        }

        foreach (var (set, index) in tree.QuestionSets.Select((s, i) => (s, i)))
        {
            var outcome = await questionSetService.Value.EvaluateAsync(
                new QuestionSetEvaluateRequest { Hash = artefact, Set = set.QuestionSetCode, Answers = AnswerTexts(set.Answers) },
                cancellationToken).ConfigureAwait(false);
            if (outcome.KnockOuts.Count > 0)
            {
                return Validation("A knock-out answer blocks the quote.", [new FieldError($"riskTree.questionSets[{index}]", "KNOCK_OUT", "pol.knock_out")]);
            }

            if (outcome.MissingRequired.Count > 0)
            {
                return Validation(
                    "Required questions are not answered.",
                    outcome.MissingRequired.Select(q => new FieldError($"riskTree.questionSets[{index}].answers.{q}", "QUESTION_REQUIRED", "pol.question_required")));
            }
        }

        // pfc.PolicyDraft.validate (REQ-POL-295). Fail closed when PFC does not provide it, unless the Development/test option allows it.
        var drafts = draftService.TryValue;
        if (drafts is null)
        {
            if (!options.Value.AllowMissingDraftValidation)
            {
                return DomainError.Of(ModuleCode.POL, "DEPENDENCY-UNAVAILABLE", "pfc.PolicyDraft.validate is not available; quoting fails closed (REQ-POL-295).");
            }

            LogDraftValidationSkipped(logger);
        }
        else
        {
            var validated = await drafts.ValidateAsync(
                new PolicyDraftValidateRequest
                {
                    Hash = artefact,
                    Checkpoint = JsonSerializer.SerializeToElement("QUOTE", SharedKernelJson.Options),
                    Draft = JsonSerializer.SerializeToElement(tree, SharedKernelJson.Options),
                },
                cancellationToken).ConfigureAwait(false);
            if (validated.Errors is { Count: > 0 } errors)
            {
                return Validation("The product rejects the draft.", errors.Select((e, i) => new FieldError($"riskTree[{i}]", "PRODUCT_RULE", "pol.product_rule", e.GetRawText())));
            }
        }

        return null;
    }

    private async Task<Result<(RateRateResponse Response, IReadOnlyList<ChargeLine> Charges)>> RateAsync(
        JobRow job, QuoteVersionRow version, RatingView view, Currency currency, CancellationToken cancellationToken)
    {
        var zone = options.Value.Zone;
        RateRateResponse response;
        try
        {
            // rat.Rate.rate writes its worksheet and RatingCalculated in this unit of work (they commit with the quote).
            // The rating artefact floats until the first rating, then stays pinned on the job (REQ-PFC-221).
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
                        Mode = job.QuoteType == "QUICK" ? RateRateRequest.EnvelopeDetail.ModeValue.Quick : RateRateRequest.EnvelopeDetail.ModeValue.Full,
                        TransactionType = "NewBusiness",
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
            return DomainError.Of(ModuleCode.POL, "RATING", "RAT did not rate with the job's rating artefact.");
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
                // Premium lines are rounded by MKT (purpose charge.line, REQ-POL-123); RAT's tax and levy lines arrive
                // already rounded on the rounded base (D-CON-11) and are taken as they are.
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

        // The charge lines must add up to RAT's totals: anything else is a rounding disagreement between RAT and MKT.
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

    /// <summary>PFC takes answers as text by question code: strings as they are, other JSON values in their JSON form.</summary>
    private static Dictionary<string, string> AnswerTexts(JsonElement answers) =>
        answers.ValueKind != JsonValueKind.Object
            ? []
            : answers.EnumerateObject().ToDictionary(
                p => p.Name,
                p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText(),
                StringComparer.Ordinal);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Quoting without pfc.PolicyDraft.validate: Policy:AllowMissingDraftValidation is set (Development and tests only).")]
    private static partial void LogDraftValidationSkipped(ILogger logger);

    private static DomainError Validation(string message, IEnumerable<FieldError> errors) =>
        new(ErrorCode.For(ModuleCode.POL, "VALIDATION"), message) { FieldErrors = [.. errors] };
}

/// <summary>Audit facts of <c>pol.Job.quote</c>: the job, version, totals, worksheet and outcome.</summary>
internal sealed class QuoteJobAuditor : ICommandAuditor<QuoteJob, JobQuoteResponse>
{
    public CommandAuditFacts Describe(QuoteJob command, Result<JobQuoteResponse>? result)
    {
        if (result is not { IsSuccess: true } success)
        {
            return new CommandAuditFacts { ObjectRef = ObjectRef.For(ModuleCode.POL, "Job", command.Request.JobId) };
        }

        var r = success.Value;
        return new CommandAuditFacts
        {
            ObjectRef = ObjectRef.For(ModuleCode.POL, "Job", r.JobId),
            BusinessKeys = BusinessKeys.Empty.With("jobId", r.JobId.Value.ToString()).With("quoteId", r.QuoteId.Value.ToString()),
            Changes = AuditDiff.Compute(null, new
            {
                versionNo = r.VersionNo, state = r.State.ToString(), decision = r.Decision.ToString(), total = r.Total.ToString(),
                worksheetId = r.WorksheetId.Value, issues = r.Issues.Count,
            }),
        };
    }
}
