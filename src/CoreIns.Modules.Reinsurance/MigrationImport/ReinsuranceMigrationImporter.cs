using System.Text.Json;
using CoreIns.Modules.Migration.Contracts.Spi;
using CoreIns.Modules.Reinsurance.Cession;
using CoreIns.SharedKernel.Json;

namespace CoreIns.Modules.Reinsurance.MigrationImport;

/// <summary>A running treaty year with the aggregate used up before the migration (an XoL aggregate that carries over).</summary>
internal sealed record TreatyYearPayload(string TreatyId, int ContractYear, decimal OpeningAggregate);

/// <summary>
/// <c>IMigrationImporter</c> for module RI (SL6-MIG-CLM-RI-IMPORT): object type <c>TreatyYear</c> opens the tracker of a treaty year
/// through <c>ri.Tracker.openingAggregate</c> (idempotent on treaty and year). A repeat with another amount is reported as a
/// conflict. TODO(hardening): no RI reverser yet; a batch holding RI objects cannot be reversed (D-SL6-05 default).
/// </summary>
internal sealed class ReinsuranceMigrationImporter(TrackerService tracker) : IMigrationImporter
{
    public const string ObjectType = "TreatyYear";

    public string ModuleName => "RI";

    public async Task<ImportOutcome> ImportAsync(MigrationImportItem item, MigrationImportContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(context);
        if (!string.Equals(item.ObjectType, ObjectType, StringComparison.Ordinal))
        {
            return Failed("MIG-ERR-VALIDATION", $"RI imports object type TreatyYear, not '{item.ObjectType}'.");
        }

        TreatyYearPayload? payload;
        try
        {
            payload = item.Payload.Deserialize<TreatyYearPayload>(SharedKernelJson.Options);
        }
        catch (JsonException ex)
        {
            return Failed("MIG-ERR-VALIDATION", "The treaty-year payload is not valid: " + ex.Message);
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.TreatyId) || payload.ContractYear is < 1990 or > 2200 || payload.OpeningAggregate < 0m)
        {
            return Failed("MIG-ERR-VALIDATION", "A treaty year needs treatyId, a contractYear and a non-negative openingAggregate.");
        }

        if (context.DryRun)
        {
            return new ImportOutcome(ImportStatus.Imported, null, null, $"Would open {payload.TreatyId}/{payload.ContractYear} at {payload.OpeningAggregate}.");
        }

        var row = await tracker.OpeningAggregateAsync(payload.TreatyId.Trim(), payload.ContractYear, payload.OpeningAggregate, cancellationToken).ConfigureAwait(false);
        return row.OpeningAggregate != payload.OpeningAggregate
            ? Failed("MIG-ERR-CONFLICT", $"{payload.TreatyId}/{payload.ContractYear} already has an opening aggregate of {row.OpeningAggregate}.")
            : new ImportOutcome(ImportStatus.Imported, row.TrackerId, null, $"Treaty year {payload.TreatyId}/{payload.ContractYear} opens at {row.OpeningAggregate}.");
    }

    private static ImportOutcome Failed(string code, string message) => new(ImportStatus.Failed, null, code, message);
}
