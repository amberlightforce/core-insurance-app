using CoreIns.Modules.Documents.Contracts;
using CoreIns.Modules.Policy.Commands.Cancellation.NonPayment;
using CoreIns.Modules.Policy.Contracts;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Modules.Policy.Contracts.Events;
using CoreIns.Modules.Policy.Domain;
using CoreIns.Modules.Policy.Persistence;
using CoreIns.Modules.Policy.Services;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Policy.Commands.Renewal;

/// <summary><c>pol.Renewal.nonRenew</c>: the insurer decides not to renew a term and sends the statutory notice.</summary>
internal sealed record NonRenew(RenewalNonRenewRequest Request) : ICommand<RenewalNonRenewResponse>;

internal sealed class NonRenewValidator : AbstractValidator<NonRenew>
{
    public NonRenewValidator()
    {
        RuleFor(c => c.Request.TermId.Value).NotEmpty().WithErrorCode("TERM_REQUIRED");
        RuleFor(c => c.Request.ReasonCode).NotEmpty().MaximumLength(64).WithErrorCode("REASON_REQUIRED");
        RuleFor(c => c.Request.Text).MaximumLength(1000).WithErrorCode("TEXT_TOO_LONG");
    }
}

/// <summary>Statuses of a <see cref="NonRenewalRow"/> and the DOC template of the notice.</summary>
internal static class NonRenewalStatus
{
    public const string NoticeRequested = "NOTICE_REQUESTED";
    public const string PsrReview = "PSR_REVIEW";
    public const string NoticeTemplate = "DT-NONRENEWAL-NOTICE";
}

