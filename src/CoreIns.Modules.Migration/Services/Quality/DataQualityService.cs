using System.Text.Json;
using CoreIns.Modules.Migration.Domain;
using CoreIns.Modules.Migration.Persistence;
using CoreIns.Modules.Migration.Services.Staging;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Migration.Services.Quality;

/// <summary>mig.Profile.run body: a snapshot id, or a source and a snapshot label (the active snapshot).</summary>
internal sealed record ProfileRunBody(Guid? SnapshotId, string? Source, string? Snapshot);

/// <summary>mig.Dq.waive body.</summary>
internal sealed record DqWaiveBody(Guid? FindingId, string? Reason, DateOnly? ExpiresOn);

internal sealed record DqRunView(
    Guid RunId, Guid SnapshotId, string Source, string Snapshot, int RuleSetVersion, int RecordCount, int ErrorCount, int WarningCount,
    IReadOnlyDictionary<string, int> FindingsByRule, IReadOnlyList<DqObjectProfileView> Profile, string RunBy, DateTimeOffset RunAt);

internal sealed record DqFindingView(
    Guid FindingId, Guid RunId, Guid SnapshotId, Guid RecordId, string ObjectType, string LegacyKey, string RuleCode, string Severity, string? Field, string Message,
    string Status, Guid? WaiverId, DateOnly? WaivedUntil);

internal sealed record DqFindingListView(DqRunView Run, int Total, IReadOnlyList<DqFindingView> Items);

internal sealed record DqWaiverView(
    Guid WaiverId, Guid FindingId, string Source, string ObjectType, string LegacyKey, string RuleCode, string Reason, DateOnly ExpiresOn, string WaivedBy, DateTimeOffset WaivedAt);

/// <summary>
/// DQ rule library runs, profiling and waivers (BO-MIG-03, REQ-MIG-047..050). Staging is only read; a run inserts its own run, finding and (first time
/// for a rule-set version) rule rows. A waiver is an insert-only row by a data steward; it stops counting on its expiry date.
/// </summary>
internal sealed class DataQualityService(MigrationDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock)
{
    public const int DefaultPageSize = 200;
    public const int MaxPageSize = 1000;
    public const int MaxWaiverDays = 365;

