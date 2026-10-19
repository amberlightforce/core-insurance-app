using System.Text.Json;
using CoreIns.Modules.Claims.Contracts.Api;
using CoreIns.Modules.Claims.Contracts.Events;
using CoreIns.Modules.Claims.Domain;
using CoreIns.Modules.Claims.Persistence;
using CoreIns.Modules.Claims.Queries;
using CoreIns.Modules.Market.Contracts.Spi;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Item = CoreIns.Modules.Claims.Contracts.Api.TransactionSetBuildRequest.TransactionItem;

namespace CoreIns.Modules.Claims.Commands.FriendlySettlement;

/// <summary><c>clm.FriendlySettlement.evaluate</c> (REQ-CLM-155, -156): eligibility through the SPI, result stored on the case.</summary>
internal sealed record EvaluateFriendlySettlement(FsEvaluateRequest Request) : ICommand<FsEvaluateResponse>;

/// <summary><c>clm.FriendlySettlement.openOwnSettlement</c> (REQ-CLM-157): own-settlement exposure and the FS receivable.</summary>
internal sealed record OpenOwnSettlement(FsOpenOwnSettlementRequest Request) : ICommand<FsOpenOwnSettlementResponse>;

/// <summary><c>clm.FriendlySettlement.submit</c> (REQ-CLM-159, submit part).</summary>
internal sealed record SubmitFriendlySettlement(FsSubmitRequest Request) : ICommand<FsSubmitResponse>;

internal sealed class EvaluateFriendlySettlementValidator : AbstractValidator<EvaluateFriendlySettlement>
{
    public EvaluateFriendlySettlementValidator() => RuleFor(c => c.Request.ClaimId.Value).NotEmpty();
}

internal sealed class OpenOwnSettlementValidator : AbstractValidator<OpenOwnSettlement>
{
    public OpenOwnSettlementValidator() => RuleFor(c => c.Request.ExpectedRecordVersion).GreaterThanOrEqualTo(1);
}

internal sealed class SubmitFriendlySettlementValidator : AbstractValidator<SubmitFriendlySettlement>
{
    public SubmitFriendlySettlementValidator() => RuleFor(c => c.Request.ExpectedRecordVersion).GreaterThanOrEqualTo(1);
}

