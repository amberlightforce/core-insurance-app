using CoreIns.Modules.Party.Contracts;
using CoreIns.Modules.Party.Contracts.Api;
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
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Policy.Commands;

/// <summary><c>pol.Submission.create</c>: starts a new-business job in Draft with quote version 1.</summary>
internal sealed record CreateSubmission(SubmissionCreateRequest Request) : ICommand<SubmissionCreateResponse>;

internal sealed class CreateSubmissionValidator : AbstractValidator<CreateSubmission>
{
    public CreateSubmissionValidator()
    {
        RuleFor(c => c.Request.Product).NotEmpty().MaximumLength(128);
        RuleFor(c => c.Request.Channel).NotEmpty().MaximumLength(128);
        RuleFor(c => c.Request.ProducerCode).MaximumLength(128);
        RuleFor(c => c.Request.PolicyholderPartyId.Value).NotEmpty().WithErrorCode("PARTY_REQUIRED");
    }
}

/// <summary>
/// Creates a submission (REQ-POL-145, REQ-POL-001): the policyholder must exist in PTY (read through
/// <c>IPartyPartyService</c> without unmasking, REQ-POL-300), the producer code must be valid for the product
/// (<c>pty.ProducerCode.validate</c>, REQ-POL-174), the product version is resolved by PFC for the effective date
/// (<c>pfc.ProductVersion.resolve</c>, REQ-POL-140/146). New business is never effective before now (REQ-POL-137). The
/// job number comes from the PLT numbering service (REQ-POL-047); a policy id is reserved so every event of the job's
/// life is ordered on one aggregate. <c>SubmissionCreated</c> goes through the outbox in the same transaction.
/// </summary>
internal sealed class CreateSubmissionHandler(
    PolicyDbContext db,
    RequestContext context,
    ILegalEntityDirectory legalEntities,
    IClock clock,
    INumberingService numbering,
    IEventPublisher events,
    IPartyPartyService parties,
    IPartyProducerCodeService producerCodes,
    Dependency<IProductProductVersionService> productVersions,
    IOptions<PolicyOptions> options) : ICommandHandler<CreateSubmission, SubmissionCreateResponse>
{
    private const string NewBusiness = "NewBusiness";

    public async Task<Result<SubmissionCreateResponse>> HandleAsync(CreateSubmission command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var now = clock.Now;
        var legalEntity = JobSupport.LegalEntity(context, legalEntities);
        var legalEntityCode = context.LegalEntity!.Value;
        var jurisdiction = context.Jurisdiction ?? throw new InvalidOperationException("The request context has no jurisdiction.");
        var zone = options.Value.Zone;

        if (request.EffectiveAt < now)
        {
            return DomainError.Of(ModuleCode.POL, "EFFDATE-LIMIT", "New business cannot start before now (REQ-POL-137).");
        }

        // The policyholder exists in PTY (masked read: POL never holds official identifiers, REQ-POL-300).
        try
        {
            await parties.GetAsync(request.PolicyholderPartyId.Value.ToString(), cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException ex) when (ex.Error.Code.Name == "NOT-FOUND")
        {
            return new DomainError(ErrorCode.For(ModuleCode.POL, "VALIDATION"), "The policyholder does not exist.")
            {
                FieldErrors = [new FieldError("policyholderPartyId", "PARTY_NOT_FOUND", "pol.party_not_found")],
            };
        }

        if (request.ProducerCode is { } producerCode)
        {
            var producer = await producerCodes.ValidateAsync(
                new ProducerCodeValidateRequest { ProducerCode = producerCode, Product = request.Product, TransactionType = NewBusiness },
                ValidAt.From(request.EffectiveAt), cancellationToken).ConfigureAwait(false);
            if (!producer.Valid)
            {
                return new DomainError(ErrorCode.For(ModuleCode.POL, "PRODUCER-INVALID"), "The producer code cannot write this product.")
                {
                    Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["reasons"] = string.Join(",", producer.Reasons) },
                };
            }
        }

        ProductVersionResolveResponse resolved;
        try
        {
            resolved = await productVersions.Value.ResolveAsync(
                new ProductVersionResolveRequest
                {
                    Jurisdiction = jurisdiction.Value, LegalEntity = legalEntityCode.Value, Product = request.Product, Channel = request.Channel,
                    TransactionType = ProductVersionResolveRequest.TransactionTypeValue.NewBusiness,
                },
                ValidAt.From(request.EffectiveAt), cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException ex)
        {
            return DomainError.Of(ModuleCode.POL, "PRODUCT-UNAVAILABLE", $"No product version is available: {ex.Error.Code}.");
        }

        var currency = request.Currency ?? options.Value.Currency;
        var jobId = JobId.New();
        var policyId = PolicyId.New();
        var expiration = PolicyTime.AnnualEnd(request.EffectiveAt, zone);
        var number = await numbering.NextAsync(new NumberRequest(NumberingSchemes.Job, now.ToBusinessDate(zone)), cancellationToken).ConfigureAwait(false);
        var state = JobStateModel.Machine.Start(JobState.Draft).Value;
        var actor = context.Actor.ToString();

        var job = new JobRow
        {
            JobId = jobId,
            LegalEntityId = legalEntity,
            Jurisdiction = jurisdiction.Value,
            JobNumber = JobNumber.Parse(number.Value),
            JobType = Codes.Of(JobType.Submission),
            State = Codes.Of(state),
            PolicyId = policyId,
            PolicyholderPartyId = request.PolicyholderPartyId,
            AccountId = request.AccountId,
            ProductCode = request.Product,
            ProductVersion = resolved.Version.ToString(),
            ArtefactHash = resolved.ArtefactHash.Value,
            RatingArtefactHash = resolved.ResolutionManifest.RatingArtefactHash?.Value,
            ResolutionHash = resolved.ResolutionHash.Hash.Value,
            ResolutionManifest = JobSupport.Json(resolved.ResolutionManifest),
            Channel = request.Channel,
            ProducerCode = request.ProducerCode,
            QuoteType = request.QuoteType == SubmissionCreateRequest.QuoteTypeValue.Quick ? "QUICK" : "FULL",
            EffectiveAt = request.EffectiveAt,
            ExpirationAt = expiration,
            Currency = currency.Code,
            CurrentVersionNo = 1,
            RecordVersion = 1,
            CreatedAt = now,
            CreatedBy = actor,
            UpdatedAt = now,
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
            RiskTree = JobSupport.Json(RiskTrees.Empty),
            RecordVersion = 1,
            CreatedAt = now,
            UpdatedAt = now,
        });

        events.Publish(new OutgoingEvent(
            EventDescriptor.From(SubmissionCreatedV1.Descriptor), "Policy", policyId.Value.ToString(),
            new SubmissionCreatedV1
            {
                JobId = jobId, AccountId = request.AccountId, ProductCode = request.Product, ProductVersion = resolved.Version,
                Channel = request.Channel, ProducerCode = request.ProducerCode,
            },
            BusinessKeys.Empty.With("policyId", policyId.Value.ToString()).With("jobId", jobId.Value.ToString())));

        return new SubmissionCreateResponse
        {
            JobId = jobId,
            JobNumber = job.JobNumber,
            PolicyId = policyId,
            State = Codes.Api(state),
            ProductVersion = resolved.Version,
            ExpirationAt = expiration,
            VersionNo = 1,
            Manifest = new SubmissionCreateResponse.ManifestDetail
            {
                ArtefactHash = resolved.ResolutionManifest.ArtefactHash,
                RatingArtefactHash = resolved.ResolutionManifest.RatingArtefactHash,
                UwRuleSetVersions = resolved.ResolutionManifest.UwRuleSetVersions,
                FormPatternEditions = resolved.ResolutionManifest.FormPatternEditions,
                ReferenceTableVersions = resolved.ResolutionManifest.ReferenceTableVersions,
                PaymentPlanVersions = resolved.ResolutionManifest.PaymentPlanVersions,
            },
        };
    }
}

/// <summary>Audit facts of <c>pol.Submission.create</c>: the job, its number and lineage keys (no personal data).</summary>
internal sealed class CreateSubmissionAuditor : ICommandAuditor<CreateSubmission, SubmissionCreateResponse>
{
    public CommandAuditFacts Describe(CreateSubmission command, Result<SubmissionCreateResponse>? result)
    {
        if (result is not { IsSuccess: true } success)
        {
            return new CommandAuditFacts();
        }

        var response = success.Value;
        return new CommandAuditFacts
        {
            ObjectRef = ObjectRef.For(ModuleCode.POL, "Job", response.JobId),
            ObjectNumber = response.JobNumber.Value,
            BusinessKeys = BusinessKeys.Empty.With("jobId", response.JobId.Value.ToString()).With("policyId", response.PolicyId.Value.ToString()),
            Changes = AuditDiff.Compute(null, new { state = response.State.ToString(), product = command.Request.Product, productVersion = response.ProductVersion.ToString() }),
        };
    }
}
