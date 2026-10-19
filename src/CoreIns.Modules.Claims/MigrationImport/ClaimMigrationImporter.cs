using System.Globalization;
using System.Text.Json;
using CoreIns.Modules.Claims.Commands;
using CoreIns.Modules.Claims.Contracts.Api;
using CoreIns.Modules.Claims.Domain;
using CoreIns.Modules.Claims.Persistence;
using CoreIns.Modules.Compliance.Contracts;
using CoreIns.Modules.Compliance.Contracts.Api;
using CoreIns.Modules.Migration.Contracts.Spi;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Numbering;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CoreIns.Modules.Claims.MigrationImport;

/// <summary>The legacy reserve of an open claim, taken as given.</summary>
internal sealed record ImportReserve(decimal Amount, string? Currency);

/// <summary>
/// The legacy open bodily-injury claim (SL6-MIG-CLM-RI-IMPORT). <c>PolicyId</c> is the core id of the imported policy (the POL xref
/// of the policy's legacy number, resolved by the batch builder). <c>NotifiedOn</c> is the original notified date: the running clock
/// starts there, so the deadline is the one the legacy system showed.
/// </summary>
internal sealed record ImportClaimPayload(
    string LegacyNumber,
    Guid PolicyId,
    Instant LossAt,
    DateOnly NotifiedOn,
    string LossCause,
    string? LossLocation,
    string? Description,
    Guid? ClaimantPartyId,
    string? CoverageCode,
    ImportReserve Reserve,
    string? ClockCode);

