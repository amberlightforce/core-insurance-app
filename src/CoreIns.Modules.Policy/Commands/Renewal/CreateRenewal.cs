using System.Text.Json;
using CoreIns.Modules.Policy.Contracts;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Modules.Policy.Contracts.Events;
using CoreIns.Modules.Policy.Domain;
using CoreIns.Modules.Policy.Domain.Servicing;
using CoreIns.Modules.Policy.Persistence;
using CoreIns.Modules.Policy.Services;
using CoreIns.Modules.Product.Contracts;
using CoreIns.Modules.Product.Contracts.Api;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
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

namespace CoreIns.Modules.Policy.Commands.Renewal;

/// <summary><c>pol.Renewal.create</c>: "Renew now" (REQ-POL-258) for one term; creates the renewal job in Draft.</summary>
internal sealed record CreateRenewal(RenewalCreateRequest Request) : ICommand<RenewalCreateResponse>;

internal sealed class CreateRenewalValidator : AbstractValidator<CreateRenewal>
{
    public CreateRenewalValidator() => RuleFor(c => c.Request.TermId).NotNull().WithErrorCode("TERM_REQUIRED");
}

/// <summary>
/// Creates the renewal job of a term (REQ-POL-245 window check, REQ-POL-246, REQ-POL-258, REQ-POL-263), under the policy lock:
/// <list type="bullet">
/// <item>the term is Scheduled or InForce, ends within <c>pol.renewal.lead_days</c> Athens days and not yet (no batch, no
/// renewal after expiry), has no open renewal and no next term (409);</item>
/// <item>the new term is [expiry, expiry + P12M), half-open: it starts exactly where the expiring one ends;</item>
/// <item>the risk tree is copied from the segment that is current at the end of the expiring term, static locators kept;</item>
/// <item>PFC resolves the product version in force for a renewal at the new term's start (1.1 on or after its window opens,
/// D-SL3-15) and the job pins the artefacts of that resolution (REQ-POL-033);</item>
/// <item><c>base_transaction_id</c> is the expiring term's head, checked again by <c>pol.Renewal.accept</c>.</item>
/// </list>
/// Emits <c>RenewalCreated</c>. Dry-run computes the same and rolls back.
/// </summary>
internal sealed class CreateRenewalHandler(
    PolicyDbContext db,
    RequestContext context,
    ILegalEntityDirectory legalEntities,
    IClock clock,
    INumberingService numbering,
    IEventPublisher events,
    Dependency<IProductProductVersionService> productVersions,
    IOptions<PolicyOptions> options,
    IOptions<RenewalOptions> renewalOptions) : ICommandHandler<CreateRenewal, RenewalCreateResponse>
{
    public async Task<Result<RenewalCreateResponse>> HandleAsync(CreateRenewal command, CancellationToken cancellationToken)
    {
        if (command.Request.TermId is not { } termId)
        {
            return RenewalSupport.TermRequired();
        }

        var legalEntity = JobSupport.LegalEntity(context, legalEntities);
        var zone = options.Value.Zone;
        if (await RenewalSupport.PolicyOfAsync(db, legalEntity, termId, cancellationToken).ConfigureAwait(false) is not { } policyId)
        {
            return JobSupport.NotFound("term");
        }

        // Lock first, then stamp (D-SL3-03 a): the record time of everything this command writes.
        var locked = await PolicyWriteLock.AcquireAsync(db, clock, options.Value.LockWait, legalEntity, policyId, cancellationToken).ConfigureAwait(false);
        if (locked.IsFailure)
        {
            return locked.Error!;
        }

        var now = locked.Value;
        var term = await RenewalSupport.CurrentTermAsync(db, legalEntity, termId, cancellationToken).ConfigureAwait(false);
        if (term is null)
        {
            return JobSupport.NotFound("term");
        }

        if (RenewalSupport.NotRenewable(term) is { } notRenewable)
        {
            return notRenewable;
        }

        // REQ-POL-245: inside the window, and the term has not ended (no grace, pol.renewal.grace_days = 0).
        var expiry = term.ValidTo;
        var daysLeft = DayCount.Days(now, expiry, zone);
        if (now >= expiry || daysLeft > renewalOptions.Value.LeadDays)
        {
            return RenewalSupport.Validation(
                "termId", "OUTSIDE_RENEWAL_WINDOW",
                $"A renewal can be created from {renewalOptions.Value.LeadDays} days before the term ends until it ends; this term ends in {daysLeft} days.");
        }

        if (await db.Jobs.AnyAsync(
                j => j.ExpiringTermId == termId && j.JobType == Codes.Of(JobType.Renewal) && j.LegalEntityId == legalEntity
                     && (j.State == Codes.Of(JobState.Draft) || j.State == Codes.Of(JobState.Quoted) || j.State == Codes.Of(JobState.Scheduled)),
                cancellationToken).ConfigureAwait(false))
        {
            return DomainError.Of(ModuleCode.POL, "ILLEGAL-TRANSITION", "The term already has an open renewal.");
        }

        if (await db.Terms.AnyAsync(t => t.PredecessorTermId == termId && t.RecordedTo == null, cancellationToken).ConfigureAwait(false))
        {
            return DomainError.Of(ModuleCode.POL, "ILLEGAL-TRANSITION", "The term has already been renewed.");
        }

        var policy = await db.Policies.AsNoTracking().SingleAsync(p => p.PolicyId == policyId, cancellationToken).ConfigureAwait(false);

        // The risk tree valid at the end of the expiring term (half-open: the segment that ends at the expiry), as known now.
        var segments = await db.Segments.AsNoTracking()
            .Where(s => s.TermId == termId && s.RecordedTo == null && s.ValidFrom < expiry && s.ValidTo >= expiry)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var segment = segments.OrderByDescending(s => s.ValidFrom).FirstOrDefault();
        if (segment is null)
        {
            return DomainError.Of(ModuleCode.POL, "SEGMENT-INVARIANT", "The expiring term has no segment at its end.");
        }

        var start = expiry;
        var end = PolicyTime.AnnualEnd(start, zone);
        var resolved = await ResolveAsync(policy, term, start, cancellationToken).ConfigureAwait(false);
        if (resolved.IsFailure)
        {
            return resolved.Error!;
        }

        var resolution = resolved.Value;
        var actor = context.Actor.ToString();
        var jobId = JobId.New();
        var number = await numbering.NextAsync(new NumberRequest(NumberingSchemes.Job, now.ToBusinessDate(zone)), cancellationToken).ConfigureAwait(false);
        var job = new JobRow
        {
            JobId = jobId,
            LegalEntityId = legalEntity,
            Jurisdiction = policy.Jurisdiction,
            JobNumber = JobNumber.Parse(number.Value),
            JobType = Codes.Of(JobType.Renewal),
            State = Codes.Of(JobStateModel.Machine.Start(JobState.Draft).Value),
            PolicyId = policyId,
            PolicyholderPartyId = policy.PolicyholderPartyId,
            AccountId = policy.AccountId,
            ProductCode = policy.ProductCode,
            ProductVersion = resolution.Version.ToString(),
            ArtefactHash = resolution.ArtefactHash.Value,
            RatingArtefactHash = resolution.ResolutionManifest.RatingArtefactHash?.Value,
            ResolutionHash = resolution.ResolutionHash.Hash.Value,
            ResolutionManifest = JobSupport.Json(resolution.ResolutionManifest),
            Channel = RenewalSupport.ChannelStaff,
            ProducerCode = term.ProducerCode,
            QuoteType = "FULL",
            EffectiveAt = start,
            ExpirationAt = end,
            Currency = term.Currency,
            CurrentVersionNo = 1,
            RecordVersion = 1,
            CreatedAt = now,
            CreatedBy = actor,
            Participants = [actor],
            UpdatedAt = now,
            ExpiringTermId = termId,
            BaseTransactionId = term.HeadTransactionId,
        };
        db.Jobs.Add(job);
        db.QuoteVersions.Add(new QuoteVersionRow
        {
            QuoteId = QuoteId.New(),
            JobId = jobId,
            LegalEntityId = legalEntity,
            VersionNo = 1,
            State = Codes.Of(QuoteStateModel.Machine.Start(QuoteState.Draft).Value),
            DraftVersion = 0,
            RiskTree = segment.Snapshot,
            RecordVersion = 1,
            CreatedAt = now,
            UpdatedAt = now,
        });

        events.Publish(new OutgoingEvent(
            EventDescriptor.From(RenewalCreatedV1.Descriptor), "Policy", policyId.Value.ToString(),
            new RenewalCreatedV1
            {
                JobId = jobId,
                ExpiringTermId = termId,
                RenewalProductCode = job.ProductCode,
                RenewalProductVersion = resolution.Version,
                ConversionOutcomeSummary = JsonSerializer.SerializeToElement(
                    new { conversion = "VERSION_RESOLUTION_ONLY", fromVersion = term.ProductVersion, toVersion = job.ProductVersion }),
            },
            BusinessKeys.Empty.With("policyId", policyId.Value.ToString()).With("jobId", jobId.Value.ToString()).With("expiringTermId", termId.Value.ToString()))
        {
            OccurredAt = now,
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A racing "Renew now" created the open renewal first (ux_job_open_renewal): a conflict, never a 500.
            return DomainError.Of(ModuleCode.POL, "ILLEGAL-TRANSITION", "The term already has an open renewal.");
        }

        var version = await RenewalSupport.CurrentVersionAsync(db, job, cancellationToken).ConfigureAwait(false);
        return new RenewalCreateResponse { Job = RenewalSupport.JobJson(job, version) };
    }

    private async Task<Result<ProductVersionResolveResponse>> ResolveAsync(PolicyRow policy, PolicyTermRow term, Instant start, CancellationToken cancellationToken)
    {
        try
        {
            return await productVersions.Value.ResolveAsync(
                new ProductVersionResolveRequest
                {
                    Jurisdiction = policy.Jurisdiction,
                    LegalEntity = context.LegalEntity!.Value.Value,
                    Product = policy.ProductCode,
                    Channel = RenewalSupport.ChannelStaff,
                    TransactionType = ProductVersionResolveRequest.TransactionTypeValue.Renewal,
                    PredecessorHash = Sha256Hash.Parse(term.ArtefactHash),
                },
                ValidAt.From(start), cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException ex)
        {
            return DomainError.Of(ModuleCode.POL, "PRODUCT-UNAVAILABLE", $"No product version is available for the renewal term: {ex.Error.Code}.");
        }
    }
}

/// <summary>Audit facts of <c>pol.Renewal.create</c>: the term and the new renewal job.</summary>
internal sealed class CreateRenewalAuditor : ICommandAuditor<CreateRenewal, RenewalCreateResponse>
{
    public CommandAuditFacts Describe(CreateRenewal command, Result<RenewalCreateResponse>? result)
    {
        if (result is not { IsSuccess: true } success || success.Value.Job is not { } job)
        {
            return new CommandAuditFacts();
        }

        var jobId = new JobId(Guid.Parse(job.GetProperty("jobId").GetString()!));
        return new CommandAuditFacts
        {
            ObjectRef = ObjectRef.For(ModuleCode.POL, "Job", jobId),
            ObjectNumber = job.GetProperty("jobNumber").GetString(),
            BusinessKeys = BusinessKeys.Empty.With("jobId", jobId.Value.ToString()).With("expiringTermId", command.Request.TermId?.Value.ToString() ?? string.Empty),
            Changes = AuditDiff.Compute(null, new { state = job.GetProperty("state").GetString(), productVersion = job.GetProperty("productVersion").GetString() }),
        };
    }
}
