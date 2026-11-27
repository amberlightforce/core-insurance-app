using System.Globalization;
using System.Text.Json;
using CoreIns.Modules.Migration.Commands;
using CoreIns.Modules.Migration.Domain;
using CoreIns.Modules.Migration.Persistence;
using CoreIns.Modules.Migration.Services.Mapping;
using CoreIns.Modules.Migration.Services.Quality;
using CoreIns.Modules.Migration.Services.SignOff;
using CoreIns.Modules.Migration.Services.Staging;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Migration.Services.Waves;

internal sealed record WavePlanBody(string? Name, string? Source, string? Snapshot, string? ObjectType, string? TermEndField, string? WindowFrom, string? WindowTo, string? Scope);

internal sealed record WaveConvertBody(Guid? WaveId, Guid? SpecId, string? BatchName);

internal sealed record WaveAdjustBody(Guid? WaveId, string? LegacyKey, string? Action, string? Reason);

internal sealed record WaveCloseBody(Guid? WaveId);

internal sealed record WaveTermView(string LegacyKey, string TermEnd, string State, string? Reason, Guid? BatchId, Guid? CoreId);

internal sealed record WaveBatchView(Guid BatchId, Guid? SpecId, string Status, int ItemCount);

internal sealed record WaveView(
    Guid WaveId, string Name, string Status, string Source, string Snapshot, string ObjectType, string TermEndField, string WindowFrom, string WindowTo, string Scope,
    int SelectedCount, IReadOnlyDictionary<string, int> Counts, bool EquationHolds, IReadOnlyList<WaveBatchView> Batches, SignOffStatusView? Signoff,
    IReadOnlyList<WaveTermView> Terms, string CreatedBy, string? ClosedBy, DateTimeOffset? ClosedAt, int? Added, int? Held);