/// <summary>
/// <c>IMigrationImporter</c> for module CLM. Creates the open claim (origin MIGRATION) on the imported policy: insured claimant, one
/// MTPL_BI exposure, the opening reserve on the migration record, and the running clock re-created from the original notified date
/// through <c>cmp.Clock.import</c>. No FNOL event is published: the claim was reported in the legacy system, so downstream consumers
/// (RI intake, FIN) do not see it as new business. TODO(hardening): the opening reserve as a ledger line once the migration may post
/// to the claim ledger; the POL xref resolved here from a legacy policy number; compensation when the clock call fails after the claim
/// is saved.
/// </summary>
internal sealed class ClaimMigrationImporter(
    ClaimsDbContext db,
    RequestContext context,
    IClock clock,
    INumberingService numbering,
    ClaimProtection protection,
    ICoverageSource coverage,
    IServiceProvider services,
    IOptions<ClaimsOptions> options) : IMigrationImporter
{
    public const string ObjectType = "Claim";
    public const string DefaultClockCode = "CLM_OPEN_CLAIM_RESPONSE";
    private const string Validation = "MIG-ERR-VALIDATION";

    public string ModuleName => "CLM";

    public async Task<ImportOutcome> ImportAsync(MigrationImportItem item, MigrationImportContext importContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(importContext);
        if (!string.Equals(item.ObjectType, ObjectType, StringComparison.Ordinal))
        {
            return Failed(Validation, $"CLM imports object type Claim, not '{item.ObjectType}'.");
        }

        ImportClaimPayload? payload;
        try
        {
            payload = item.Payload.Deserialize<ImportClaimPayload>(SharedKernelJson.Options);
        }
        catch (JsonException ex)
        {
            return Failed(Validation, "The claim payload is not valid: " + ex.Message);
        }

        var now = clock.Now;
        if (payload is null || string.IsNullOrWhiteSpace(payload.LegacyNumber) || payload.PolicyId == Guid.Empty || string.IsNullOrWhiteSpace(payload.LossCause)
            || payload.Reserve is null || payload.NotifiedOn == default)
        {
            return Failed(Validation, "A claim needs legacyNumber, policyId, lossAt, notifiedOn, lossCause and reserve.");
        }

        var settings = options.Value;
        var currency = payload.Reserve.Currency is { } code ? Currency.FromCode(code) : Currency.FromCode("EUR");
        var reserve = new Money(payload.Reserve.Amount, currency);
        if (payload.Reserve.Amount < 0m || !reserve.IsRoundedToMinorUnits)
        {
            return Failed(Validation, "The reserve must be zero or more, with no more decimals than the currency allows.");
        }

        if (payload.LossAt > now)
        {
            return Failed(Validation, "lossAt cannot be in the future.");
        }

        var notifiedOn = new BusinessDate(payload.NotifiedOn);
        var noticeStart = notifiedOn.StartOfDayIn(settings.Zone);
        if (noticeStart > now || notifiedOn < settings.DateOf(payload.LossAt))
        {
            return Failed(Validation, "notifiedOn must be between the loss date and today.");
        }

        var legalEntity = protection.Current(context);
        if (await db.MigrationImports.AnyAsync(m => m.LegalEntityId == legalEntity && m.LegacyNumber == payload.LegacyNumber, cancellationToken).ConfigureAwait(false))
        {
            return Failed("MIG-ERR-DUPLICATE", $"Legacy claim {payload.LegacyNumber} is already imported.");
        }

        var read = await coverage.ReadAsync(payload.PolicyId, payload.LossAt, now, cancellationToken).ConfigureAwait(false);
        if (read.Outcome != SnapshotReadOutcome.Found || read.Facts is not { } snapshot)
        {
            return Failed("MIG-ERR-DEPENDENCY", $"The policy {payload.PolicyId} could not be read from POL: {read.Detail}");
        }

        if (importContext.DryRun)
        {
            return new ImportOutcome(ImportStatus.Imported, null, null, $"Would import claim {payload.LegacyNumber} on policy {snapshot.PolicyNumber}, reserve {reserve}.");
        }

        var jurisdiction = (context.Jurisdiction ?? throw new InvalidOperationException("The request context has no jurisdiction.")).Value;
        var actor = context.Actor.ToString();
        var claimId = ClaimId.New();
        var state = ClaimStateModel.Machine.Start(ClaimState.Draft).Value;
        var opened = ClaimStateModel.Fire(state, ClaimTrigger.Submit);
        if (opened.IsFailure)
        {
            return Failed(Validation, opened.Error!.Detail ?? opened.Error.Code.ToString());
        }

        var number = await numbering.NextAsync(new NumberRequest(NumberingSchemes.Claim, settings.DateOf(now)), cancellationToken).ConfigureAwait(false);
        var claimNumber = ClaimNumber.Parse(number.Value);
        var (status, subStatus) = ClaimStates.ToColumns(opened.Value);
        var coverageCode = payload.CoverageCode ?? "MTPL";
        var claim = new ClaimRow
        {
            ClaimId = claimId, LegalEntityId = legalEntity, Jurisdiction = jurisdiction, ClaimNumber = claimNumber,
            PolicyId = new PolicyId(snapshot.PolicyId), PolicyNumber = PolicyNumber.Parse(snapshot.PolicyNumber), InsuredPartyId = new PartyId(snapshot.InsuredPartyId),
            SnapshotRef = snapshot.SnapshotRef, SnapshotSegmentId = snapshot.SegmentId, PolicyTermId = snapshot.TermId is { } termId ? new PolicyTermId(termId) : null,
            SnapshotValidAt = snapshot.ValidAt, SnapshotKnownAt = snapshot.KnownAt, SnapshotStatus = Codes.Of(SnapshotStatus.Verified),
            PolicyInForceAtLoss = snapshot.InForce, PolicyStatusAtLoss = snapshot.Status ?? snapshot.NotInForceReason,
            SnapshotCoverageCodes = [.. snapshot.CoverageCodes], ProductCode = snapshot.ProductCode, ProductVersion = snapshot.ProductVersion,
            LineOfBusiness = "MOTOR", LossAt = payload.LossAt, LossDate = settings.DateOf(payload.LossAt), NoticeOn = notifiedOn, LossCause = payload.LossCause,
            LossLocationEncrypted = await protection.EncryptAsync(legalEntity, ClaimProtection.LossLocationField, claimId.Value, (payload.LossLocation ?? "Legacy (not migrated)").Trim(), cancellationToken).ConfigureAwait(false),
            DescriptionEncrypted = await protection.EncryptAsync(legalEntity, ClaimProtection.DescriptionField, claimId.Value, (payload.Description ?? "Legacy (not migrated)").Trim(), cancellationToken).ConfigureAwait(false),
            Channel = "STAFF", ReceiptMedium = null, HandlingSegment = settings.DefaultHandlingSegment, Status = status, SubStatus = subStatus,
            CoverageInQuestion = !snapshot.InForce, CreatedAt = now, CreatedBy = actor, UpdatedAt = now,
        };

        var insured = new ClaimantRow
        {
            ClaimantId = ClaimantId.New(), ClaimId = claimId, LegalEntityId = legalEntity, Jurisdiction = jurisdiction,
            PartyId = claim.InsuredPartyId, ClaimantType = Codes.Of(ClaimantType.Insured), CreatedAt = now, CreatedBy = actor,
        };
        var claimants = new List<ClaimantRow> { insured };
        var injured = insured;
        if (payload.ClaimantPartyId is { } third && third != insured.PartyId.Value)
        {
            injured = new ClaimantRow
            {
                ClaimantId = ClaimantId.New(), ClaimId = claimId, LegalEntityId = legalEntity, Jurisdiction = jurisdiction,
                PartyId = new PartyId(third), ClaimantType = Codes.Of(ClaimantType.ThirdParty), CreatedAt = now, CreatedBy = actor,
            };
            claimants.Add(injured);
        }

        claim.LastExposureSequence = 1;
        var exposure = SubmitFnolHandler.NewExposure(
            claim, 1, Codes.Of(ExposureKind.MtplBi), coverageCode, injured.ClaimantId, null, null, CoverageRules.Indicate(snapshot, coverageCode), now, actor);

        db.Claims.Add(claim);
        db.Claimants.AddRange(claimants);
        db.Exposures.Add(exposure);
        var import = new ClaimMigrationImportRow
        {
            ClaimId = claimId, LegalEntityId = legalEntity, BatchId = importContext.BatchId, LegacyNumber = payload.LegacyNumber, OriginalNotifiedOn = notifiedOn,
            OpeningReserve = reserve.Amount, Currency = currency.Code, ImportedAt = now,
        };
        db.MigrationImports.Add(import);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Failed("MIG-ERR-DUPLICATE", $"Legacy claim {payload.LegacyNumber} conflicts with an existing record.");
        }

        // The running clock keeps its original start, so its deadline is the legacy one (CLM-010, CMP-009).
        var clocks = services.GetService<IComplianceClockService>();
        if (clocks is null)
        {
            return Failed("MIG-ERR-DEPENDENCY", "CMP is not registered in this deployment; the claim clock cannot be imported.");
        }

        var subject = new ObjectRef(ModuleCode.CLM, "Claim", claimId.Value.ToString());
        var record = new
        {
            clockCode = payload.ClockCode ?? DefaultClockCode,
            subjectRef = new { module = "CLM", type = "Claim", id = claimId.Value.ToString() },
            key = "migration",
            startedAt = noticeStart.ToString(),
        };
        try
        {
            var imported = await clocks.ImportAsync(
                new ClockImportRequest { LegacyRecords = JsonSerializer.SerializeToElement(new[] { record }, SharedKernelJson.Options) }, CommandOptions.New(), cancellationToken)
                .ConfigureAwait(false);
            import.ClockId = imported.GetProperty("instances")[0].GetProperty("instanceId").GetGuid();
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException ex)
        {
            return Failed("MIG-ERR-DEPENDENCY", $"The claim clock was refused by CMP: {ex.Error.Code} {subject.Id}.");
        }

        return new ImportOutcome(ImportStatus.Imported, claimId.Value, null, $"Claim {claimNumber.Value} (legacy {payload.LegacyNumber}), reserve {reserve.Amount.ToString(CultureInfo.InvariantCulture)}.");
    }

    private static ImportOutcome Failed(string code, string message) => new(ImportStatus.Failed, null, code, message);
}
