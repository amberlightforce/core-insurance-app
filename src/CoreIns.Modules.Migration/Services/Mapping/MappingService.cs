using System.Text.Json.Nodes;
using CoreIns.Modules.Migration.Domain;
using CoreIns.Modules.Migration.Persistence;
using CoreIns.Modules.Migration.Services.Quality;
using CoreIns.Modules.Migration.Services.Staging;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Contracts.Api;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Migration.Services.Mapping;

internal sealed record MappingCreateBody(
    string? Name, string? SourceObjectType, string? TargetModule, string? TargetObjectType, string? Description, IReadOnlyList<MappingField>? Fields, IReadOnlyList<MappingCodeMapDef>? CodeMaps);

internal sealed record MappingPublishBody(Guid? SpecId, bool? Reject, string? Comment);

internal sealed record MappingPreviewBody(Guid? SpecId, Guid? SnapshotId, string? Source, string? Snapshot, int? SampleSize, bool? RequireCleanDq);

internal sealed record MappingSpecView(
    Guid SpecId, string Name, int Version, string Status, string SourceObjectType, string TargetModule, string TargetObjectType, string? Description,
    IReadOnlyList<MappingField> Fields, IReadOnlyList<MappingCodeMapDef> CodeMaps, string ContentHash, string CreatedBy, DateTimeOffset CreatedAt,
    string? RequestedBy, Guid? ApprovalRequestId, string? DecidedBy, DateTimeOffset? DecidedAt, string? DecisionComment);

/// <summary>An approved spec with its fields and code tables, ready to map records.</summary>
internal sealed record ApprovedMapping(MappingSpecRow Spec, List<MappingField> Fields, Dictionary<string, Dictionary<string, string>> Lookup);

internal sealed record MappingSpecListView(IReadOnlyList<MappingSpecView> Items);

internal sealed record MappingUnmappedCodeView(string CodeMap, string Value, int Count);

internal sealed record MappingPreviewView(
    Guid SpecId, int SpecVersion, string SpecStatus, Guid SnapshotId, Guid? DqRunId, int Sampled, int Clean, int WithIssues,
    IReadOnlyList<MappingItemView> Items, IReadOnlyList<MappingUnmappedCodeView> UnmappedCodes);

internal sealed record MappingItemView(string LegacyKey, string ObjectType, string LegacyId, string ItemHash, JsonNode Payload, IReadOnlyList<MappingIssueView> Issues);

/// <summary>The approval type of a mapping spec: the maker puts a spec forward, a different checker approves it.</summary>
internal static class MappingApproval
{
    public const string Type = "MIG.MAPPING_SPEC";
    public const string ScopeDimension = "scope";
    public const string Scope = "MAPPING";
    public const string ReferralRole = "Staff.DataSteward";

    public static ObjectRef Subject(Guid specId) => new(ModuleCode.MIG, "MappingSpec", specId.ToString("D"));
}

/// <summary>
/// Mapping specs and code tables (BO-MIG-04, REQ-MIG-054..062). A spec version maps one staged object type to one importer payload shape; its code tables
/// are versioned with it. A new version is a new row; once put forward for approval a version is frozen (database trigger as well). The preview only reads staging.
/// </summary>
internal sealed class MappingService(MigrationDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock, IServiceProvider services)
{
    public const int DefaultSample = 25;
    public const int MaxSample = 500;
    public const int MaxList = 200;

