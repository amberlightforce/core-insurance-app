using System.Globalization;
using System.Text.Json;
using CoreIns.Modules.Claims.Contracts.Api;
using CoreIns.Modules.Claims.Contracts.Events;
using CoreIns.Modules.Claims.Domain;
using CoreIns.Modules.Claims.Persistence;
using CoreIns.Modules.Claims.Queries;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Events;
using CoreIns.Platform.Numbering;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Claims.Commands;

/// <summary><c>clm.Fnol.submit</c> as a command of the platform pipeline (validation → transaction → idempotency → audit → authority → handler).</summary>
/// <param name="Request">The contract request (generated from contracts/openapi/clm.yaml).</param>
internal sealed record SubmitFnol(FnolSubmitRequest Request) : ICommand<FnolSubmitResponse>;

/// <summary>
/// Shape rules (the pipeline turns failures into CLM-ERR-VALIDATION with field errors). Missing mandatory fields are not
/// shape errors: the handler reports them as CLM-ERR-FNOL-001 (REQ-CLM-030).
/// </summary>
internal sealed class SubmitFnolValidator : AbstractValidator<SubmitFnol>
{
    public SubmitFnolValidator()
    {
        RuleFor(c => c.Request).SetValidator(new FnolRequestValidator());
    }
}

/// <summary>Shape rules of an FNOL payload, shared with <c>clm.Fnol.validate</c>.</summary>
internal sealed class FnolRequestValidator : AbstractValidator<FnolSubmitRequest>
{
    private const string CodePattern = "^[A-Z][A-Z0-9_]{0,63}$";

    public FnolRequestValidator()
    {
        RuleFor(r => r.LineOfBusiness).Must(l => ReferenceCodes.SliceLines.Contains(l!)).When(r => !string.IsNullOrWhiteSpace(r.LineOfBusiness))
            .WithErrorCode("LINE_NOT_SUPPORTED").WithMessage("The slice takes motor claims only (MOTOR).");
        RuleFor(r => r.Channel).Must(c => ReferenceCodes.Channels.Contains(c!)).When(r => !string.IsNullOrWhiteSpace(r.Channel))
            .WithErrorCode("CHANNEL").WithMessage("The channel is not in the shared channel code list (R-84).");
        RuleFor(r => r.Channel).Must(c => ReferenceCodes.SliceChannels.Contains(c!)).When(r => r.Channel is not null && ReferenceCodes.Channels.Contains(r.Channel))
            .WithErrorCode("CHANNEL_NOT_SUPPORTED").WithMessage("Only the staff channels (STAFF, CONTACT_CENTRE) submit FNOL in this release.");
        RuleFor(r => r.ReceiptMedium).Must(m => ReferenceCodes.ReceiptMedia.Contains(m!)).When(r => r.ReceiptMedium is not null)
            .WithErrorCode("RECEIPT_MEDIUM");
        RuleFor(r => r.LossCause).Matches(CodePattern).When(r => !string.IsNullOrWhiteSpace(r.LossCause)).WithErrorCode("CODE");
        RuleFor(r => r.LossLocation).MaximumLength(500);
        RuleFor(r => r.Description).MaximumLength(4000);
        RuleFor(r => r.Incidents).Must(i => i is null || i.Count <= 10).WithErrorCode("TOO_MANY");
        RuleForEach(r => r.Incidents).ChildRules(incident =>
        {
            incident.RuleFor(i => i.VehicleRef).MaximumLength(64);
            incident.RuleForEach(i => i.DamageAreas).NotEmpty().Matches(CodePattern).WithErrorCode("CODE");
        });
        RuleFor(r => r.Exposures).Must(e => e is null || e.Count <= 10).WithErrorCode("TOO_MANY");
        RuleForEach(r => r.Exposures).ChildRules(exposure => exposure.RuleFor(e => e.CoverageCode).NotEmpty().Matches(CodePattern).WithErrorCode("CODE"));
        RuleFor(r => r.Exposures).Must(e => e is null || e.Select(x => (x.Kind, x.CoverageCode)).Distinct().Count() == e.Count)
            .WithErrorCode("EXPOSURE_REPEATED").WithMessage("Each proposed exposure (kind, coverage) once; the claimant is the insured (REQ-CLM-063).");
        When(r => r.DuplicateDecision is not null, () =>
        {
            RuleFor(r => r.DuplicateDecision!.ReasonCode).NotEmpty().Matches(CodePattern).WithErrorCode("CODE");
            RuleFor(r => r.DuplicateDecision!.LinkedClaimId).NotNull().When(r => r.DuplicateDecision!.Action == DuplicateDecision.ActionValue.Link)
                .WithErrorCode("LINK_TARGET_REQUIRED");
        });
        RuleFor(r => r.Reporter!.Relationship).Matches(CodePattern).When(r => r.Reporter?.Relationship is not null).WithErrorCode("CODE");
    }
}

