using System.Text.Json;
using CoreIns.Modules.Compliance.Contracts;
using CoreIns.Modules.Compliance.Contracts.Api;
using CoreIns.Modules.Compliance.Persistence;
using CoreIns.Modules.Migration.Contracts.Spi;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CoreIns.Modules.Compliance.MigrationImport;

/// <summary>Object types the CMP importer takes (SL6-MIG-CLM-RI-IMPORT).</summary>
internal static class ComplianceImportTypes
{
    public const string Clock = "Clock";
    public const string BureauHandover = "BureauHandover";
    public const string FiscalContinuity = "FiscalContinuity";
}

internal sealed record BureauHandoverPayload(string VehicleRef, DateOnly HandoverDate, Guid? PolicyId);

internal sealed record LegacyMark(long Number, string Mark);

internal sealed record FiscalContinuityPayload(string SeriesId, long LastLegacyNumber, List<LegacyMark>? Marks);

/// <summary>
/// <c>IMigrationImporter</c> for module CMP (SL6-MIG-CLM-RI-IMPORT). <c>Clock</c> re-creates a running clock (cmp.Clock.import),
/// <c>BureauHandover</c> records the handover date of a vehicle (record only, nothing is transmitted) and <c>FiscalContinuity</c>
/// moves a fiscal series to the last legacy number (so the next issued number continues the sequence) and keeps the legacy MARKs for
/// credit lookups. CMP has no reverser: a batch holding CMP objects is NOT_REVERSIBLE (D-SL6-05).
/// </summary>
internal sealed class ComplianceMigrationImporter(
    ComplianceDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock, IComplianceClockService clocks) : IMigrationImporter
{
    private const string Validation = "MIG-ERR-VALIDATION";

    public string ModuleName => "CMP";

    public Task<ImportOutcome> ImportAsync(MigrationImportItem item, MigrationImportContext importContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(importContext);
        return item.ObjectType switch
        {
            ComplianceImportTypes.Clock => ImportClockAsync(item, importContext, cancellationToken),
            ComplianceImportTypes.BureauHandover => ImportHandoverAsync(item, importContext, cancellationToken),
            ComplianceImportTypes.FiscalContinuity => ImportFiscalAsync(item, importContext, cancellationToken),
            _ => Task.FromResult(Failed(Validation, $"CMP imports Clock, BureauHandover or FiscalContinuity, not '{item.ObjectType}'.")),
        };
    }

    private async Task<ImportOutcome> ImportClockAsync(MigrationImportItem item, MigrationImportContext importContext, CancellationToken cancellationToken)
    {
        // The payload is one legacy clock record; cmp.Clock.import takes an array.
        var request = new ClockImportRequest { LegacyRecords = JsonSerializer.SerializeToElement(new[] { item.Payload }) };
        try
        {
            var result = await clocks.ImportAsync(request, importContext.DryRun ? CommandOptions.New() with { DryRun = true } : CommandOptions.New(), cancellationToken)
                .ConfigureAwait(false);
            var instance = result.GetProperty("instances")[0];
            return new ImportOutcome(ImportStatus.Imported, instance.GetProperty("instanceId").GetGuid(), null, $"Clock deadline {instance.GetProperty("deadline").GetString()}.");
        }
        catch (DomainException ex)
        {
            return Failed(Validation, ex.Error.Detail ?? ex.Error.Code.ToString());
        }
    }

    private async Task<ImportOutcome> ImportHandoverAsync(MigrationImportItem item, MigrationImportContext importContext, CancellationToken cancellationToken)
    {
        var payload = Parse<BureauHandoverPayload>(item);
        if (payload is null || string.IsNullOrWhiteSpace(payload.VehicleRef) || payload.HandoverDate == default)
        {
            return Failed(Validation, "A bureau handover needs vehicleRef and handoverDate.");
        }

        var legalEntity = LegalEntity();

        // TODO(hardening): the market pack's plate normalisation; here upper case without spaces or hyphens.
        var vehicle = new string([.. payload.VehicleRef.Where(char.IsLetterOrDigit)]).ToUpperInvariant();
        if (vehicle.Length == 0)
        {
            return Failed(Validation, "vehicleRef has no letters or digits.");
        }

        if (await db.BureauHandovers.AnyAsync(h => h.LegalEntityId == legalEntity && h.VehicleRef == vehicle, cancellationToken).ConfigureAwait(false))
        {
            return Failed("MIG-ERR-DUPLICATE", $"A handover date is already recorded for {vehicle}.");
        }

        if (importContext.DryRun)
        {
            return new ImportOutcome(ImportStatus.Imported, null, null, $"Would record handover of {vehicle} on {payload.HandoverDate:yyyy-MM-dd}.");
        }

        var row = new BureauHandoverRow
        {
            HandoverId = Guid.CreateVersion7(), LegalEntityId = legalEntity, VehicleRef = vehicle, PolicyId = payload.PolicyId,
            HandoverDate = new BusinessDate(payload.HandoverDate), BatchId = importContext.BatchId, RecordedAt = clock.Now,
        };
        db.BureauHandovers.Add(row);
        return await SaveAsync(row.HandoverId, $"Handover of {vehicle} recorded.", item.LegacyId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ImportOutcome> ImportFiscalAsync(MigrationImportItem item, MigrationImportContext importContext, CancellationToken cancellationToken)
    {
        var payload = Parse<FiscalContinuityPayload>(item);
        if (payload is null || string.IsNullOrWhiteSpace(payload.SeriesId) || payload.LastLegacyNumber < 0)
        {
            return Failed(Validation, "Fiscal continuity needs seriesId and a non-negative lastLegacyNumber.");
        }

        var marks = payload.Marks ?? [];
        if (marks.Any(m => m.Number <= 0 || m.Number > payload.LastLegacyNumber || string.IsNullOrWhiteSpace(m.Mark)) || marks.Select(m => m.Number).Distinct().Count() != marks.Count)
        {
            return Failed(Validation, "Every legacy mark needs a number between 1 and lastLegacyNumber (once) and a mark.");
        }

        var legalEntity = LegalEntity();
        var series = await db.FiscalSeries.SingleOrDefaultAsync(s => s.LegalEntityId == legalEntity && s.SeriesId == payload.SeriesId, cancellationToken).ConfigureAwait(false);

        // The series may only continue the legacy sequence if CMP has issued nothing in it yet (gapless, no renumbering).
        if (series is { LastNumber: > 0 })
        {
            return Failed("MIG-ERR-CONFLICT", $"Series {payload.SeriesId} already issued numbers up to {series.LastNumber}; its continuity cannot be moved.");
        }

        if (importContext.DryRun)
        {
            return new ImportOutcome(ImportStatus.Imported, null, null, $"Would continue series {payload.SeriesId} after {payload.LastLegacyNumber} with {marks.Count} marks.");
        }

        if (series is null)
        {
            db.FiscalSeries.Add(new FiscalSeriesRow { LegalEntityId = legalEntity, SeriesId = payload.SeriesId, LastNumber = payload.LastLegacyNumber });
        }
        else
        {
            series.LastNumber = payload.LastLegacyNumber;
        }

        var now = clock.Now;
        foreach (var mark in marks)
        {
            db.LegacyFiscalMarks.Add(new LegacyFiscalMarkRow
            {
                MarkId = Guid.CreateVersion7(), LegalEntityId = legalEntity, SeriesId = payload.SeriesId, LegacyNumber = mark.Number, Mark = mark.Mark.Trim(),
                BatchId = importContext.BatchId, RecordedAt = now,
            });
        }

        // Series ids are not Guids: the core id is a fresh id of the continuity record.
        return await SaveAsync(Guid.CreateVersion7(), $"Series {payload.SeriesId} continues after {payload.LastLegacyNumber}.", item.LegacyId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ImportOutcome> SaveAsync(Guid id, string message, string legacyId, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Failed("MIG-ERR-DUPLICATE", $"{legacyId} conflicts with an existing record.");
        }

        return new ImportOutcome(ImportStatus.Imported, id, null, message);
    }

    private LegalEntityId LegalEntity() =>
        legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));

    private static T? Parse<T>(MigrationImportItem item)
        where T : class
    {
        try
        {
            return item.Payload.Deserialize<T>(SharedKernelJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ImportOutcome Failed(string code, string message) => new(ImportStatus.Failed, null, code, message);
}
