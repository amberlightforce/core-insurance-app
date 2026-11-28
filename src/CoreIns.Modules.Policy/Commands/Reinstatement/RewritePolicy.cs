using System.Text.Json;
using CoreIns.Modules.Policy.Contracts;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Modules.Policy.Contracts.Events;
using CoreIns.Modules.Policy.Domain;
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

namespace CoreIns.Modules.Policy.Commands.Reinstatement;

/// <summary>The rewrite types (REQ-POL-037: the account changes only through a move or a rewrite) and the marker a rewrite job carries.</summary>
internal static class RewriteTypes
{
    /// <summary>A new policy on the same account.</summary>
    public const string Standard = "STANDARD";

    /// <summary>A new policy on another account (<c>targetAccountId</c>).</summary>
    public const string AccountChange = "ACCOUNT_CHANGE";

    /// <summary>Prefix of the job's reason code that marks a submission as the rewrite of a cancelled term (the suffix is the type).</summary>
    public const string Marker = "REWRITE:";

    public static string? Parse(JsonElement? type)
    {
        if (type is not { ValueKind: JsonValueKind.String } t)
        {
            return null;
        }

        var code = t.GetString()?.Trim().ToUpperInvariant();
        return code is Standard or AccountChange ? code : null;
    }
}

/// <summary><c>pol.Rewrite.create</c>: start a new policy from a cancelled term; the new quote and bind go through the ordinary job commands (BO-POL-07).</summary>
internal sealed record RewritePolicy(RewriteCreateRequest Request) : ICommand<RewriteCreateResponse>;

internal sealed class RewritePolicyValidator : AbstractValidator<RewritePolicy>
{
    public RewritePolicyValidator()
    {
        RuleFor(c => c.Request.TermId).NotNull().WithErrorCode("TERM_REQUIRED");
        RuleFor(c => c.Request.RewriteType).Must(t => RewriteTypes.Parse(t) is not null).WithErrorCode("REWRITE_TYPE_UNKNOWN");
        RuleFor(c => c.Request.EffectiveAt).NotNull().WithErrorCode("EFFECTIVE_AT_REQUIRED");
        RuleFor(c => c.Request.TargetAccountId).NotNull()
            .When(c => RewriteTypes.Parse(c.Request.RewriteType) == RewriteTypes.AccountChange).WithErrorCode("TARGET_ACCOUNT_REQUIRED");
    }
}

