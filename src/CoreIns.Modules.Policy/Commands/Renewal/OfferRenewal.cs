using CoreIns.Modules.Policy.Contracts;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Modules.Policy.Contracts.Events;
using CoreIns.Modules.Policy.Domain;
using CoreIns.Modules.Policy.Persistence;
using CoreIns.Modules.Policy.Services;
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
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Policy.Commands.Renewal;

/// <summary><c>pol.Renewal.offer</c>: rates the renewal, evaluates UW at PRE_BIND and offers it (Draft → Quoted.Offered).</summary>
internal sealed record OfferRenewal(RenewalOfferRequest Request) : ICommand<RenewalOfferResponse>;

internal sealed class OfferRenewalValidator : AbstractValidator<OfferRenewal>
{
    public OfferRenewalValidator() => RuleFor(c => c.Request.TermId.Value).NotEmpty().WithErrorCode("TERM_REQUIRED");
}

/// <summary>
/// Quotes and offers a renewal (REQ-POL-249 subset, REQ-POL-250 subset, REQ-POL-201), under the policy lock:
/// <list type="number">
/// <item>a Draft job is validated and rated in RENEWAL mode under the artefact PFC resolved for the new term start;
/// premium and tax lines are frozen on the quote version (Draft → Quoted);</item>
/// <item>UW evaluates at PRE_BIND through the same in-process path as new business; every actor on the job is a participant
/// (D-UW-01). An open issue blocking PRE_BIND leaves the job Quoted and Referred, <b>not</b> Offered: no
/// <c>RenewalOffered</c> goes out until the referral is decided and the offer is made again;</item>
/// <item>unblocked, the job becomes <c>Quoted.Offered</c> and <c>RenewalOffered</c> is published (acceptance mode EXPLICIT;
/// no document, no notice clock; the offer stands until the expiring term ends).</item>
/// </list>
/// Editing an offered job (<c>pol.Job.updateDraft</c>) supersedes the quoted version and keeps it; offering again rates the
/// new version, so earlier offers are kept (REQ-POL-255, minimal).
/// </summary>
internal sealed class OfferRenewalHandler(
    PolicyDbContext db,
    RequestContext context,
    ILegalEntityDirectory legalEntities,
    IClock clock,
    IEventPublisher events,
    Dependency<IUnderwritingRulesService> underwriting,
    RatingInput ratingInput,
    RenewalPricing pricing,
    IOptions<PolicyOptions> options) : ICommandHandler<OfferRenewal, RenewalOfferResponse>
{
    private const int MaxVersions = 20;

    public async Task<Result<RenewalOfferResponse>> HandleAsync(OfferRenewal command, CancellationToken cancellationToken)
    {
        var termId = command.Request.TermId;
        if (command.Request.AcceptanceMode is { } mode && mode != RenewalSupport.AcceptanceModeExplicit)
        {
            return RenewalSupport.Validation("acceptanceMode", "ACCEPTANCE_MODE_NOT_SUPPORTED", "Only explicit acceptance (EXPLICIT) is supported.");
        }

        var legalEntity = JobSupport.LegalEntity(context, legalEntities);
        var zone = options.Value.Zone;
        _ = underwriting.Value; // fail fast (POL-ERR-DEPENDENCY-UNAVAILABLE) when UW is not wired
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
        var job = await RenewalSupport.RenewalJobAsync(db, legalEntity, command.Request.JobId, termId, cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            return JobSupport.NotFound("renewal job of the term");
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

        var version = await RenewalSupport.CurrentVersionAsync(db, job, cancellationToken).ConfigureAwait(false);
        var state = Codes.Parse<JobState>(job.State);

        // Rebase (REQ-POL-246, D-SL3-10): a change or cancellation was bound on the expiring term after this renewal was created or
        // last offered, so its base is no longer the head. The risk tree is copied again from the current head's segment at expiry,
        // the base moves to the head, and the offer is rated afresh; a quote version already issued is superseded and kept.
        if (job.BaseTransactionId != term.HeadTransactionId)
        {
            var current = await RenewalSupport.RiskTreeAtExpiryAsync(db, termId, term.ValidTo, cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                return DomainError.Of(ModuleCode.POL, "SEGMENT-INVARIANT", "The expiring term has no segment at its end.");
            }

            if (state == JobState.Quoted)
            {
                var edit = JobSupport.Fire(job, JobTrigger.Edit);
                var superseded = JobSupport.Fire(version, QuoteTrigger.Supersede);
                if (edit.IsFailure || superseded.IsFailure)
                {
                    return JobSupport.Stale();
                }

                if (version.VersionNo >= MaxVersions)
                {
                    return DomainError.Of(ModuleCode.POL, "VALIDATION", $"A job holds at most {MaxVersions} quote versions.");
                }

                job.State = Codes.Of(edit.Value);
                version.State = Codes.Of(superseded.Value);
                version.RecordVersion++;
                version.UpdatedAt = now;
                version = new QuoteVersionRow
                {
                    QuoteId = QuoteId.New(), JobId = job.JobId, LegalEntityId = job.LegalEntityId, VersionNo = version.VersionNo + 1,
                    State = Codes.Of(QuoteStateModel.Machine.Start(QuoteState.Draft).Value), DraftVersion = 0, RiskTree = current,
                    RecordVersion = 1, CreatedAt = now, UpdatedAt = now,
                };
                db.QuoteVersions.Add(version);
                job.CurrentVersionNo = version.VersionNo;
            }
            else
            {
                version.RiskTree = current;
                version.DraftVersion++;
                version.UpdatedAt = now;
            }

            job.BaseTransactionId = term.HeadTransactionId;
            job.SubState = null;
            job.Referred = false;
            state = JobState.Draft;
        }
        var currency = Currency.FromCode(job.Currency);
        if (state is not (JobState.Draft or JobState.Quoted))
        {
            return DomainError.Of(ModuleCode.POL, "ILLEGAL-TRANSITION", $"A {state} renewal cannot be offered.");
        }

        var tree = JobSupport.Tree(version);
        var view = await ratingInput.BuildAsync(tree, job.EffectiveAt, cancellationToken).ConfigureAwait(false);
        if (view.IsFailure)
        {
            return view.Error!;
        }

        IReadOnlyList<ChargeLine> charges;
        if (state == JobState.Draft)
        {
            var quoted = JobSupport.Fire(job, JobTrigger.Quote);
            var versionQuoted = JobSupport.Fire(version, QuoteTrigger.Quote);
            if (quoted.IsFailure || versionQuoted.IsFailure)
            {
                return (quoted.IsFailure ? quoted.Error : versionQuoted.Error)!;
            }

            var invalid = await pricing.ValidateAsync(tree, Sha256Hash.Parse(job.ArtefactHash), cancellationToken).ConfigureAwait(false);
            if (invalid is not null)
            {
                return invalid;
            }

            var rated = await pricing.RateAsync(job, version, view.Value, currency, cancellationToken).ConfigureAwait(false);
            if (rated.IsFailure)
            {
                return rated.Error!;
            }

            var (response, rates) = rated.Value;
            charges = rates;
            var (premium, taxes, total) = Charges.Totals(charges, currency);
            version.WorksheetId = response.WorksheetId.Value;
            version.WorksheetHash = response.WorksheetHash.Value;
            version.Bindable = response.Bindable;
            version.Charges = JobSupport.Json(charges);
            version.Premium = premium.Amount;
            version.Taxes = taxes.Amount;
            version.Total = total.Amount;
            version.ConfigurationHash = (response.ConfigurationHash ?? context.ConfigurationHash)?.Hash.Value;
            version.State = Codes.Of(versionQuoted.Value);
            version.QuotedAt = now;
            // The offer stands until the expiring term ends (explicit acceptance, no notice clock); a price change of the
            // configuration before then is caught at acceptance.
            version.ValidUntil = term.ValidTo;
            job.State = Codes.Of(quoted.Value);
        }
        else if (state == JobState.Quoted && job.SubState != Codes.Of(JobSubState.Offered))
        {
            // Quoted but not offered: the offer was held back by a UW referral; its decision flows back here without re-entry.
            charges = JobSupport.FromJson<List<ChargeLine>>(version.Charges!);
        }
        else
        {
            return DomainError.Of(ModuleCode.POL, "ILLEGAL-TRANSITION", $"A {state} renewal that is already offered cannot be offered again; edit it first.");
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
        version.UwEvaluationId = evaluation.EvaluationId;
        version.RecordVersion++;
        version.UpdatedAt = now;
        job.Referred = blocked;
        job.SubState = blocked ? null : Codes.Of(JobSubState.Offered);
        job.RecordVersion++;
        job.UpdatedAt = now;

        if (!blocked)
        {
            var (premium, taxes, total) = Charges.Totals(charges, currency);
            events.Publish(new OutgoingEvent(
                EventDescriptor.From(RenewalOfferedV1.Descriptor), "Policy", policyId.Value.ToString(),
                new RenewalOfferedV1
                {
                    JobId = job.JobId,
                    OfferVersion = version.VersionNo,
                    PremiumSummary = new PremiumSummary { Premium = premium, Taxes = taxes, Total = total },
                    AcceptanceMode = RenewalSupport.AcceptanceModeExplicit,
                    Deadline = term.ValidTo,
                    PredecessorTermId = termId.Value,
                    NewTermNumber = term.TermNumber + 1,
                },
                BusinessKeys.Empty.With("policyId", policyId.Value.ToString()).With("jobId", job.JobId.Value.ToString())
                    .With("quoteId", version.QuoteId.Value.ToString()))
            {
                OccurredAt = now,
            });
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return JobSupport.Stale();
        }

        var (offerPremium, offerTaxes, offerTotal) = Charges.Totals(charges, currency);
        return new RenewalOfferResponse
        {
            JobId = job.JobId,
            State = Codes.Api(Codes.Parse<JobState>(job.State)),
            OfferVersion = version.VersionNo,
            PremiumSummary = new PremiumSummary { Premium = offerPremium, Taxes = offerTaxes, Total = offerTotal },
            AcceptanceMode = RenewalSupport.AcceptanceModeExplicit,
            Deadline = term.ValidTo,
            Referred = blocked,
        };
    }
}

/// <summary>Audit facts of <c>pol.Renewal.offer</c>.</summary>
internal sealed class OfferRenewalAuditor : ICommandAuditor<OfferRenewal, RenewalOfferResponse>
{
    public CommandAuditFacts Describe(OfferRenewal command, Result<RenewalOfferResponse>? result)
    {
        if (result is not { IsSuccess: true } success)
        {
            return new CommandAuditFacts { ObjectRef = ObjectRef.For(ModuleCode.POL, "Job", command.Request.JobId) };
        }

        var response = success.Value;
        return new CommandAuditFacts
        {
            ObjectRef = ObjectRef.For(ModuleCode.POL, "Job", response.JobId),
            BusinessKeys = BusinessKeys.Empty.With("jobId", response.JobId.Value.ToString()).With("expiringTermId", command.Request.TermId.Value.ToString()),
            Changes = AuditDiff.Compute(null, new { state = response.State.ToString(), offerVersion = response.OfferVersion }),
        };
    }
}