/// <summary>
/// Evaluates the claim's eligibility. Facts come from the claim, its liability facts and its financials (D-SL4-17); the amount to
/// clear is the gross incurred (open reserve + paid) on the own-damage exposures. The stored result keeps rule id, version and legal
/// status (REQ-MKT-313). A case that already has its recovery cannot be re-evaluated.
/// </summary>
internal sealed class EvaluateFriendlySettlementHandler(
    ClaimsDbContext db, RequestContext context, IClock clock, ClaimProtection protection, FsGate gate, FinancialsReader financials)
    : ICommandHandler<EvaluateFriendlySettlement, FsEvaluateResponse>
{
    public async Task<Result<FsEvaluateResponse>> HandleAsync(EvaluateFriendlySettlement command, CancellationToken cancellationToken)
    {
        var entity = protection.Current(context);
        var spi = await gate.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (spi.IsFailure)
        {
            return spi.Error!;
        }

        var claim = await ClaimSupport.LoadAsync(db, entity, command.Request.ClaimId, cancellationToken).ConfigureAwait(false);
        if (claim is null)
        {
            return ClaimSupport.NotFound("claim");
        }

        if (claim.Status != ClaimStates.Open)
        {
            return DomainError.Of(ModuleCode.CLM, "ILLEGAL-TRANSITION", "Friendly Settlement needs an open claim.");
        }

        if (claim.CounterpartyInsurerPartyId is not { } counterparty || claim.FaultInsuredPct is null)
        {
            return FnolAssessment.Invalid("claim.liabilityFacts", "LIABILITY_FACTS", "Record the counterparty insurer and the insured fault percentage first.");
        }

        var exposures = await db.Exposures.AsNoTracking().Where(e => e.ClaimId == claim.ClaimId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var lines = await financials.LinesAsync(claim.ClaimId, cancellationToken).ConfigureAwait(false);
        var approved = await financials.ApprovedAsync(claim.ClaimId, null, cancellationToken).ConfigureAwait(false);
        var own = exposures.Where(e => e.Kind == "OWN_DAMAGE").Select(e => e.ExposureId).ToHashSet();
        var amount = lines.Where(l => own.Contains(l.ExposureId))
            .Sum(l => approved.TryGetValue(l.ReserveLineId, out var a) ? a.OpenReserve + a.Paid : 0m);
        var bodily = exposures.Any(e => e.Kind is "MTPL_BI" or "LIABILITY_INJURY");
        var facts = new FsClaimFacts
        {
            LegalEntityId = entity.Value,
            ClaimId = claim.ClaimId.Value,
            AccidentDate = claim.LossDate.Value,
            ProductLine = claim.LineOfBusiness,
            CounterpartyInsurerCode = FsCases.Code(counterparty),
            AccidentInCountry = claim.AccidentInGreece ?? false,
            VehicleCount = claim.VehicleCount ?? 0,
            InsuredFaultPercent = claim.FaultInsuredPct.Value,
            DamageTypes = bodily ? ["BODILY_INJURY"] : ["MATERIAL_DAMAGE"],
            Amount = new SpiMoney(amount, "EUR"),
            JointReport = claim.JointAccidentReport ?? false,
        };

        FsEligibilityDecision decision;
        try
        {
            decision = await spi.Value.EvaluateEligibilityAsync(facts, cancellationToken).ConfigureAwait(false);
        }
        catch (SpiException ex)
        {
            return FsCases.FromSpi(ex);
        }

        if (decision.Result == FsEligibilityResult.NotApplicable)
        {
            return FsGate.Disabled("The pack has no Friendly Settlement.");
        }

        if (gate.Refuses(decision))
        {
            return FsGate.Disabled("The eligibility values are not settled; refused in Production (PITFALLS 36).");
        }

        var now = clock.Now;
        var eligible = decision.Result == FsEligibilityResult.Eligible;
        var row = await db.FsCases.SingleOrDefaultAsync(c => c.LegalEntityId == entity && c.ClaimId == claim.ClaimId && c.Role == FsCases.OwnRole
            && c.Status != "REJECTED" && c.Status != "RECONCILED", cancellationToken).ConfigureAwait(false);
        if (row is not null && (row.RecoveryId is not null || row.Status is not ("ELIGIBILITY_PENDING" or "ELIGIBLE" or "NOT_ELIGIBLE")))
        {
            return DomainError.Of(ModuleCode.CLM, "ILLEGAL-TRANSITION", "The Friendly Settlement case is already in progress.");
        }

        if (row is null)
        {
            row = new FsCaseRow
            {
                FsCaseId = Guid.CreateVersion7(), ClaimId = claim.ClaimId, Role = FsCases.OwnRole, LegalEntityId = entity, Jurisdiction = claim.Jurisdiction,
                CreatedAt = now, CreatedBy = context.Actor.ToString(),
            };
            db.FsCases.Add(row);
        }
        else
        {
            row.RecordVersion++;
        }

        var eligibility = FsCases.EligibilityOf(decision);
        row.CounterpartyInsurerPartyId = counterparty;
        row.EligibilityResult = JsonSerializer.SerializeToDocument(eligibility, SharedKernelJson.Options);
        row.EligibilityRuleId = decision.RuleId;
        row.EligibilityRuleVersion = decision.RuleVersion;
        row.LegalStatus = eligibility.LegalStatus;
        row.Provisional = decision.Provisional;
        row.ClearingValue = eligible ? decision.ClearingValue?.Amount : null;
        row.Status = eligible ? "ELIGIBLE" : "NOT_ELIGIBLE";
        row.UpdatedAt = now;
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ClaimSupport.IsUniqueViolation(ex, "ux_fs_case_open_claim_role"))
        {
            return ClaimSupport.Stale();
        }

        return new FsEvaluateResponse { FsCase = await FsCases.ViewAsync(db, financials, row, cancellationToken).ConfigureAwait(false), Eligibility = eligibility };
    }
}

