using CoreIns.Modules.Claims.Contracts.Api;
using CoreIns.Modules.Claims.Domain;
using CoreIns.Modules.Claims.Persistence;
using CoreIns.Modules.Claims.Queries;
using CoreIns.Modules.Claims.Services;
using CoreIns.Modules.Market.Contracts.Spi;
using CoreIns.Modules.Policy.Contracts;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Claims.Commands.FriendlySettlement;

/// <summary>Result of an at-fault clearing notification: the case, its claim and exposure, and the system set that pays the CLEARING payable.</summary>
public sealed record FsNotificationReceived(FsCaseView FsCase, ClaimId ClaimId, ExposureId ExposureId, Guid SetId, string SetStatus, bool Replay);

/// <summary>
/// Inbound at-fault clearing message (REQ-CLM-158). Internal in-process command; the only transport in slice 4 is the
/// Development-only endpoint (D-SL4-18). The message is parsed by the SPI; a malformed one is <c>CLM-ERR-VALIDATION</c>.
/// </summary>
internal sealed record ReceiveFsNotification(string Format, byte[] Body) : ICommand<FsNotificationReceived>;

/// <summary>
/// Creates or matches the claim on our policy (policy number and accident date), its MTPL_PD exposure, the at-fault case and the
/// CLEARING payment (system set from the FS_NOTIFICATION evidence; authority is still checked, so the set normally waits for a
/// human approval). Idempotent on the clearing reference.
/// </summary>
internal sealed class ReceiveFsNotificationHandler(
    ClaimsDbContext db, RequestContext context, IClock clock, ClaimProtection protection, FsGate gate, FinancialsReader financials,
    IClaimFinancialEngine engine, IServiceProvider services,
    ICommandHandler<CreateExposure, ExposureCreateResponse> createExposure,
    ICommandHandler<SubmitFnol, FnolSubmitResponse> submitFnol) : ICommandHandler<ReceiveFsNotification, FsNotificationReceived>
{
    public async Task<Result<FsNotificationReceived>> HandleAsync(ReceiveFsNotification command, CancellationToken cancellationToken)
    {
        var entity = protection.Current(context);
        var spi = await gate.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (spi.IsFailure)
        {
            return spi.Error!;
        }

        FsNotification notification;
        try
        {
            notification = await spi.Value.ReceiveNotificationAsync(new FsNotificationMessage(command.Format, command.Body), cancellationToken).ConfigureAwait(false);
        }
        catch (SpiException ex)
        {
            return FsCases.FromSpi(ex);
        }

        if (notification.Role != FsRole.AtFaultInsurer || notification.ClearingValue.Currency != "EUR" || notification.ClearingValue.Amount <= 0m
            || !FsCases.TryParty(notification.CounterpartyInsurerCode, out var counterparty))
        {
            return FnolAssessment.Invalid("notification", "FS_NOTIFICATION", "The notification must be an at-fault EUR notification from an insurer party.");
        }

        // Replay: the clearing reference is the idempotency key.
        var existing = await db.FsCases.AsNoTracking().SingleOrDefaultAsync(
            c => c.LegalEntityId == entity && c.NotificationReference == notification.ClearingReference, cancellationToken).ConfigureAwait(false);
        if (existing is { ExposureId: { } knownExposure })
        {
            return await FinishAsync(existing, knownExposure, replay: true, cancellationToken).ConfigureAwait(false);
        }

        PolicyNumber policyNumber;
        try
        {
            policyNumber = PolicyNumber.Parse(notification.PolicyNumber);
        }
        catch (FormatException)
        {
            return FnolAssessment.Invalid("policyNumber", "POLICY_NUMBER", "The policy number is not valid.");
        }

        var lossDate = new BusinessDate(notification.AccidentDate);
        var claimId = await db.Claims.AsNoTracking().Where(c => c.LegalEntityId == entity && c.PolicyNumber == policyNumber && c.LossDate == lossDate
            && c.Status == ClaimStates.Open).OrderBy(c => c.CreatedAt).Select(c => (ClaimId?)c.ClaimId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (claimId is null)
        {
            var created = await CreateClaimAsync(policyNumber, notification, counterparty, cancellationToken).ConfigureAwait(false);
            if (created.IsFailure)
            {
                return created.Error!;
            }

            claimId = created.Value;
        }

        var claim = await ClaimSupport.LoadAsync(db, entity, claimId.Value, cancellationToken).ConfigureAwait(false);
        if (claim is null)
        {
            return ClaimSupport.NotFound("claim");
        }

        var exposureId = await db.Exposures.AsNoTracking().Where(e => e.ClaimId == claim.ClaimId && e.Kind == "MTPL_PD" && e.Status == ClaimStates.Open)
            .OrderBy(e => e.Sequence).Select(e => (ExposureId?)e.ExposureId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (exposureId is null)
        {
            var coverage = claim.SnapshotCoverageCodes.FirstOrDefault(c => c.StartsWith("MTPL", StringComparison.Ordinal));
            if (coverage is null)
            {
                return FnolAssessment.Invalid("coverageCode", "COVERAGE", "The policy has no MTPL cover to settle the clearing notification.");
            }

            var exposure = await createExposure.HandleAsync(new CreateExposure(new ExposureCreateRequest
            {
                ClaimId = claim.ClaimId, ExpectedRecordVersion = claim.RecordVersion, Kind = ExposureKind.MtplPd, CoverageCode = coverage,
            }), cancellationToken).ConfigureAwait(false);
            if (exposure.IsFailure)
            {
                return exposure.Error!;
            }

            exposureId = exposure.Value.Exposure.ExposureId;
        }

        var now = clock.Now;
        var row = new FsCaseRow
        {
            FsCaseId = Guid.CreateVersion7(), ClaimId = claim.ClaimId, ExposureId = exposureId, Role = FsCases.AtFaultRole, CounterpartyInsurerPartyId = counterparty,
            ClearingValue = notification.ClearingValue.Amount, Currency = "EUR", ClearingReference = notification.ClearingReference,
            NotificationReference = notification.ClearingReference, Status = "ACCEPTED", LegalEntityId = entity, Jurisdiction = claim.Jurisdiction,
            CreatedAt = now, CreatedBy = context.Actor.ToString(), UpdatedAt = now,
        };
        db.FsCases.Add(row);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ClaimSupport.IsUniqueViolation(ex, "ux_fs_case_open_claim_role"))
        {
            return DomainError.Of(ModuleCode.CLM, "ILLEGAL-TRANSITION", "An at-fault Friendly Settlement leg is already open on this claim.");
        }
        catch (DbUpdateException ex) when (ClaimSupport.IsUniqueViolation(ex, "ux_fs_case_notification"))
        {
            return ClaimSupport.Stale();
        }

        return await FinishAsync(row, exposureId.Value, replay: false, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<FsNotificationReceived>> FinishAsync(FsCaseRow row, ExposureId exposureId, bool replay, CancellationToken cancellationToken)
    {
        TransactionSetSubmitResponse set;
        try
        {
            set = await engine.SubmitSystemSetAsync(row.ClaimId, new ClaimFinancialEvidence("FS_NOTIFICATION", row.NotificationReference!), cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }

        return new FsNotificationReceived(await FsCases.ViewAsync(db, financials, row, cancellationToken).ConfigureAwait(false), row.ClaimId, exposureId, set.SetId, set.Status, replay);
    }

    private async Task<Result<ClaimId>> CreateClaimAsync(PolicyNumber policyNumber, FsNotification notification, PartyId counterparty, CancellationToken cancellationToken)
    {
        var policies = services.GetService<IPolicyPolicyService>();
        if (policies is null)
        {
            return DomainError.Of(ModuleCode.CLM, "DEPENDENCY-UNAVAILABLE", "The policy service is not available.");
        }

        var found = await policies.SearchAsync(policyNumber: policyNumber, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (found.Items.Count != 1)
        {
            return ClaimSupport.NotFound("policy of the notification");
        }

        var coverage = "MTPL";
        var lossAt = Instant.FromDateTimeOffset(new DateTimeOffset(notification.AccidentDate.ToDateTime(new TimeOnly(9, 0)), TimeSpan.Zero));
        var response = await submitFnol.HandleAsync(new SubmitFnol(new FnolSubmitRequest
        {
            LineOfBusiness = "MOTOR", PolicyId = found.Items[0].PolicyId, PolicyNumber = policyNumber, LossAt = lossAt, LossCause = "COLLISION",
            LossLocation = "Reported by the clearing house", Description = "Created from a Friendly Settlement clearing notification", Channel = "STAFF",
            Exposures = [new FnolExposureInput { Kind = ExposureKind.MtplPd, CoverageCode = coverage }],
            LiabilityFacts = new LiabilityFacts
            {
                InsuredFaultPct = 100m, FaultSource = "FS_NOTIFICATION", CounterpartyInsurerPartyId = counterparty.Value, JointReportFlag = true,
                VehicleCount = 2, AccidentInGreece = true,
            },
        }), cancellationToken).ConfigureAwait(false);
        if (response.IsFailure)
        {
            return response.Error!;
        }

        // FNOL does not persist liability facts yet (clm.Claim.update belongs to SL4-CLM-RECOVERY-OPS): the notification states them.
        var row = await ClaimSupport.LoadAsync(db, protection.Current(context), response.Value.ClaimId, cancellationToken).ConfigureAwait(false);
        if (row is not null)
        {
            row.FaultInsuredPct = 100m;
            row.FaultSource = "FS_NOTIFICATION";
            row.CounterpartyInsurerPartyId = counterparty;
            row.JointAccidentReport = true;
            row.VehicleCount = 2;
            row.AccidentInGreece = true;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return response.Value.ClaimId;
    }
}

internal sealed class ReceiveFsNotificationAuditor : ICommandAuditor<ReceiveFsNotification, FsNotificationReceived>
{
    public CommandAuditFacts Describe(ReceiveFsNotification command, Result<FsNotificationReceived>? result) =>
        result is { IsSuccess: true } ok
            ? new CommandAuditFacts
            {
                ObjectRef = ObjectRef.For(ModuleCode.CLM, "FsCase", ok.Value.FsCase.FsCaseId),
                BusinessKeys = BusinessKeys.Empty.With("claimId", ok.Value.ClaimId.Value.ToString()).With("fsCaseId", ok.Value.FsCase.FsCaseId.Value.ToString()),
                Changes = AuditDiff.Compute(null, new { clearingReference = ok.Value.FsCase.ClearingReference, setId = ok.Value.SetId, replay = ok.Value.Replay }),
            }
            : new CommandAuditFacts();
}