/// <summary>
/// Records the insurer's non-renewal of a term (REQ-POL-009), under the policy lock:
/// <list type="number">
/// <item>the term is Scheduled or InForce, has not ended, has no successor term and no earlier non-renewal;</item>
/// <item>the notice period (<c>Policy:Renewal:NonRenewalNoticeDays</c>) is an unsettled legal value (Unverified by default): Production
/// refuses the command until it is Settled (D-USR-19); the notice is too late when the latest notice day (term end minus the period, Athens
/// days) has passed (<c>POL-ERR-NOTICE-TOO-LATE</c>);</item>
/// <item>an open renewal job of the term is closed (draft withdrawn, offered declined);</item>
/// <item>a vulnerable or unknown policyholder goes to the PSR queue and nothing is sent (NX-HD-02); otherwise the notice letter is requested
/// from DOC and <c>PolicyNonRenewed</c> is emitted.</item>
/// </list>
/// Dry-run computes the same and rolls back; it requests no document.
/// </summary>
internal sealed class NonRenewHandler(
    PolicyDbContext db,
    RequestContext context,
    ILegalEntityDirectory legalEntities,
    IClock clock,
    IEventPublisher events,
    IHostEnvironment environment,
    IVulnerabilityCheck vulnerability,
    PsrWorkTasks psrTasks,
    PartyDisplayName partyNames,
    Dependency<IDocumentRequestService> documents,
    IOptions<PolicyOptions> options,
    IOptions<RenewalOptions> renewalOptions) : ICommandHandler<NonRenew, RenewalNonRenewResponse>
{
    public async Task<Result<RenewalNonRenewResponse>> HandleAsync(NonRenew command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = JobSupport.LegalEntity(context, legalEntities);
        var zone = options.Value.Zone;
        var settings = renewalOptions.Value;
        if (environment.IsProduction() && !string.Equals(settings.NonRenewalNoticeLegalStatus, "Settled", StringComparison.Ordinal))
        {
            return DomainError.Of(
                ModuleCode.POL, "NOTICE-PERIOD-UNSETTLED",
                $"The statutory non-renewal notice period is legally {settings.NonRenewalNoticeLegalStatus}; non-renewal is refused in Production until it is Settled.");
        }

        if (await RenewalSupport.PolicyOfAsync(db, legalEntity, request.TermId, cancellationToken).ConfigureAwait(false) is not { } policyId)
        {
            return JobSupport.NotFound("term");
        }

        var locked = await PolicyWriteLock.AcquireAsync(db, clock, options.Value.LockWait, legalEntity, policyId, cancellationToken).ConfigureAwait(false);
        if (locked.IsFailure)
        {
            return locked.Error!;
        }

        var now = locked.Value;
        var term = await RenewalSupport.CurrentTermAsync(db, legalEntity, request.TermId, cancellationToken).ConfigureAwait(false);
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
            return RenewalSupport.Validation("termId", "TERM_ENDED", "The term has ended; it can no longer be non-renewed.");
        }

        if (await db.Terms.AnyAsync(t => t.PredecessorTermId == request.TermId && t.RecordedTo == null, cancellationToken).ConfigureAwait(false))
        {
            return DomainError.Of(ModuleCode.POL, "ILLEGAL-TRANSITION", "The term has already been renewed.");
        }

        if (await db.NonRenewals.AnyAsync(n => n.TermId == request.TermId, cancellationToken).ConfigureAwait(false))
        {
            return DomainError.Of(ModuleCode.POL, "ILLEGAL-TRANSITION", "The term has already been non-renewed.");
        }

        // The latest day the notice can be given: the term end's Athens date minus the notice period. Inclusive: the next day is too late.
        var lastNoticeDay = term.ValidTo.ToBusinessDate(zone).AddDays(-settings.NonRenewalNoticeDays);
        var deadline = lastNoticeDay.AddDays(1).StartOfDayIn(zone);
        if (now >= deadline)
        {
            return DomainError.Of(
                ModuleCode.POL, "NOTICE-TOO-LATE",
                $"The notice must be given {settings.NonRenewalNoticeDays} days before the term ends; the last day was {lastNoticeDay}.");
        }

        var openJobs = await db.Jobs.Where(
                j => j.ExpiringTermId == request.TermId && j.LegalEntityId == legalEntity && j.JobType == Codes.Of(JobType.Renewal)
                     && (j.State == Codes.Of(JobState.Draft) || j.State == Codes.Of(JobState.Quoted)))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var job in openJobs)
        {
            var fired = JobSupport.Fire(job, Codes.Parse<JobState>(job.State) == JobState.Draft ? JobTrigger.Withdraw : JobTrigger.Decline);
            if (fired.IsFailure)
            {
                return fired.Error!;
            }

            job.State = Codes.Of(fired.Value);
            job.ReasonCode = request.ReasonCode;
            job.RecordVersion++;
            job.UpdatedAt = now;
        }

        var policy = await db.Policies.AsNoTracking().SingleAsync(p => p.PolicyId == policyId, cancellationToken).ConfigureAwait(false);
        var psr = await vulnerability.IsVulnerableAsync(policy.PolicyholderPartyId, cancellationToken).ConfigureAwait(false);
        var row = new NonRenewalRow
        {
            NonRenewalId = Guid.CreateVersion7(), LegalEntityId = legalEntity, PolicyId = policyId, TermId = request.TermId, ReasonCode = request.ReasonCode,
            Note = request.Text, Status = psr ? NonRenewalStatus.PsrReview : NonRenewalStatus.NoticeRequested, EffectiveAt = term.ValidTo, NoticeDeadline = deadline,
            NoticeDays = settings.NonRenewalNoticeDays, LegalStatus = settings.NonRenewalNoticeLegalStatus, RequestedBy = context.Actor.ToString(), CreatedAt = now,
        };
        db.NonRenewals.Add(row);

        if (!psr)
        {
            // TODO(hardening): the document id is only known after the DOC call; a retry after a failed save requests the letter again
            // (DOC de-duplicates on template + request number, so it is not rendered twice).
            if (!context.DryRun && documents.TryValue is { } docs)
            {
                var outcome = await docs.RequestAsync(
                    new DocumentRequestSpec(
                        NonRenewalStatus.NoticeTemplate,
                        row.NonRenewalId.ToString("D"),
                        new ObjectRef(ModuleCode.POL, "Policy", policyId.Value.ToString("D")),
                        policy.PolicyholderPartyId,
                        "el",
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["customerName"] = await partyNames.ResolveAsync(policy.PolicyholderPartyId, cancellationToken).ConfigureAwait(false),
                            ["policyNo"] = policy.PolicyNumber.ToString(),
                            ["expiryDate"] = term.ValidTo.ToBusinessDate(zone).ToString(),
                        }),
                    cancellationToken).ConfigureAwait(false);
                row.NoticeDocumentId = outcome.DocumentId.Value;
            }

            events.Publish(new OutgoingEvent(
                EventDescriptor.From(PolicyNonRenewedV1.Descriptor), "Policy", policyId.Value.ToString(),
                new PolicyNonRenewedV1 { TermId = request.TermId, Source = "INSURER", Reason = request.ReasonCode, NoticeDate = now.ToBusinessDate(zone) },
                BusinessKeys.Empty.With("policyId", policyId.Value.ToString()).With("policyTermId", request.TermId.Value.ToString())
                    .With("nonRenewalId", row.NonRenewalId.ToString("D")))
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
        catch (DbUpdateException)
        {
            return DomainError.Of(ModuleCode.POL, "ILLEGAL-TRANSITION", "The term has already been non-renewed.");
        }

        if (psr && !context.DryRun)
        {
            // The review task in the Work PSR queue. TODO(hardening): a PSR decision endpoint for non-renewals (none in the contract yet).
            await psrTasks.RaiseAsync(
                "WRK-POL-NONPAY-PSR-EXCEPTION", new ObjectRef(ModuleCode.POL, "Policy", policyId.Value.ToString("D")),
                $"NON_RENEWAL:{row.NonRenewalId:D}", cancellationToken).ConfigureAwait(false);
        }

        return new RenewalNonRenewResponse
        {
            NonRenewalId = row.NonRenewalId,
            TermId = request.TermId,
            Status = psr ? RenewalNonRenewResponse.StatusValue.PsrReview : RenewalNonRenewResponse.StatusValue.NoticeRequested,
            ReasonCode = request.ReasonCode,
            EffectiveAt = term.ValidTo,
            NoticeDeadline = deadline,
            NoticeDays = settings.NonRenewalNoticeDays,
            LegalStatus = Enum.Parse<RenewalNonRenewResponse.LegalStatusValue>(settings.NonRenewalNoticeLegalStatus),
            NoticeDocumentId = row.NoticeDocumentId is { } d ? new DocumentId(d) : null,
            RecordedAt = now,
        };
    }
}

/// <summary>Audit facts of <c>pol.Renewal.nonRenew</c>.</summary>
internal sealed class NonRenewAuditor : ICommandAuditor<NonRenew, RenewalNonRenewResponse>
{
    public CommandAuditFacts Describe(NonRenew command, Result<RenewalNonRenewResponse>? result) =>
        result is { IsSuccess: true } success
            ? new CommandAuditFacts
            {
                ObjectRef = ObjectRef.For(ModuleCode.POL, "Term", command.Request.TermId),
                BusinessKeys = BusinessKeys.Empty.With("termId", command.Request.TermId.Value.ToString()).With("nonRenewalId", success.Value.NonRenewalId.ToString("D")),
                Changes = AuditDiff.Compute(null, new { status = success.Value.Status.ToString(), reason = command.Request.ReasonCode, legalStatus = success.Value.LegalStatus.ToString() }),
            }
            : new CommandAuditFacts { ObjectRef = ObjectRef.For(ModuleCode.POL, "Term", command.Request.TermId) };
}