/// <summary>
/// Opens the own settlement of an eligible case: the <c>FS_OWN_SETTLEMENT</c> exposure (the customer is paid there through the
/// ordinary set builder), the FRIENDLY_SETTLEMENT recovery, and a recovery reserve at the clearing value (REQ-CLM-157). The
/// reserve set goes through the normal authority check as the calling user.
/// </summary>
internal sealed class OpenOwnSettlementHandler(
    ClaimsDbContext db, RequestContext context, IClock clock, ClaimProtection protection, FsGate gate, FinancialsReader financials,
    ICommandHandler<CreateExposure, ExposureCreateResponse> createExposure,
    ICommandHandler<BuildTransactionSet, TransactionSetBuildResponse> build,
    ICommandHandler<SubmitTransactionSet, TransactionSetSubmitResponse> submit) : ICommandHandler<OpenOwnSettlement, FsOpenOwnSettlementResponse>
{
    public async Task<Result<FsOpenOwnSettlementResponse>> HandleAsync(OpenOwnSettlement command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var entity = protection.Current(context);
        var spi = await gate.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (spi.IsFailure)
        {
            return spi.Error!;
        }

        var claimId = await db.FsCases.AsNoTracking().Where(c => c.FsCaseId == request.FsCaseId.Value && c.LegalEntityId == entity)
            .Select(c => (ClaimId?)c.ClaimId).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (claimId is null)
        {
            return ClaimSupport.NotFound("Friendly Settlement case");
        }

        var claim = await ClaimSupport.LoadAsync(db, entity, claimId.Value, cancellationToken).ConfigureAwait(false);
        var row = await FsCases.LockAsync(db, entity, request.FsCaseId, cancellationToken).ConfigureAwait(false);
        if (claim is null || row is null)
        {
            return ClaimSupport.NotFound("Friendly Settlement case");
        }

        if (row.RecordVersion != request.ExpectedRecordVersion)
        {
            return ClaimSupport.Stale(row.RecordVersion);
        }

        if (row.Role != FsCases.OwnRole || row.Status != "ELIGIBLE" || row.RecoveryId is not null || row.ClearingValue is not { } value || value <= 0m)
        {
            return DomainError.Of(ModuleCode.CLM, "ILLEGAL-TRANSITION", "Only an eligible case with a positive clearing value, not yet opened, can open its own settlement.");
        }

        var existing = await db.Exposures.AsNoTracking().Where(e => e.ClaimId == claim.ClaimId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var coverage = existing.FirstOrDefault(e => e.Kind == "OWN_DAMAGE")?.CoverageCode
            ?? claim.SnapshotCoverageCodes.FirstOrDefault(c => c is "OD" || c.StartsWith("OWN", StringComparison.Ordinal));
        if (coverage is null)
        {
            return FnolAssessment.Invalid("coverageCode", "COVERAGE", "The policy has no own-damage cover to settle through.");
        }

        var created = await createExposure.HandleAsync(new CreateExposure(new ExposureCreateRequest
        {
            ClaimId = claim.ClaimId, ExpectedRecordVersion = claim.RecordVersion, Kind = ExposureKind.FsOwnSettlement, CoverageCode = coverage,
            ClaimantPartyId = request.ClaimantPartyId,
        }), cancellationToken).ConfigureAwait(false);
        if (created.IsFailure)
        {
            return created.Error!;
        }

        var exposureId = created.Value.Exposure.ExposureId;
        var now = clock.Now;
        var recovery = new RecoveryRow
        {
            RecoveryId = RecoveryId.New(), ClaimId = claim.ClaimId, ExposureId = exposureId, Type = "FRIENDLY_SETTLEMENT",
            CounterpartyPartyId = row.CounterpartyInsurerPartyId, ExpectedAmount = value, Currency = "EUR", Status = "OPEN", FsCaseId = row.FsCaseId,
            LegalEntityId = entity, Jurisdiction = claim.Jurisdiction, CreatedAt = now, CreatedBy = context.Actor.ToString(), UpdatedAt = now,
        };
        db.Recoveries.Add(recovery);
        row.ExposureId = exposureId;
        row.RecoveryId = recovery.RecoveryId;
        row.RecordVersion++;
        row.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var built = await build.HandleAsync(new BuildTransactionSet(new TransactionSetBuildRequest
        {
            ClaimId = claim.ClaimId,
            Transactions =
            [
                new Item
                {
                    Kind = Item.KindValue.RecoveryReserve, ExposureId = exposureId, CostType = "INDEMNITY", CostCategory = "VEHICLE_REPAIR",
                    Amount = new Money(value, Currency.EUR), RecoveryId = recovery.RecoveryId, Reason = "FS_CLEARING_RECEIVABLE",
                },
            ],
        }), cancellationToken).ConfigureAwait(false);
        if (built.IsFailure)
        {
            return built.Error!;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var submitted = await submit.HandleAsync(new SubmitTransactionSet(new TransactionSetSubmitRequest { SetId = built.Value.SetId }), cancellationToken).ConfigureAwait(false);
        if (submitted.IsFailure)
        {
            return submitted.Error!;
        }

        return new FsOpenOwnSettlementResponse
        {
            FsCase = await FsCases.ViewAsync(db, financials, row, cancellationToken).ConfigureAwait(false),
            ExposureId = exposureId,
            RecoveryId = recovery.RecoveryId,
        };
    }
}

/// <summary>Submits the opened case to the clearing house once (idempotent on the case id at the SPI) and publishes <c>FriendlySettlementSubmitted</c>.</summary>
internal sealed class SubmitFriendlySettlementHandler(
    ClaimsDbContext db, RequestContext context, IClock clock, ClaimProtection protection, FsGate gate, FinancialsReader financials, IEventPublisher events)
    : ICommandHandler<SubmitFriendlySettlement, FsSubmitResponse>
{
    public async Task<Result<FsSubmitResponse>> HandleAsync(SubmitFriendlySettlement command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var entity = protection.Current(context);
        var spi = await gate.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (spi.IsFailure)
        {
            return spi.Error!;
        }

        var claimId = await db.FsCases.AsNoTracking().Where(c => c.FsCaseId == request.FsCaseId.Value && c.LegalEntityId == entity)
            .Select(c => (ClaimId?)c.ClaimId).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (claimId is null)
        {
            return ClaimSupport.NotFound("Friendly Settlement case");
        }

        var claim = await ClaimSupport.LoadAsync(db, entity, claimId.Value, cancellationToken).ConfigureAwait(false);
        var row = await FsCases.LockAsync(db, entity, request.FsCaseId, cancellationToken).ConfigureAwait(false);
        if (claim is null || row is null)
        {
            return ClaimSupport.NotFound("Friendly Settlement case");
        }

        if (row.RecordVersion != request.ExpectedRecordVersion)
        {
            return ClaimSupport.Stale(row.RecordVersion);
        }

        if (row.Role != FsCases.OwnRole || row.Status != "ELIGIBLE" || row.RecoveryId is null || row.ClearingValue is not { } value
            || row.EligibilityResult?.Deserialize<FsEligibility>(SharedKernelJson.Options) is not { } eligibility)
        {
            return DomainError.Of(ModuleCode.CLM, "ILLEGAL-TRANSITION", "Only an eligible case with its own settlement opened can be submitted.");
        }

        FsSubmissionResult result;
        try
        {
            result = await spi.Value.SubmitAsync(new FsSubmission
            {
                LegalEntityId = entity.Value, FsCaseId = row.FsCaseId, ClaimId = claim.ClaimId.Value,
                CounterpartyInsurerCode = FsCases.Code(row.CounterpartyInsurerPartyId), AccidentDate = claim.LossDate.Value,
                ClearingValue = new SpiMoney(value, "EUR"),
                ClearingValueBasis = eligibility.ClearingValueBasis == FsEligibility.ClearingValueBasisValue.Average ? ClearingValueBasis.Average : ClearingValueBasis.Actual,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (SpiException ex)
        {
            return FsCases.FromSpi(ex);
        }

        row.ClearingReference = result.ClearingReference;
        row.Status = "SUBMITTED";
        row.RecordVersion++;
        row.UpdatedAt = clock.Now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        events.Publish(new OutgoingEvent(
            EventDescriptor.From(FriendlySettlementSubmittedV1.Descriptor),
            ClaimEvents.AggregateType,
            claim.ClaimId.Value.ToString(),
            new FriendlySettlementSubmittedV1
            {
                FsCaseId = new FsCaseId(row.FsCaseId), Counterparty = FsCases.Code(row.CounterpartyInsurerPartyId), ClearingValue = new Money(value, Currency.EUR),
                ClaimId = claim.ClaimId, ClearingReference = result.ClearingReference, ClearingValueBasis = eligibility.ClearingValueBasis?.ToString().ToUpperInvariant(),
                RuleId = eligibility.RuleId, RuleVersion = eligibility.RuleVersion, LegalStatus = eligibility.LegalStatus, Provisional = eligibility.Provisional,
            },
            BusinessKeys.Empty.With("claimId", claim.ClaimId.Value.ToString()).With("fsCaseId", row.FsCaseId.ToString())));

        return new FsSubmitResponse { FsCase = await FsCases.ViewAsync(db, financials, row, cancellationToken).ConfigureAwait(false), ClearingReference = result.ClearingReference };
    }
}

internal sealed class EvaluateFriendlySettlementAuditor : ICommandAuditor<EvaluateFriendlySettlement, FsEvaluateResponse>
{
    public CommandAuditFacts Describe(EvaluateFriendlySettlement command, Result<FsEvaluateResponse>? result) =>
        result is { IsSuccess: true } ok
            ? new CommandAuditFacts
            {
                ObjectRef = ObjectRef.For(ModuleCode.CLM, "FsCase", ok.Value.FsCase.FsCaseId),
                BusinessKeys = BusinessKeys.Empty.With("claimId", command.Request.ClaimId.Value.ToString()).With("fsCaseId", ok.Value.FsCase.FsCaseId.Value.ToString()),
                Changes = AuditDiff.Compute(null, new { state = ok.Value.FsCase.State.ToString(), reasons = ok.Value.Eligibility.Reasons, ruleId = ok.Value.Eligibility.RuleId }),
            }
            : new CommandAuditFacts
            {
                ObjectRef = ObjectRef.For(ModuleCode.CLM, "Claim", command.Request.ClaimId),
                BusinessKeys = BusinessKeys.Empty.With("claimId", command.Request.ClaimId.Value.ToString()),
            };
}

internal sealed class OpenOwnSettlementAuditor : ICommandAuditor<OpenOwnSettlement, FsOpenOwnSettlementResponse>
{
    public CommandAuditFacts Describe(OpenOwnSettlement command, Result<FsOpenOwnSettlementResponse>? result) =>
        new()
        {
            ObjectRef = ObjectRef.For(ModuleCode.CLM, "FsCase", command.Request.FsCaseId),
            BusinessKeys = BusinessKeys.Empty.With("fsCaseId", command.Request.FsCaseId.Value.ToString()),
            Changes = AuditDiff.Compute(null, new { exposureId = result is { IsSuccess: true } ok ? ok.Value.ExposureId.Value : (Guid?)null }),
        };
}

internal sealed class SubmitFriendlySettlementAuditor : ICommandAuditor<SubmitFriendlySettlement, FsSubmitResponse>
{
    public CommandAuditFacts Describe(SubmitFriendlySettlement command, Result<FsSubmitResponse>? result) =>
        new()
        {
            ObjectRef = ObjectRef.For(ModuleCode.CLM, "FsCase", command.Request.FsCaseId),
            BusinessKeys = BusinessKeys.Empty.With("fsCaseId", command.Request.FsCaseId.Value.ToString()),
            Changes = AuditDiff.Compute(null, new { clearingReference = result is { IsSuccess: true } ok ? ok.Value.ClearingReference : null }),
        };
}
