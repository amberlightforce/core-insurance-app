using System.Text.Json;
using CoreIns.Modules.Finance.Domain;
using CoreIns.Modules.Finance.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Finance.Posting;

/// <summary>
/// Recognises a correction entry (REQ-POL-108): a BIL entry whose lines carry the <c>correctionOf</c> dimension, or whose charges were
/// announced by a POL <c>ChargeDeltaEmitted</c> that carries <c>correctionOf</c> (the context FIN keeps for every delta).
/// </summary>
internal sealed class OosCorrections(FinanceDbContext db)
{
    public const string ChargeDeltaEmitted = "pol.ChargeDeltaEmitted";

    public async Task<JournalCorrection?> FindAsync(SourceEntry entry, CancellationToken cancellationToken)
    {
        foreach (var line in entry.Lines)
        {
            if (line.Id("correctionOf") is { } direct)
            {
                return new JournalCorrection(direct, line.Id("oosSetId"));
            }
        }

        var charges = entry.Lines.Select(l => l.Id(LineDimensionKeys.ChargeId)).Where(id => id is not null).Select(id => id!.Value).ToHashSet();
        var policies = entry.Lines.Select(l => l.Id(LineDimensionKeys.PolicyId)).Where(id => id is not null).Select(id => id!.Value).Distinct().ToList();
        if (charges.Count == 0 || policies.Count == 0)
        {
            return null;
        }

        var deltas = await db.BusinessEvents.AsNoTracking()
            .Where(b => b.RegistryName == ChargeDeltaEmitted && b.PolicyId != null && policies.Contains(b.PolicyId.Value))
            .Select(b => b.Payload).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var payload in deltas)
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.TryGetProperty("chargeId", out var chargeId) && Guid.TryParse(chargeId.GetString(), out var id) && charges.Contains(id)
                && root.TryGetProperty("correctionOf", out var correctionOf) && correctionOf.ValueKind == JsonValueKind.String && Guid.TryParse(correctionOf.GetString(), out var corrected))
            {
                Guid? set = root.TryGetProperty("oosSetId", out var setId) && setId.ValueKind == JsonValueKind.String && Guid.TryParse(setId.GetString(), out var parsed) ? parsed : null;
                return new JournalCorrection(corrected, set);
            }
        }

        return null;
    }
}