    private LegalEntityId LegalEntity => legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));

    private static DomainError Invalid(string detail) => DomainError.Of(ModuleCode.MIG, MigrationErrors.Validation, detail);

    public async Task<Result<MappingSpecView>> CreateAsync(MappingCreateBody body, CancellationToken cancellationToken)
    {
        var name = body.Name?.Trim() ?? string.Empty;
        var source = body.SourceObjectType?.Trim() ?? string.Empty;
        var target = body.TargetObjectType?.Trim() ?? string.Empty;
        var module = body.TargetModule?.Trim().ToUpperInvariant() ?? string.Empty;
        if (!MappingEngine.ValidName(name) || source.Length is 0 or > 64 || target.Length is 0 or > 64 || !MappingEngine.ValidModule(module) || body.Description?.Length > 500)
        {
            return Invalid("Give a name (letters, digits, dot, underscore, hyphen), the source and target object types, and a three-letter target module.");
        }

        var problem = MappingEngine.Normalise(body.Fields, body.CodeMaps, out var fields, out var maps);
        if (problem is not null)
        {
            return Invalid(problem);
        }

        var legalEntity = LegalEntity;
        var last = await db.MappingSpecs.AsNoTracking().Where(s => s.LegalEntityId == legalEntity && s.Name == name).MaxAsync(s => (int?)s.Version, cancellationToken).ConfigureAwait(false);
        var row = new MappingSpecRow
        {
            SpecId = Guid.CreateVersion7(), LegalEntityId = legalEntity, Name = name, Version = (last ?? 0) + 1, SourceObjectType = source, TargetModule = module, TargetObjectType = target,
            Description = string.IsNullOrWhiteSpace(body.Description) ? null : body.Description.Trim(), Fields = MappingEngine.Serialize(fields),
            ContentHash = MappingEngine.ContentHash(name, source, module, target, fields, maps), Status = MappingStatus.Draft, CreatedBy = context.Actor.ToString(), CreatedAt = clock.Now,
        };
        db.MappingSpecs.Add(row);
        foreach (var map in maps)
        {
            foreach (var entry in map.Entries)
            {
                db.CodeMaps.Add(new CodeMapRow { EntryId = Guid.CreateVersion7(), SpecId = row.SpecId, MapName = map.Name, LegacyValue = entry.Legacy, CoreValue = entry.Core });
            }
        }

        // TODO(hardening): two creates of the same name racing for the next version hit the unique index and surface as a 500; retry with the next number.
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToView(row, fields, maps);
    }

    public async Task<Result<MappingSpecView>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var legalEntity = LegalEntity;
        var row = await db.MappingSpecs.AsNoTracking().FirstOrDefaultAsync(s => s.SpecId == id && s.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return MigrationErrors.NotFound("The mapping spec");
        }

        return ToView(row, MappingEngine.Deserialize<List<MappingField>>(row.Fields) ?? [], await LoadMapsAsync(row.SpecId, cancellationToken).ConfigureAwait(false));
    }

    public async Task<Result<MappingSpecListView>> ListAsync(string? name, string? status, CancellationToken cancellationToken)
    {
        if (status is not null && status is not (MappingStatus.Draft or MappingStatus.PendingApproval or MappingStatus.Approved or MappingStatus.Rejected))
        {
            return Invalid("status is DRAFT, PENDING_APPROVAL, APPROVED or REJECTED.");
        }

        var legalEntity = LegalEntity;
        var query = db.MappingSpecs.AsNoTracking().Where(s => s.LegalEntityId == legalEntity);
        if (!string.IsNullOrWhiteSpace(name))
        {
            var n = name.Trim();
            query = query.Where(s => s.Name == n);
        }

        if (status is not null)
        {
            query = query.Where(s => s.Status == status);
        }

        var rows = await query.OrderByDescending(s => s.CreatedAt).ThenByDescending(s => s.SpecId).Take(MaxList).ToListAsync(cancellationToken).ConfigureAwait(false);

        // The list carries the fields but not the code tables (read one spec for those).
        return new MappingSpecListView([.. rows.Select(r => ToView(r, MappingEngine.Deserialize<List<MappingField>>(r.Fields) ?? [], []))]);
    }

    public async Task<Result<MappingSpecView>> PublishAsync(MappingPublishBody body, CancellationToken cancellationToken)
    {
        if (body.SpecId is not { } id)
        {
            return Invalid("Give a spec id.");
        }

        var legalEntity = LegalEntity;
        var row = await db.MappingSpecs.FirstOrDefaultAsync(s => s.SpecId == id && s.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return MigrationErrors.NotFound("The mapping spec");
        }

        var fields = MappingEngine.Deserialize<List<MappingField>>(row.Fields) ?? [];
        var maps = await LoadMapsAsync(row.SpecId, cancellationToken).ConfigureAwait(false);
        if (MappingEngine.ContentHash(row.Name, row.SourceObjectType, row.TargetModule, row.TargetObjectType, fields, maps) != row.ContentHash)
        {
            return MigrationErrors.State("The stored spec no longer matches its content hash; create a new version.");
        }

        var approvals = services.GetService<IPlatformApprovalService>();
        if (approvals is null)
        {
            return DomainError.Of(ModuleCode.MIG, "APPROVAL-REQUIRED", "The approval service is not available.");
        }

        var actor = context.Actor.ToString();
        var comment = string.IsNullOrWhiteSpace(body.Comment) ? null : body.Comment.Trim();
        if (comment?.Length > 500)
        {
            return Invalid("The comment is at most 500 characters.");
        }

        try
        {
            if (row.Status == MappingStatus.Draft)
            {
                if (body.Reject == true)
                {
                    return MigrationErrors.State("Only a spec pending approval can be rejected.");
                }

                var response = await approvals.RequestAsync(
                    new ApprovalRequestRequest
                    {
                        Type = MappingApproval.Type,
                        ObjectRef = MappingApproval.Subject(row.SpecId),
                        PayloadHash = Sha256Hash.Parse(row.ContentHash),
                        Authority = new ApprovalAuthority
                        {
                            Type = MappingApproval.Type,
                            Codes = new Dictionary<string, string>(StringComparer.Ordinal) { [MappingApproval.ScopeDimension] = MappingApproval.Scope },
                        },
                        ReferralRole = MappingApproval.ReferralRole,
                        Reason = $"Approve mapping spec {row.Name} v{row.Version} ({row.SourceObjectType} to {row.TargetModule} {row.TargetObjectType}).",
                    },
                    CommandOptions.New(),
                    cancellationToken).ConfigureAwait(false);
                row.Status = MappingStatus.PendingApproval;
                row.RequestedBy = actor;
                row.ApprovalRequestId = response.Request.RequestId;
            }
            else if (row.Status == MappingStatus.PendingApproval && row.ApprovalRequestId is { } requestId)
            {
                if (actor == row.CreatedBy || actor == row.RequestedBy)
                {
                    return DomainError.Of(ModuleCode.MIG, "SIGNOFF-SOD", "The person who created or put forward a mapping spec cannot approve or reject it; a different checker decides.");
                }

                var reject = body.Reject == true;
                await approvals.DecideAsync(
                    new ApprovalDecideRequest
                    {
                        RequestId = requestId,
                        PayloadHash = Sha256Hash.Parse(row.ContentHash),
                        Decision = reject ? ApprovalDecideRequest.DecisionValue.Reject : ApprovalDecideRequest.DecisionValue.Approve,
                        Comment = comment,
                    },
                    CommandOptions.New(),
                    cancellationToken).ConfigureAwait(false);
                row.Status = reject ? MappingStatus.Rejected : MappingStatus.Approved;
                row.DecidedBy = actor;
                row.DecidedAt = clock.Now;
                row.DecisionComment = comment;
            }
            else
            {
                return MigrationErrors.State($"A {row.Status} spec cannot be published; create a new version to change it.");
            }
        }
        catch (DomainException ex) when (ex.Error.Code.Module == ModuleCode.PLT)
        {
            return ex.Error;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToView(row, fields, maps);
    }

    /// <summary>
    /// Loads a spec a batch converts with and proves it is still what was approved (BO-MIG-07): it exists in the legal entity, its status is APPROVED, the
    /// stored content still hashes to the recorded hash and, when given, to the hash the batch recorded at creation.
    /// </summary>
    public async Task<Result<ApprovedMapping>> LoadApprovedAsync(Guid specId, string? expectedHash, CancellationToken cancellationToken)
    {
        var legalEntity = LegalEntity;
        var spec = await db.MappingSpecs.AsNoTracking().FirstOrDefaultAsync(s => s.SpecId == specId && s.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (spec is null)
        {
            return MigrationErrors.NotFound("The mapping spec");
        }

        if (spec.Status != MappingStatus.Approved)
        {
            return MigrationErrors.State($"Mapping spec {spec.Name} v{spec.Version} is {spec.Status}; only an APPROVED spec can convert a batch.");
        }

        var fields = MappingEngine.Deserialize<List<MappingField>>(spec.Fields) ?? [];
        var maps = await LoadMapsAsync(spec.SpecId, cancellationToken).ConfigureAwait(false);
        if (MappingEngine.ContentHash(spec.Name, spec.SourceObjectType, spec.TargetModule, spec.TargetObjectType, fields, maps) != spec.ContentHash
            || (expectedHash is not null && expectedHash != spec.ContentHash))
        {
            return MigrationErrors.State($"Mapping spec {spec.Name} v{spec.Version} no longer matches the content the batch was built from.");
        }

        var lookup = maps.ToDictionary(m => m.Name, m => m.Entries.ToDictionary(e => e.Legacy, e => e.Core, StringComparer.Ordinal), StringComparer.Ordinal);
        return new ApprovedMapping(spec, fields, lookup);
    }

    public async Task<Result<MappingPreviewView>> PreviewAsync(MappingPreviewBody body, CancellationToken cancellationToken)
    {
        var sample = body.SampleSize ?? DefaultSample;
        if (body.SpecId is not { } specId || sample is < 1 or > MaxSample)
        {
            return Invalid($"Give a spec id and a sample size of 1 to {MaxSample}.");
        }

        var legalEntity = LegalEntity;
        var spec = await db.MappingSpecs.AsNoTracking().FirstOrDefaultAsync(s => s.SpecId == specId && s.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (spec is null)
        {
            return MigrationErrors.NotFound("The mapping spec");
        }

        var fields = MappingEngine.Deserialize<List<MappingField>>(spec.Fields) ?? [];
        var maps = await LoadMapsAsync(spec.SpecId, cancellationToken).ConfigureAwait(false);
        if (MappingEngine.ContentHash(spec.Name, spec.SourceObjectType, spec.TargetModule, spec.TargetObjectType, fields, maps) != spec.ContentHash)
        {
            return MigrationErrors.State("The stored spec no longer matches its content hash; create a new version.");
        }

        StagingSnapshotRow? snapshot;
        if (body.SnapshotId is { } sid)
        {
            snapshot = await db.StagingSnapshots.AsNoTracking().FirstOrDefaultAsync(s => s.SnapshotId == sid && s.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
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

        // A snapshot whose latest data quality run still has open errors on this object type is not mapped (waived findings do not count).
        var run = await db.DqRuns.AsNoTracking().Where(r => r.SnapshotId == snapshot.SnapshotId && r.LegalEntityId == legalEntity)
            .OrderByDescending(r => r.RunAt).ThenByDescending(r => r.RunId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (run is not null && body.RequireCleanDq != false)
        {
            var today = DateOnly.FromDateTime(clock.Now.ToUtcDateTime());
            var source = snapshot.Source;
            var open = await db.DqFindings.AsNoTracking().CountAsync(
                f => f.RunId == run.RunId && f.Severity == DqRules.Error && f.ObjectType == spec.SourceObjectType
                    && !db.DqWaivers.Any(w => w.LegalEntityId == legalEntity && w.Source == source && w.ExpiresOn >= today && w.RuleCode == f.RuleCode && w.ObjectType == f.ObjectType && w.LegacyKey == f.LegacyKey),
                cancellationToken).ConfigureAwait(false);
            if (open > 0)
            {
                return MigrationErrors.State($"The latest data quality run of the snapshot has {open} open error(s) on {spec.SourceObjectType} records; fix or waive them before mapping.");
            }
        }

        var raw = await db.StagingRecords.AsNoTracking().Where(r => r.SnapshotId == snapshot.SnapshotId && r.ObjectType == spec.SourceObjectType).OrderBy(r => r.Seq).Take(sample)
            .Select(r => new { r.RecordId, r.Seq, r.ObjectType, r.LegacyKey, r.Payload }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var lookup = maps.ToDictionary(m => m.Name, m => m.Entries.ToDictionary(e => e.Legacy, e => e.Core, StringComparer.Ordinal), StringComparer.Ordinal);
        var items = new List<MappingItemView>(raw.Count);
        var unmapped = new Dictionary<(string Map, string Value), int>();
        foreach (var r in raw)
        {
            using var document = ParseOrEmpty(r.Payload);
            var result = MappingEngine.Apply(new DqRecord(r.RecordId, r.Seq, r.ObjectType, r.LegacyKey, document.RootElement), spec.TargetObjectType, fields, lookup);
            foreach (var issue in result.Issues.Where(i => i.Code == MappingIssueCode.UnmappedCode))
            {
                var map = fields.First(f => f.Target == issue.Field).CodeMap!;
                var key = (map, issue.Value ?? string.Empty);
                unmapped[key] = unmapped.GetValueOrDefault(key) + 1;
            }

            items.Add(new MappingItemView(result.LegacyKey, result.ObjectType, result.LegacyId, result.ItemHash, result.Payload, result.Issues));
        }

        var withIssues = items.Count(i => i.Issues.Count > 0);
        return new MappingPreviewView(
            spec.SpecId, spec.Version, spec.Status, snapshot.SnapshotId, run?.RunId, items.Count, items.Count - withIssues, withIssues, items,
            [.. unmapped.OrderBy(u => u.Key.Map, StringComparer.Ordinal).ThenBy(u => u.Key.Value, StringComparer.Ordinal).Select(u => new MappingUnmappedCodeView(u.Key.Map, u.Key.Value, u.Value))]);
    }

    private static System.Text.Json.JsonDocument ParseOrEmpty(string payload)
    {
        try
        {
            return System.Text.Json.JsonDocument.Parse(payload);
        }
        catch (System.Text.Json.JsonException)
        {
            return System.Text.Json.JsonDocument.Parse("{}");
        }
    }

    private async Task<List<MappingCodeMapDef>> LoadMapsAsync(Guid specId, CancellationToken cancellationToken)
    {
        var entries = await db.CodeMaps.AsNoTracking().Where(c => c.SpecId == specId).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. entries.GroupBy(e => e.MapName, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new MappingCodeMapDef(g.Key, [.. g.OrderBy(e => e.LegacyValue, StringComparer.Ordinal).Select(e => new MappingCodeEntry(e.LegacyValue, e.CoreValue))]))];
    }

    private static MappingSpecView ToView(MappingSpecRow r, IReadOnlyList<MappingField> fields, IReadOnlyList<MappingCodeMapDef> maps) => new(
        r.SpecId, r.Name, r.Version, r.Status, r.SourceObjectType, r.TargetModule, r.TargetObjectType, r.Description, fields, maps, r.ContentHash, r.CreatedBy, r.CreatedAt.ToDateTimeOffset(),
        r.RequestedBy, r.ApprovalRequestId, r.DecidedBy, r.DecidedAt?.ToDateTimeOffset(), r.DecisionComment);
}