    private LegalEntityId LegalEntity => legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));

    private DateOnly Today => DateOnly.FromDateTime(clock.Now.ToUtcDateTime());

    public async Task<Result<DqRunView>> RunAsync(ProfileRunBody body, CancellationToken cancellationToken)
    {
        var legalEntity = LegalEntity;
        StagingSnapshotRow? snapshot;
        if (body.SnapshotId is { } id)
        {
            snapshot = await db.StagingSnapshots.AsNoTracking().FirstOrDefaultAsync(s => s.SnapshotId == id && s.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var source = body.Source?.Trim().ToUpperInvariant() ?? string.Empty;
            var label = body.Snapshot?.Trim() ?? string.Empty;
            if (source.Length == 0 || label.Length == 0)
            {
                return Invalid("Give a snapshot id, or a source and a snapshot label.");
            }

            snapshot = await db.StagingSnapshots.AsNoTracking()
                .FirstOrDefaultAsync(s => s.LegalEntityId == legalEntity && s.Source == source && s.Snapshot == label && s.Status == StagingStatus.Active, cancellationToken).ConfigureAwait(false);
        }

        if (snapshot is null)
        {
            return MigrationErrors.NotFound("The staged snapshot");
        }

        // TODO(hardening): records are read into memory (staging holds at most 100,000 per load); chunk by object type if loads grow.
        var raw = await db.StagingRecords.AsNoTracking().Where(r => r.SnapshotId == snapshot.SnapshotId).OrderBy(r => r.Seq)
            .Select(r => new { r.RecordId, r.Seq, r.ObjectType, r.LegacyKey, r.Payload }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var documents = new List<JsonDocument>(raw.Count);
        try
        {
            var records = new List<DqRecord>(raw.Count);
            foreach (var r in raw)
            {
                var document = Parse(r.Payload);
                documents.Add(document);
                records.Add(new DqRecord(r.RecordId, r.Seq, r.ObjectType, r.LegacyKey, document.RootElement));
            }

            var drafts = DqRules.Evaluate(records, Today);
            var profile = DqProfiler.Profile(records);
            var now = clock.Now;
            var runId = Guid.CreateVersion7();
            var byRule = drafts.GroupBy(d => d.RuleCode, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count());
            var run = new DqRunRow
            {
                RunId = runId, LegalEntityId = legalEntity, SnapshotId = snapshot.SnapshotId, RuleSetVersion = DqRules.Version, RecordCount = records.Count,
                ErrorCount = drafts.Count(d => d.Severity == DqRules.Error), WarningCount = drafts.Count(d => d.Severity == DqRules.Warning),
                FindingsByRule = JsonSerializer.Serialize(byRule), Profile = JsonSerializer.Serialize(profile), RunBy = context.Actor.ToString(), RunAt = now,
            };
            db.DqRuns.Add(run);
            if (!await db.DqRules.AnyAsync(r => r.RuleSetVersion == DqRules.Version, cancellationToken).ConfigureAwait(false))
            {
                foreach (var rule in DqRules.Catalogue)
                {
                    db.DqRules.Add(new DqRuleRow
                    {
                        RuleId = Guid.CreateVersion7(), RuleSetVersion = DqRules.Version, Code = rule.Code, ObjectType = rule.ObjectType, Kind = rule.Kind, Severity = rule.Severity, Description = rule.Description,
                    });
                }
            }

            var seq = 0;
            foreach (var d in drafts)
            {
                var record = records[d.Index];
                db.DqFindings.Add(new DqFindingRow
                {
                    FindingId = Guid.CreateVersion7(), RunId = runId, SnapshotId = snapshot.SnapshotId, RecordId = record.RecordId, ObjectType = record.ObjectType, LegacyKey = record.LegacyKey,
                    RuleCode = d.RuleCode, Severity = d.Severity, Field = d.Field, Message = d.Message, Seq = ++seq,
                });
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException) when (db.ChangeTracker.Entries<DqRuleRow>().Any())
            {
                // Two first runs raced to record the rule set; the rule rows are the same, so a retry without them is safe.
                foreach (var entry in db.ChangeTracker.Entries<DqRuleRow>().ToList())
                {
                    entry.State = EntityState.Detached;
                }

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            return ToView(run, snapshot);
        }
        finally
        {
            foreach (var document in documents)
            {
                document.Dispose();
            }
        }
    }

    public async Task<Result<DqFindingListView>> FindingsAsync(
        Guid? runId, Guid? snapshotId, string? ruleCode, string? severity, string? objectType, bool? includeWaived, int? limit, int? offset, CancellationToken cancellationToken)
    {
        var legalEntity = LegalEntity;
        var take = limit ?? DefaultPageSize;
        var skip = offset ?? 0;
        if (take is < 1 or > MaxPageSize || skip < 0)
        {
            return Invalid($"limit is 1 to {MaxPageSize} and offset is not negative.");
        }

        if (severity is not null && severity is not (DqRules.Error or DqRules.Warning))
        {
            return Invalid("severity is ERROR or WARNING.");
        }

        DqRunRow? run;
        if (runId is { } rid)
        {
            run = await db.DqRuns.AsNoTracking().FirstOrDefaultAsync(r => r.RunId == rid && r.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        }
        else if (snapshotId is { } sid)
        {
            run = await db.DqRuns.AsNoTracking().Where(r => r.SnapshotId == sid && r.LegalEntityId == legalEntity).OrderByDescending(r => r.RunAt).ThenByDescending(r => r.RunId)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            return Invalid("Give a run id or a snapshot id.");
        }

        if (run is null)
        {
            return MigrationErrors.NotFound("The data quality run");
        }

        var snapshot = await db.StagingSnapshots.AsNoTracking().FirstAsync(s => s.SnapshotId == run.SnapshotId, cancellationToken).ConfigureAwait(false);
        var today = Today;
        var source = snapshot.Source;
        var waivers = await ActiveWaiversAsync(legalEntity, source, today, cancellationToken).ConfigureAwait(false);

        var query = db.DqFindings.AsNoTracking().Where(f => f.RunId == run.RunId);
        if (!string.IsNullOrWhiteSpace(ruleCode))
        {
            var code = ruleCode.Trim();
            query = query.Where(f => f.RuleCode == code);
        }

        if (severity is not null)
        {
            query = query.Where(f => f.Severity == severity);
        }

        if (!string.IsNullOrWhiteSpace(objectType))
        {
            var type = objectType.Trim();
            query = query.Where(f => f.ObjectType == type);
        }

        if (includeWaived == false)
        {
            query = query.Where(f => !db.DqWaivers.Any(w => w.LegalEntityId == legalEntity && w.Source == source && w.ExpiresOn >= today
                && w.RuleCode == f.RuleCode && w.ObjectType == f.ObjectType && w.LegacyKey == f.LegacyKey));
        }

        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var page = await query.OrderBy(f => f.Seq).Skip(skip).Take(take).ToListAsync(cancellationToken).ConfigureAwait(false);
        var items = page.Select(f =>
        {
            waivers.TryGetValue((f.RuleCode, f.ObjectType, f.LegacyKey), out var waiver);
            return new DqFindingView(
                f.FindingId, f.RunId, f.SnapshotId, f.RecordId, f.ObjectType, f.LegacyKey, f.RuleCode, f.Severity, f.Field, f.Message,
                waiver is null ? "OPEN" : "WAIVED", waiver?.WaiverId, waiver?.ExpiresOn);
        }).ToList();
        return new DqFindingListView(ToView(run, snapshot), total, items);
    }

    public async Task<Result<DqWaiverView>> WaiveAsync(DqWaiveBody body, CancellationToken cancellationToken)
    {
        var reason = body.Reason?.Trim() ?? string.Empty;
        if (body.FindingId is null || reason.Length is 0 or > 500 || body.ExpiresOn is null)
        {
            return Invalid("Give a finding, a reason of up to 500 characters and an expiry date.");
        }

        var today = Today;
        var expires = body.ExpiresOn.Value;
        if (expires <= today || expires > today.AddDays(MaxWaiverDays))
        {
            return Invalid($"The expiry date is after today and at most {MaxWaiverDays} days ahead.");
        }

        var legalEntity = LegalEntity;
        var findingId = body.FindingId.Value;
        var finding = await db.DqFindings.AsNoTracking().FirstOrDefaultAsync(f => f.FindingId == findingId, cancellationToken).ConfigureAwait(false);
        var run = finding is null ? null : await db.DqRuns.AsNoTracking().FirstOrDefaultAsync(r => r.RunId == finding.RunId && r.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (finding is null || run is null)
        {
            return MigrationErrors.NotFound("The finding");
        }

        var actor = context.Actor.ToString();
        if (run.RunBy == actor)
        {
            return DomainError.Of(ModuleCode.MIG, "SIGNOFF-SOD", "The person who ran the data quality check cannot waive its findings; a data steward decides.");
        }

        var snapshot = await db.StagingSnapshots.AsNoTracking().FirstAsync(s => s.SnapshotId == run.SnapshotId, cancellationToken).ConfigureAwait(false);
        var active = await ActiveWaiversAsync(legalEntity, snapshot.Source, today, cancellationToken).ConfigureAwait(false);
        if (active.ContainsKey((finding.RuleCode, finding.ObjectType, finding.LegacyKey)))
        {
            return MigrationErrors.State("The finding is already waived; wait for the waiver to expire before waiving again.");
        }

        var row = new DqWaiverRow
        {
            WaiverId = Guid.CreateVersion7(), LegalEntityId = legalEntity, Source = snapshot.Source, ObjectType = finding.ObjectType, LegacyKey = finding.LegacyKey, RuleCode = finding.RuleCode,
            FindingId = finding.FindingId, Reason = reason, ExpiresOn = expires, WaivedBy = actor, WaivedAt = clock.Now,
        };
        db.DqWaivers.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new DqWaiverView(row.WaiverId, row.FindingId, row.Source, row.ObjectType, row.LegacyKey, row.RuleCode, row.Reason, row.ExpiresOn, row.WaivedBy, row.WaivedAt.ToDateTimeOffset());
    }

    // TODO(hardening): the active waivers of a source are loaded whole; page or join in SQL if a source accumulates many thousands.
    private async Task<Dictionary<(string Rule, string Type, string Key), DqWaiverRow>> ActiveWaiversAsync(LegalEntityId legalEntity, string source, DateOnly today, CancellationToken cancellationToken)
    {
        var rows = await db.DqWaivers.AsNoTracking().Where(w => w.LegalEntityId == legalEntity && w.Source == source && w.ExpiresOn >= today).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.GroupBy(w => (w.RuleCode, w.ObjectType, w.LegacyKey)).ToDictionary(g => g.Key, g => g.OrderByDescending(w => w.ExpiresOn).First());
    }

    private static JsonDocument Parse(string payload)
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

    private static DqRunView ToView(DqRunRow run, StagingSnapshotRow snapshot) => new(
        run.RunId, run.SnapshotId, snapshot.Source, snapshot.Snapshot, run.RuleSetVersion, run.RecordCount, run.ErrorCount, run.WarningCount,
        JsonSerializer.Deserialize<Dictionary<string, int>>(run.FindingsByRule) ?? [], JsonSerializer.Deserialize<List<DqObjectProfileView>>(run.Profile) ?? [], run.RunBy, run.RunAt.ToDateTimeOffset());

    private static DomainError Invalid(string detail) => DomainError.Of(ModuleCode.MIG, MigrationErrors.Validation, detail);
}