/// <summary>
/// Registers a claim from a staff FNOL (REQ-CLM-001, -002, -030, -036, -041, -043, -044, -048, -049, -061, -062, -064, -071):
/// checks via <see cref="FnolAssessment"/>; claim number from the gapless PLT series CLAIM inside this transaction
/// (REQ-CLM-043, D-SL2-07; a dry run or a rollback consumes none); the claim goes Draft → Open(New); insured claimant,
/// vehicle incidents and confirmed exposures; the immutable FNOL snapshot (REQ-CLM-044); and, through the outbox in the
/// same transaction, <c>ClaimReported</c>, <c>CoverageVerified</c> and one <c>ExposureCreated</c> per exposure on the
/// claim's gap-free aggregate sequence (REQ-CLM-005). Free text is encrypted (P2) and is absent from the response,
/// the events and the audit record.
/// </summary>
internal sealed class SubmitFnolHandler(
    ClaimsDbContext db,
    RequestContext context,
    IClock clock,
    INumberingService numbering,
    IEventPublisher events,
    ClaimProtection protection,
    FnolAssessment assessment,
    ClaimReader reader,
    IOptions<ClaimsOptions> options) : ICommandHandler<SubmitFnol, FnolSubmitResponse>
{
    public async Task<Result<FnolSubmitResponse>> HandleAsync(SubmitFnol command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = protection.Current(context);
        var assessed = await assessment.AssessAsync(request, legalEntity, cancellationToken).ConfigureAwait(false);
        if (assessed.Error is { } error)
        {
            return error;
        }

        var snapshot = assessed.Snapshot!;
        var settings = options.Value;
        var jurisdiction = (context.Jurisdiction ?? throw new InvalidOperationException("The request context has no jurisdiction.")).Value;
        var now = clock.Now;
        var actor = context.Actor.ToString();
        var claimId = ClaimId.New();

        // Draft → Open(New) on submit (REQ-CLM-071); the number is issued only now (REQ-CLM-043).
        var state = ClaimStateModel.Machine.Start(ClaimState.Draft).Value;
        var opened = ClaimStateModel.Fire(state, ClaimTrigger.Submit);
        if (opened.IsFailure)
        {
            return opened.Error!;
        }

        var number = await numbering.NextAsync(new NumberRequest(NumberingSchemes.Claim, settings.DateOf(now)), cancellationToken).ConfigureAwait(false);
        var claimNumber = ClaimNumber.Parse(number.Value);
        var (status, subStatus) = ClaimStates.ToColumns(opened.Value);
        var duplicate = request.DuplicateDecision;
        var claim = new ClaimRow
        {
            ClaimId = claimId,
            LegalEntityId = legalEntity,
            Jurisdiction = jurisdiction,
            ClaimNumber = claimNumber,
            PolicyId = new PolicyId(snapshot.PolicyId),
            PolicyNumber = PolicyNumber.Parse(snapshot.PolicyNumber),
            InsuredPartyId = new PartyId(snapshot.InsuredPartyId),
            SnapshotRef = snapshot.SnapshotRef,
            SnapshotSegmentId = snapshot.SegmentId,
            PolicyTermId = snapshot.TermId is { } termId ? new PolicyTermId(termId) : null,
            SnapshotValidAt = snapshot.ValidAt,
            SnapshotKnownAt = snapshot.KnownAt,
            SnapshotStatus = Codes.Of(SnapshotStatus.Verified),
            PolicyInForceAtLoss = snapshot.InForce,
            PolicyStatusAtLoss = snapshot.Status ?? snapshot.NotInForceReason,
            SnapshotCoverageCodes = [.. snapshot.CoverageCodes],
            ProductCode = snapshot.ProductCode,
            ProductVersion = snapshot.ProductVersion,
            LineOfBusiness = request.LineOfBusiness!,
            LossAt = assessed.LossAt,
            LossDate = assessed.LossDate,
            NoticeOn = assessed.NoticeOn,
            LossCause = request.LossCause!,
            LossLocationEncrypted = await protection.EncryptAsync(legalEntity, ClaimProtection.LossLocationField, claimId.Value, request.LossLocation!.Trim(), cancellationToken).ConfigureAwait(false),
            DescriptionEncrypted = await protection.EncryptAsync(legalEntity, ClaimProtection.DescriptionField, claimId.Value, request.Description!.Trim(), cancellationToken).ConfigureAwait(false),
            Channel = request.Channel!,
            ReceiptMedium = request.ReceiptMedium,
            HandlingSegment = settings.DefaultHandlingSegment,
            Status = status,
            SubStatus = subStatus,

            // REQ-CLM-049: a loss outside the policy's term creates the claim with coverage pending and flagged.
            CoverageInQuestion = !snapshot.InForce,
            DuplicateOfClaimId = duplicate?.Action == DuplicateDecision.ActionValue.Link ? duplicate.LinkedClaimId : null,
            DuplicateReasonCode = assessed.Duplicates.Count > 0 ? duplicate?.ReasonCode : null,
            CreatedAt = now,
            CreatedBy = actor,
            UpdatedAt = now,
        };

        var insured = new ClaimantRow
        {
            ClaimantId = ClaimantId.New(), ClaimId = claimId, LegalEntityId = legalEntity, Jurisdiction = jurisdiction,
            PartyId = claim.InsuredPartyId, ClaimantType = Codes.Of(ClaimantType.Insured), CreatedAt = now, CreatedBy = actor,
        };

        var incidents = (request.Incidents ?? []).Select(i => new IncidentRow
        {
            IncidentId = Guid.CreateVersion7(), ClaimId = claimId, LegalEntityId = legalEntity, Jurisdiction = jurisdiction,
            IncidentType = "VEHICLE", VehicleRef = string.IsNullOrWhiteSpace(i.VehicleRef) ? null : i.VehicleRef.Trim(), Drivable = i.Drivable,
            DamageAreas = [.. (i.DamageAreas ?? []).Distinct(StringComparer.Ordinal)], CreatedAt = now, CreatedBy = actor,
        }).ToList();

        var exposures = new List<ExposureRow>();
        foreach (var proposal in request.Exposures ?? [])
        {
            claim.LastExposureSequence++;
            exposures.Add(NewExposure(claim, claim.LastExposureSequence, Codes.Of(proposal.Kind), proposal.CoverageCode, insured.ClaimantId,
                incidents.FirstOrDefault()?.IncidentId, null, CoverageRules.Indicate(snapshot, proposal.CoverageCode), now, actor));
        }

        var fnolId = Guid.CreateVersion7();
        var fnol = new FnolSnapshotRow
        {
            FnolId = fnolId, ClaimId = claimId, LegalEntityId = legalEntity, Jurisdiction = jurisdiction, Channel = claim.Channel,
            ReporterPartyId = request.Reporter?.PartyId,
            PayloadEncrypted = await protection.EncryptAsync(legalEntity, ClaimProtection.FnolPayloadField, fnolId, JsonSerializer.Serialize(request, SharedKernelJson.Options), cancellationToken).ConfigureAwait(false),
            SubmittedAt = now, CreatedAt = now, CreatedBy = actor,
        };

        db.Claims.Add(claim);
        db.Claimants.Add(insured);
        db.Incidents.AddRange(incidents);
        db.FnolSnapshots.Add(fnol);
        db.Exposures.AddRange(exposures);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Events in the same transaction, one aggregate (the claim): gap-free per-claim sequence (REQ-CLM-005). No free text.
        Publish(claim, new ClaimReportedV1
        {
            ClaimId = claimId,
            ClaimNumber = claimNumber,
            PolicyId = claim.PolicyId,
            PolicyNumber = claim.PolicyNumber,
            SnapshotRef = claim.SnapshotRef,
            LossDate = claim.LossDate,
            NoticeDate = claim.NoticeOn,
            LossCause = claim.LossCause,
            Line = claim.LineOfBusiness,
            ProductCode = claim.ProductCode,
            CatCode = null,
            Channel = claim.Channel,
            Claimants = [new() { PartyId = insured.PartyId, Role = insured.ClaimantType }],
            HandlingSegment = claim.HandlingSegment,
        }, BusinessKeys.Empty.With("claimId", claimId.Value.ToString()).With("policyId", claim.PolicyId.Value.ToString()));
        Publish(claim, new CoverageVerifiedV1
        {
            SnapshotRef = claim.SnapshotRef,
            Outcome = snapshot.InForce ? "IN_FORCE" : "NOT_IN_FORCE",
            DecisionMaker = "SYSTEM",
        }, BusinessKeys.Empty.With("claimId", claimId.Value.ToString()));
        foreach (var exposure in exposures)
        {
            Publish(claim, ExposureCreated(claim, exposure, insured.PartyId, settings),
                BusinessKeys.Empty.With("claimId", claimId.Value.ToString()).With("exposureId", exposure.ExposureId.Value.ToString()));
        }

        var summary = await reader.GetSummaryAsync(legalEntity, claimId, cancellationToken).ConfigureAwait(false);
        return new FnolSubmitResponse
        {
            ClaimId = claimId,
            ClaimNumber = claimNumber,
            Claim = summary!,
            Exposures = await reader.ExposuresAsync(claimId, cancellationToken).ConfigureAwait(false),
            CoverageIndications = assessed.Indications,
            DuplicateCandidates = assessed.Duplicates,
            HandlingSegment = claim.HandlingSegment,
            RequiredDocuments = [],
        };
    }

    /// <summary>A new open exposure numbered claim number + sequence (REQ-CLM-043, D-SL2-07).</summary>
    internal static ExposureRow NewExposure(
        ClaimRow claim, int sequence, string kind, string coverageCode, ClaimantId claimant, Guid? incident, string? duplicateReason,
        CoverageIndicationCode indication, Instant now, string actor)
    {
        var (status, subStatus) = ExposureStates.ToColumns(ExposureStateModel.Machine.Start(ExposureState.New).Value);
        return new ExposureRow
        {
            ExposureId = ExposureId.New(),
            ClaimId = claim.ClaimId,
            LegalEntityId = claim.LegalEntityId,
            Jurisdiction = claim.Jurisdiction,
            ExposureNumber = ExposureNumber.Parse(string.Create(CultureInfo.InvariantCulture, $"{claim.ClaimNumber.Value}-{sequence:D3}")),
            Sequence = sequence,
            Kind = kind,
            CoverageCode = coverageCode,
            ClaimantId = claimant,
            IncidentId = incident,
            Status = status,
            SubStatus = subStatus,
            CoverageIndication = Codes.Of(indication),
            CoverageDecision = Codes.Of(CoverageDecisionCode.Pending),
            DuplicateReason = duplicateReason,
            CreatedAt = now,
            CreatedBy = actor,
            UpdatedAt = now,
        };
    }

    /// <summary>The <c>ExposureCreated</c> payload (no P2/P3; the claimant party id is P1 per the schema).</summary>
    internal static ExposureCreatedV1 ExposureCreated(ClaimRow claim, ExposureRow exposure, PartyId claimantParty, ClaimsOptions settings) => new()
    {
        ExposureId = exposure.ExposureId,
        ExposureNumber = exposure.ExposureNumber,
        Kind = exposure.Kind,
        CoverageCode = exposure.CoverageCode,
        ClaimantPartyId = claimantParty,
        SiiLob = settings.UnmappedSiiLob,
        AccidentDate = claim.LossDate,
    };

    private void Publish<TPayload>(ClaimRow claim, TPayload payload, BusinessKeys keys)
        where TPayload : CoreIns.Platform.Contracts.Events.IEventPayload<TPayload> =>
        events.Publish(new OutgoingEvent(EventDescriptor.From(TPayload.Descriptor), ClaimEvents.AggregateType, claim.ClaimId.Value.ToString(), payload, keys));
}