/// <summary>
/// Creates the Draft submission of a rewrite (BO-POL-07). The old term must be Cancelled and have no open rewrite. The new job carries the
/// old term's policyholder, product and risk (copied into quote version 1), a fresh policy id, the product version resolved for the new
/// effective time and, for <c>ACCOUNT_CHANGE</c>, the target account. It is quoted and bound with <c>pol.Job.updateDraft/quote/bind</c>,
/// so every bind gate (UW, sanctions screening) applies; <see cref="RewriteJobs.PublishRewrittenAsync"/> publishes <c>PolicyRewritten</c>
/// from the bind. TODO(hardening): move the old term to Rewritten (the state table only permits that from Scheduled or InForce).
/// </summary>
internal sealed class RewritePolicyHandler(
    PolicyDbContext db,
    RequestContext context,
    ILegalEntityDirectory legalEntities,
    IClock clock,
    INumberingService numbering,
    Dependency<IProductProductVersionService> productVersions,
    IOptions<PolicyOptions> options) : ICommandHandler<RewritePolicy, RewriteCreateResponse>
{
    public async Task<Result<RewriteCreateResponse>> HandleAsync(RewritePolicy command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var type = RewriteTypes.Parse(request.RewriteType)!;
        var zone = options.Value.Zone;
        var now = clock.Now;
        var effective = request.EffectiveAt!.Value;
        var legalEntity = JobSupport.LegalEntity(context, legalEntities);
        var jurisdiction = context.Jurisdiction ?? throw new InvalidOperationException("The request context has no jurisdiction.");

        var term = await db.Terms.AsNoTracking().SingleOrDefaultAsync(
            x => x.TermId == request.TermId!.Value && x.LegalEntityId == legalEntity && x.RecordedTo == null, cancellationToken).ConfigureAwait(false);
        if (term is null)
        {
            return JobSupport.NotFound("term");
        }

        if (Codes.Parse<PolicyTermState>(term.State) != PolicyTermState.Cancelled)
        {
            return DomainError.Of(ModuleCode.POL, "ILLEGAL-TRANSITION", $"A {Codes.Parse<PolicyTermState>(term.State)} term cannot be rewritten: only a cancelled term can.");
        }

        if (effective < now)
        {
            return DomainError.Of(ModuleCode.POL, PolicyErrorNames.EffdateLimit, "A rewrite cannot start before now (REQ-POL-137).");
        }

        var open = new[] { Codes.Of(JobState.Draft), Codes.Of(JobState.Quoted), Codes.Of(JobState.Scheduled) };
        if (await db.Jobs.AsNoTracking().AnyAsync(
                j => j.TargetTermId == term.TermId && j.JobType == Codes.Of(JobType.Submission) && j.ReasonCode!.StartsWith(RewriteTypes.Marker) && open.Contains(j.State),
                cancellationToken).ConfigureAwait(false))
        {
            return DomainError.Of(ModuleCode.POL, "OPEN-JOB", "The term already has an open rewrite.");
        }

        var policy = await db.Policies.AsNoTracking().SingleAsync(p => p.PolicyId == term.PolicyId && p.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        var segment = (await db.Segments.AsNoTracking().Where(s => s.TermId == term.TermId).ToListAsync(cancellationToken).ConfigureAwait(false))
            .OrderByDescending(s => s.ValidFrom).ThenByDescending(s => s.RecordedFrom).FirstOrDefault();
        var issuance = await db.Jobs.AsNoTracking().Where(j => j.PolicyId == policy.PolicyId && j.JobType == Codes.Of(JobType.Submission))
            .OrderBy(j => j.CreatedAt).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var channel = issuance?.Channel ?? context.Channel ?? "STAFF";

        ProductVersionResolveResponse resolved;
        try
        {
            resolved = await productVersions.Value.ResolveAsync(
                new ProductVersionResolveRequest
                {
                    Jurisdiction = jurisdiction.Value, LegalEntity = context.LegalEntity!.Value.Value, Product = policy.ProductCode, Channel = channel,
                    TransactionType = ProductVersionResolveRequest.TransactionTypeValue.NewBusiness,
                },
                ValidAt.From(effective), cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException ex)
        {
            return DomainError.Of(ModuleCode.POL, "PRODUCT-UNAVAILABLE", $"No product version is available: {ex.Error.Code}.");
        }

        var jobId = JobId.New();
        var number = await numbering.NextAsync(new NumberRequest(NumberingSchemes.Job, now.ToBusinessDate(zone)), cancellationToken).ConfigureAwait(false);
        var actor = context.Actor.ToString();
        db.Jobs.Add(new JobRow
        {
            JobId = jobId, LegalEntityId = legalEntity, Jurisdiction = jurisdiction.Value, JobNumber = JobNumber.Parse(number.Value),
            JobType = Codes.Of(JobType.Submission), State = Codes.Of(JobStateModel.Machine.Start(JobState.Draft).Value), PolicyId = PolicyId.New(),
            PolicyholderPartyId = policy.PolicyholderPartyId, AccountId = type == RewriteTypes.AccountChange ? request.TargetAccountId : policy.AccountId,
            ProductCode = policy.ProductCode, ProductVersion = resolved.Version.ToString(), ArtefactHash = resolved.ArtefactHash.Value,
            RatingArtefactHash = resolved.ResolutionManifest.RatingArtefactHash?.Value, ResolutionHash = resolved.ResolutionHash.Hash.Value,
            ResolutionManifest = JobSupport.Json(resolved.ResolutionManifest), Channel = channel, ProducerCode = term.ProducerCode, QuoteType = "FULL",
            EffectiveAt = effective, ExpirationAt = PolicyTime.AnnualEnd(effective, zone), Currency = term.Currency, CurrentVersionNo = 1, RecordVersion = 1,
            CreatedAt = now, CreatedBy = actor, Participants = [actor], UpdatedAt = now,
            TargetTermId = term.TermId, ReasonCode = RewriteTypes.Marker + type,
        });
        db.QuoteVersions.Add(new QuoteVersionRow
        {
            QuoteId = QuoteId.New(), JobId = jobId, LegalEntityId = legalEntity, VersionNo = 1,
            State = Codes.Of(QuoteStateModel.Machine.Start(QuoteState.Draft).Value), DraftVersion = 0,
            RiskTree = segment?.Snapshot ?? JobSupport.Json(RiskTrees.Empty), RecordVersion = 1, CreatedAt = now, UpdatedAt = now,
        });
        return new RewriteCreateResponse { JobId = jobId };
    }
}

/// <summary>Bind-time part of a rewrite: the event that links the old term to the new one.</summary>
internal static class RewriteJobs
{
    /// <summary>The rewrite type of a job, <c>null</c> when it is not a rewrite submission.</summary>
    public static string? TypeOf(JobRow job) =>
        job.JobType == Codes.Of(JobType.Submission) && job.TargetTermId is not null && job.ReasonCode is { } code && code.StartsWith(RewriteTypes.Marker, StringComparison.Ordinal)
            ? code[RewriteTypes.Marker.Length..]
            : null;

    /// <summary>Publishes <c>PolicyRewritten</c> (once, with the bind) when the bound job is the rewrite of a cancelled term.</summary>
    public static async Task PublishRewrittenAsync(
        PolicyDbContext db, IEventPublisher events, JobRow job, PolicyTermId newTermId, PolicyNumber newPolicyNumber, Instant at, CancellationToken cancellationToken)
    {
        if (TypeOf(job) is not { } rewriteType)
        {
            return;
        }

        var oldTermId = job.TargetTermId!.Value;
        var oldPolicyId = await db.Terms.AsNoTracking().Where(t => t.TermId == oldTermId).Select(t => t.PolicyId).FirstAsync(cancellationToken).ConfigureAwait(false);
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(PolicyRewrittenV1.Descriptor), "Policy", oldPolicyId.Value.ToString(),
            new PolicyRewrittenV1
            {
                OldTermId = oldTermId, NewTermId = newTermId, RewriteType = rewriteType, NewPolicyId = job.PolicyId, NewPolicyNumber = newPolicyNumber,
            },
            BusinessKeys.Empty.With("policyId", oldPolicyId.Value.ToString()).With("oldTermId", oldTermId.Value.ToString())
                .With("newTermId", newTermId.Value.ToString()).With("jobId", job.JobId.Value.ToString()))
        {
            OccurredAt = at,
        });
    }
}

/// <summary>Audit facts of <c>pol.Rewrite.create</c>.</summary>
internal sealed class RewritePolicyAuditor : ICommandAuditor<RewritePolicy, RewriteCreateResponse>
{
    public CommandAuditFacts Describe(RewritePolicy command, Result<RewriteCreateResponse>? result)
    {
        var facts = new CommandAuditFacts { ObjectRef = ObjectRef.For(ModuleCode.POL, "Term", command.Request.TermId ?? default) };
        return result is { IsSuccess: true } success
            ? facts with
            {
                BusinessKeys = BusinessKeys.Empty.With("jobId", success.Value.JobId?.Value.ToString() ?? string.Empty).With("termId", command.Request.TermId.ToString() ?? string.Empty),
                Changes = AuditDiff.Compute(null, new { rewriteType = RewriteTypes.Parse(command.Request.RewriteType), effectiveAt = command.Request.EffectiveAt?.ToString() }),
            }
            : facts;
    }
}