/// <summary>
/// Renewal-based conversion waves (BO-MIG-07, D-CON-07). A wave selects the policies of a staged snapshot whose legacy term ends in a renewal date window,
/// converts them through an approved mapping and a normal maker-checker batch, and closes when the count equation holds and the wave sign-off is ready.
/// Idempotent and resumable: planning again adds only new terms, a policy is claimed by one term at a time across all waves (database index), and a
/// term left over stays PLANNED until the wave closes it NOT_CONVERTED for a later wave.
/// </summary>
internal sealed class WaveService(
    MigrationDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock, MappingService mappings, SignOffService signOff, IServiceProvider services)
{
    public const int MaxRecords = 100_000;
    public const int DefaultTermPage = 200;
    public const int MaxTermPage = 1000;
    private const string DefaultObjectType = "Policy";
    private const string DefaultTermEndField = "endDate";

    private LegalEntityId LegalEntity => legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));

    private static DomainError Invalid(string detail) => DomainError.Of(ModuleCode.MIG, MigrationErrors.Validation, detail);

    private static bool TryDate(string? text, out DateOnly date) => DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static string Format(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public async Task<Result<WaveView>> PlanAsync(WavePlanBody body, CancellationToken cancellationToken)
    {
        var name = body.Name?.Trim() ?? string.Empty;
        var source = body.Source?.Trim().ToUpperInvariant() ?? string.Empty;
        var label = body.Snapshot?.Trim() ?? string.Empty;
        var objectType = string.IsNullOrWhiteSpace(body.ObjectType) ? DefaultObjectType : body.ObjectType.Trim();
        var field = string.IsNullOrWhiteSpace(body.TermEndField) ? DefaultTermEndField : body.TermEndField.Trim();
        var scope = string.IsNullOrWhiteSpace(body.Scope) ? name : body.Scope.Trim();
        if (!MappingEngine.ValidName(name) || source.Length == 0 || label.Length == 0 || objectType.Length > 64 || field.Length is 0 or > 64 || scope.Length is 0 or > 64)
        {
            return Invalid("Give a wave name (letters, digits, dot, underscore, hyphen), the staged source and snapshot, and a scope of at most 64 characters.");
        }

        if (!TryDate(body.WindowFrom, out var from) || !TryDate(body.WindowTo, out var to) || to < from || to.DayNumber - from.DayNumber > 3660)
        {
            return Invalid("Give the renewal window as windowFrom and windowTo (yyyy-MM-dd, from not after to, at most ten years).");
        }

        var legalEntity = LegalEntity;
        var snapshot = await db.StagingSnapshots.AsNoTracking()
            .FirstOrDefaultAsync(s => s.LegalEntityId == legalEntity && s.Source == source && s.Snapshot == label && s.Status == StagingStatus.Active, cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return MigrationErrors.NotFound("The staged snapshot");
        }

        var wave = await db.Waves.FirstOrDefaultAsync(w => w.LegalEntityId == legalEntity && w.Name == name, cancellationToken).ConfigureAwait(false);
        if (wave is null)
        {
            wave = new WaveRow
            {
                WaveId = Guid.CreateVersion7(), LegalEntityId = legalEntity, Name = name, Status = WaveStatus.Planned, SnapshotId = snapshot.SnapshotId, Source = source, Snapshot = label,
                ObjectType = objectType, TermEndField = field, WindowFrom = from, WindowTo = to, Scope = scope, CreatedBy = context.Actor.ToString(), CreatedAt = clock.Now, RecordVersion = 1,
            };
            db.Waves.Add(wave);
        }
        else if (wave.Status == WaveStatus.Closed)
        {
            return MigrationErrors.State($"Wave {name} is closed; plan a new wave.");
        }
        else if (wave.SnapshotId != snapshot.SnapshotId || wave.ObjectType != objectType || wave.TermEndField != field || wave.WindowFrom != from || wave.WindowTo != to)
        {
            return MigrationErrors.State($"Wave {name} already exists with a different snapshot or window; plan under a new name.");
        }

        // Policies already claimed by a term (planned, offered or converted in any wave) are left alone; so are terms already in this wave in any state.
        var claimed = (await db.WaveTerms.AsNoTracking().Where(t => t.LegalEntityId == legalEntity && t.ObjectType == objectType && TermState.Claiming.Contains(t.State))
            .Select(t => t.LegacyKey).ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
        var inWave = (await db.WaveTerms.AsNoTracking().Where(t => t.WaveId == wave.WaveId).Select(t => t.LegacyKey).ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
        var raw = await db.StagingRecords.AsNoTracking().Where(r => r.SnapshotId == snapshot.SnapshotId && r.ObjectType == objectType).OrderBy(r => r.Seq)
            .Select(r => new { r.LegacyKey, r.Payload }).Take(MaxRecords).ToListAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.Now;
        var added = 0;
        foreach (var record in raw)
        {
            if (claimed.Contains(record.LegacyKey) || inWave.Contains(record.LegacyKey) || !TryTermEnd(record.Payload, field, out var termEnd) || termEnd < from || termEnd > to)
            {
                continue;
            }

            db.WaveTerms.Add(new WaveTermRow
            {
                TermId = Guid.CreateVersion7(), WaveId = wave.WaveId, LegalEntityId = legalEntity, ObjectType = objectType, LegacyKey = record.LegacyKey, TermEnd = termEnd,
                State = TermState.Planned, UpdatedAt = now,
            });
            inWave.Add(record.LegacyKey);
            added++;
        }

        // TODO(hardening): two waves planned at the same moment can pick the same policy; the partial unique index refuses the second save (surfaces as a 500, nothing is stored). Retry with the claimed set refreshed.
        wave.SelectedCount += added;
        wave.RecordVersion++;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await ViewAsync(wave, null, DefaultTermPage, added, null, cancellationToken).ConfigureAwait(false);
    }

    private static bool TryTermEnd(string payload, string field, out DateOnly termEnd)
    {
        termEnd = default;
        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty(field, out var value)
                && value.ValueKind == JsonValueKind.String && TryDate(value.GetString(), out termEnd);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public async Task<Result<WaveView>> GetAsync(Guid id, string? state, int? limit, CancellationToken cancellationToken)
    {
        if (state is not null && !TermState.All.Contains(state))
        {
            return Invalid($"state is one of {string.Join(", ", TermState.All)}.");
        }

        if (limit is < 1 or > MaxTermPage)
        {
            return Invalid($"limit is 1 to {MaxTermPage}.");
        }

        var wave = await LoadWaveAsync(id, track: false, cancellationToken).ConfigureAwait(false);
        return wave is null ? MigrationErrors.NotFound("The wave") : await ViewAsync(wave, state, limit ?? DefaultTermPage, null, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<WaveView>> ConvertAsync(WaveConvertBody body, CancellationToken cancellationToken)
    {
        if (body.WaveId is not { } waveId || body.SpecId is not { } specId || body.BatchName?.Length > 200)
        {
            return Invalid("Give a wave id and a mapping spec id.");
        }

        var wave = await LoadWaveAsync(waveId, track: true, cancellationToken).ConfigureAwait(false);
        if (wave is null)
        {
            return MigrationErrors.NotFound("The wave");
        }

        if (wave.Status == WaveStatus.Closed)
        {
            return MigrationErrors.State($"Wave {wave.Name} is closed.");
        }

        // Only an APPROVED spec whose content still hashes to what was approved converts a wave; the batch records its hash and is checked again before the run.
        var approved = await mappings.LoadApprovedAsync(specId, null, cancellationToken).ConfigureAwait(false);
        if (!approved.IsSuccess)
        {
            return approved.Error;
        }

        var mapping = approved.Value;
        if (mapping.Spec.SourceObjectType != wave.ObjectType)
        {
            return Invalid($"The mapping spec maps {mapping.Spec.SourceObjectType}; the wave converts {wave.ObjectType}.");
        }

        var legalEntity = LegalEntity;
        var terms = await db.WaveTerms.Where(t => t.WaveId == wave.WaveId && t.State == TermState.Planned && t.BatchId == null).OrderBy(t => t.LegacyKey)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var openErrors = await OpenDqErrorKeysAsync(wave, legalEntity, cancellationToken).ConfigureAwait(false);
        var staged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var chunk in terms.Select(t => t.LegacyKey).Chunk(1000))
        {
            var keys = chunk;
            var rows = await db.StagingRecords.AsNoTracking().Where(r => r.SnapshotId == wave.SnapshotId && r.ObjectType == wave.ObjectType && keys.Contains(r.LegacyKey))
                .OrderBy(r => r.Seq).Select(r => new { r.LegacyKey, r.Payload }).ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var row in rows)
            {
                staged.TryAdd(row.LegacyKey, row.Payload);
            }
        }

        var now = clock.Now;
        var items = new List<(string Module, string ObjectType, string LegacyId, string Payload)>();
        var offered = new List<WaveTermRow>();
        var held = 0;
        foreach (var term in terms)
        {
            string? hold = null;
            if (openErrors.Contains(term.LegacyKey))
            {
                hold = "DQ: the record has open data quality errors.";
            }
            else if (!staged.TryGetValue(term.LegacyKey, out var payload))
            {
                hold = "STAGING: the record is no longer in the snapshot.";
            }
            else
            {
                using var document = ParseOrEmpty(payload);
                var result = MappingEngine.Apply(new DqRecord(Guid.Empty, 0, wave.ObjectType, term.LegacyKey, document.RootElement), mapping.Spec.TargetObjectType, mapping.Fields, mapping.Lookup);
                if (result.Issues.Count > 0)
                {
                    hold = "MAPPING: " + string.Join("; ", result.Issues.Select(i => $"{i.Code} {i.Field}"));
                }
                else
                {
                    items.Add((mapping.Spec.TargetModule, mapping.Spec.TargetObjectType, term.LegacyKey, result.Payload.ToJsonString()));
                    offered.Add(term);
                }
            }

            if (hold is not null)
            {
                term.Reason = hold.Length <= 500 ? hold : hold[..500];
                term.UpdatedAt = now;
                held++;
            }
        }

        if (items.Count > 0)
        {
            var batch = await BatchBuilder.AddAsync(
                db, services, legalEntity, string.IsNullOrWhiteSpace(body.BatchName) ? $"{wave.Name}-{mapping.Spec.Name}-v{mapping.Spec.Version}" : body.BatchName.Trim(),
                items, now, mapping.Spec.SpecId, mapping.Spec.ContentHash, cancellationToken).ConfigureAwait(false);
            foreach (var term in offered)
            {
                term.State = TermState.Offered;
                term.BatchId = batch.BatchId;
                term.Reason = null;
                term.UpdatedAt = now;
            }

            wave.Status = WaveStatus.InProgress;
            wave.RecordVersion++;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await ViewAsync(wave, null, DefaultTermPage, null, held, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<WaveView>> AdjustAsync(WaveAdjustBody body, CancellationToken cancellationToken)
    {
        var action = body.Action?.Trim().ToUpperInvariant();
        var reason = body.Reason?.Trim() ?? string.Empty;
        if (body.WaveId is not { } waveId || string.IsNullOrWhiteSpace(body.LegacyKey) || action is not ("EXCLUDE" or "WITHDRAW") || reason.Length is 0 or > 500)
        {
            return Invalid("Give a wave id, the policy legacy key, an action of EXCLUDE or WITHDRAW and a reason of at most 500 characters.");
        }

        var wave = await LoadWaveAsync(waveId, track: true, cancellationToken).ConfigureAwait(false);
        if (wave is null)
        {
            return MigrationErrors.NotFound("The wave");
        }

        if (wave.Status == WaveStatus.Closed)
        {
            return MigrationErrors.State($"Wave {wave.Name} is closed.");
        }

        var key = body.LegacyKey.Trim();
        var term = await db.WaveTerms.FirstOrDefaultAsync(t => t.WaveId == wave.WaveId && t.LegacyKey == key, cancellationToken).ConfigureAwait(false);
        if (term is null)
        {
            return MigrationErrors.NotFound("The wave term");
        }

        var allowed = action == "EXCLUDE" ? term.State == TermState.Planned : term.State is TermState.Planned or TermState.Offered;
        if (!allowed)
        {
            return MigrationErrors.State($"A term in state {term.State} cannot be {(action == "EXCLUDE" ? "excluded" : "withdrawn")}.");
        }

        // The policy keeps its legacy routing (no route is written); a withdrawn offered term is refused when its batch runs.
        term.State = action == "EXCLUDE" ? TermState.Excluded : TermState.Withdrawn;
        term.Reason = reason;
        term.UpdatedAt = clock.Now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await ViewAsync(wave, null, DefaultTermPage, null, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<WaveView>> CloseAsync(WaveCloseBody body, CancellationToken cancellationToken)
    {
        if (body.WaveId is not { } waveId)
        {
            return Invalid("Give a wave id.");
        }

        var wave = await LoadWaveAsync(waveId, track: true, cancellationToken).ConfigureAwait(false);
        if (wave is null)
        {
            return MigrationErrors.NotFound("The wave");
        }

        if (wave.Status == WaveStatus.Closed)
        {
            return await ViewAsync(wave, null, DefaultTermPage, null, null, cancellationToken).ConfigureAwait(false);
        }

        var counts = await CountsAsync(wave.WaveId, cancellationToken).ConfigureAwait(false);
        var blockers = new List<string>();
        if (counts.Values.Sum() != wave.SelectedCount)
        {
            blockers.Add($"COUNT_EQUATION: {wave.SelectedCount} selected, {counts.Values.Sum()} in a state");
        }

        if (counts.GetValueOrDefault(TermState.Offered) > 0)
        {
            blockers.Add($"TERMS_OFFERED: {counts[TermState.Offered]} term(s) are offered and not yet converted, withdrawn or failed");
        }

        var unfinished = await UnfinishedBatchesAsync(wave.WaveId, cancellationToken).ConfigureAwait(false);
        blockers.AddRange(unfinished.Select(b => $"BATCH_NOT_FINISHED: {b}"));
        var status = await signOff.StatusAsync(wave.Scope, cancellationToken).ConfigureAwait(false);
        if (!status.IsSuccess)
        {
            return status.Error;
        }

        blockers.AddRange(status.Value.Blockers.Select(b => $"SIGNOFF: {b}"));
        if (!status.Value.Ready && status.Value.Blockers.Count == 0)
        {
            blockers.Add("SIGNOFF: not ready");
        }

        if (blockers.Count > 0)
        {
            return MigrationErrors.State($"Wave {wave.Name} cannot close: {string.Join("; ", blockers)}.");
        }

        // What was not converted stays with the legacy system and is selectable by a later wave.
        var now = clock.Now;
        foreach (var term in await db.WaveTerms.Where(t => t.WaveId == wave.WaveId && t.State == TermState.Planned).ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            term.State = TermState.NotConverted;
            term.Reason ??= "Not converted in this wave.";
            term.UpdatedAt = now;
        }

        wave.Status = WaveStatus.Closed;
        wave.ClosedBy = context.Actor.ToString();
        wave.ClosedAt = now;
        wave.RecordVersion++;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await ViewAsync(wave, null, DefaultTermPage, null, null, cancellationToken).ConfigureAwait(false);
    }

    private async Task<WaveRow?> LoadWaveAsync(Guid id, bool track, CancellationToken cancellationToken)
    {
        var legalEntity = LegalEntity;
        var query = track ? db.Waves.AsQueryable() : db.Waves.AsNoTracking();
        return await query.FirstOrDefaultAsync(w => w.WaveId == id && w.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Dictionary<string, int>> CountsAsync(Guid waveId, CancellationToken cancellationToken)
    {
        var rows = await db.WaveTerms.AsNoTracking().Where(t => t.WaveId == waveId).GroupBy(t => t.State).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.ToDictionary(r => r.Key, r => r.Count, StringComparer.Ordinal);
    }

    private async Task<List<string>> UnfinishedBatchesAsync(Guid waveId, CancellationToken cancellationToken)
    {
        var ids = db.WaveTerms.AsNoTracking().Where(t => t.WaveId == waveId && t.BatchId != null).Select(t => t.BatchId);
        var open = await db.Batches.AsNoTracking()
            .Where(b => ids.Contains(b.BatchId) && b.Status != BatchStatus.Completed && b.Status != BatchStatus.CompletedWithErrors && b.Status != BatchStatus.Rejected && b.Status != BatchStatus.Reversed)
            .Select(b => b.Name + " (" + b.Status + ")").ToListAsync(cancellationToken).ConfigureAwait(false);
        return open;
    }

    /// <summary>Legacy keys with an open (not waived) ERROR finding in the latest data quality run of the wave snapshot.</summary>
    private async Task<HashSet<string>> OpenDqErrorKeysAsync(WaveRow wave, LegalEntityId legalEntity, CancellationToken cancellationToken)
    {
        var run = await db.DqRuns.AsNoTracking().Where(r => r.SnapshotId == wave.SnapshotId && r.LegalEntityId == legalEntity)
            .OrderByDescending(r => r.RunAt).ThenByDescending(r => r.RunId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return [];
        }

        var today = DateOnly.FromDateTime(clock.Now.ToUtcDateTime());
        var source = wave.Source;
        var objectType = wave.ObjectType;
        var keys = await db.DqFindings.AsNoTracking().Where(
            f => f.RunId == run.RunId && f.Severity == DqRules.Error && f.ObjectType == objectType
                && !db.DqWaivers.Any(w => w.LegalEntityId == legalEntity && w.Source == source && w.ExpiresOn >= today && w.RuleCode == f.RuleCode && w.ObjectType == f.ObjectType && w.LegacyKey == f.LegacyKey))
            .Select(f => f.LegacyKey).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
        return keys.ToHashSet(StringComparer.Ordinal);
    }

    private static JsonDocument ParseOrEmpty(string payload)
    {
        try
        {
            return JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            return JsonDocument.Parse("{}");
        }
    }

    private async Task<WaveView> ViewAsync(WaveRow wave, string? state, int limit, int? added, int? held, CancellationToken cancellationToken)
    {
        var counts = await CountsAsync(wave.WaveId, cancellationToken).ConfigureAwait(false);
        var query = db.WaveTerms.AsNoTracking().Where(t => t.WaveId == wave.WaveId);
        if (state is not null)
        {
            query = query.Where(t => t.State == state);
        }

        var terms = await query.OrderBy(t => t.TermEnd).ThenBy(t => t.LegacyKey).Take(limit).ToListAsync(cancellationToken).ConfigureAwait(false);
        var batchIds = await db.WaveTerms.AsNoTracking().Where(t => t.WaveId == wave.WaveId && t.BatchId != null).Select(t => t.BatchId!.Value).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
        var batches = await db.Batches.AsNoTracking().Where(b => batchIds.Contains(b.BatchId)).OrderBy(b => b.CreatedAt).ToListAsync(cancellationToken).ConfigureAwait(false);
        var itemCounts = (await db.Items.AsNoTracking().Where(i => batchIds.Contains(i.BatchId)).GroupBy(i => i.BatchId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(g => g.Key, g => g.Count);
        var status = await signOff.StatusAsync(wave.Scope, cancellationToken).ConfigureAwait(false);
        var full = TermState.All.ToDictionary(s => s, s => counts.GetValueOrDefault(s), StringComparer.Ordinal);
        return new WaveView(
            wave.WaveId, wave.Name, wave.Status, wave.Source, wave.Snapshot, wave.ObjectType, wave.TermEndField, Format(wave.WindowFrom), Format(wave.WindowTo), wave.Scope, wave.SelectedCount,
            full, full.Values.Sum() == wave.SelectedCount, [.. batches.Select(b => new WaveBatchView(b.BatchId, b.MappingSpecId, b.Status, itemCounts.GetValueOrDefault(b.BatchId)))],
            status.IsSuccess ? status.Value : null, [.. terms.Select(t => new WaveTermView(t.LegacyKey, Format(t.TermEnd), t.State, t.Reason, t.BatchId, t.CoreId))],
            wave.CreatedBy, wave.ClosedBy, wave.ClosedAt?.ToDateTimeOffset(), added, held);
    }
}