/// <summary>Event conventions of CLM: every claim-scoped event is on aggregate <c>Claim</c> keyed by the claim id (ordering key claim_id), so the outbox's per-aggregate sequence is the per-claim gap-free sequence (REQ-CLM-005).</summary>
internal static class ClaimEvents
{
    public const string AggregateType = "Claim";
}

/// <summary>Audit facts of <c>clm.Fnol.submit</c>: object, number and lineage; no free text (P2).</summary>
internal sealed class SubmitFnolAuditor : ICommandAuditor<SubmitFnol, FnolSubmitResponse>
{
    public CommandAuditFacts Describe(SubmitFnol command, Result<FnolSubmitResponse>? result)
    {
        if (result is not { IsSuccess: true } success)
        {
            return new CommandAuditFacts();
        }

        var claim = success.Value.Claim;
        return new CommandAuditFacts
        {
            ObjectRef = ObjectRef.For(ModuleCode.CLM, "Claim", claim.ClaimId),
            ObjectNumber = claim.ClaimNumber.Value,
            BusinessKeys = BusinessKeys.Empty.With("claimId", claim.ClaimId.Value.ToString()).With("policyId", claim.PolicyId.Value.ToString()),
            Changes = AuditDiff.Compute(null, new
            {
                status = "OPEN",
                subStatus = "NEW",
                policyNumber = claim.PolicyNumber.Value,
                snapshotRef = claim.SnapshotRef,
                lossDate = claim.LossDate.ToString(),
                lossCause = claim.LossCause,
                channel = claim.Channel,
                coverageInQuestion = claim.CoverageInQuestion,
                exposures = success.Value.Exposures.Select(e => e.ExposureNumber.Value).ToArray(),
            }),
        };
    }
}
