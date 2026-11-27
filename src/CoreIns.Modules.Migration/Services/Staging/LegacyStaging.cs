using System.Text;
using System.Text.Json;
using CoreIns.Modules.Migration.Domain;
using CoreIns.Modules.Migration.Persistence;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Migration.Services.Staging;

/// <summary>mig.Staging.load body.</summary>
internal sealed record StagingLoadBody(string? Source, string? Snapshot, string? Format, string? Content);

/// <summary>A staged snapshot (mig.Staging.load, mig.Staging.snapshots).</summary>
internal sealed record StagingSnapshotView(
    Guid SnapshotId, string Source, string Snapshot, int Version, string Status, string Format, string ContentHash, int RecordCount,
    IReadOnlyDictionary<string, int> Counts, string LoadedBy, DateTimeOffset LoadedAt, bool? Unchanged, Guid? SupersededSnapshotId);

internal sealed record StagingSnapshotList(IReadOnlyList<StagingSnapshotView> Items);

/// <summary><c>mig.staging_snapshot</c>: one load of one legacy extract. Only <see cref="Status"/> ever changes (ACTIVE to SUPERSEDED).</summary>
internal sealed class StagingSnapshotRow
{
    public Guid SnapshotId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public string Source { get; set; } = string.Empty;

    public string Snapshot { get; set; } = string.Empty;

    public int Version { get; set; }

    public string Status { get; set; } = StagingStatus.Active;

    public string Format { get; set; } = string.Empty;

    public string ContentHash { get; set; } = string.Empty;

    public int RecordCount { get; set; }

    /// <summary>Records per object type, as a JSON object.</summary>
    public string Counts { get; set; } = "{}";

    public string LoadedBy { get; set; } = string.Empty;

    public Instant LoadedAt { get; set; }
}

/// <summary><c>mig.staging_record</c>: one raw legacy record of a snapshot. Insert-only; never validated or converted here (DQ is BO-MIG-03).</summary>
internal sealed class StagingRecordRow
{
    public Guid RecordId { get; set; }

    public Guid SnapshotId { get; set; }

    public int Seq { get; set; }

    public string ObjectType { get; set; } = string.Empty;

    public string LegacyKey { get; set; } = string.Empty;

    public string Payload { get; set; } = "{}";

    /// <summary>SHA-256 of object type, legacy key and payload text.</summary>
    public string RowHash { get; set; } = string.Empty;
}

internal static class StagingStatus
{
    public const string Active = "ACTIVE";
    public const string Superseded = "SUPERSEDED";
}

internal static class StagingModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<StagingSnapshotRow>(e =>
        {
            e.ToTable("staging_snapshot", t => t.HasCheckConstraint("ck_staging_snapshot_status", "status IN ('ACTIVE','SUPERSEDED')"));
            e.HasKey(r => r.SnapshotId).HasName("pk_staging_snapshot");
            e.Property(r => r.SnapshotId).HasColumnName("snapshot_id");
            e.Property(r => r.LegalEntityId).HasColumnName("legal_entity_id");
            e.Property(r => r.Source).HasColumnName("source");
            e.Property(r => r.Snapshot).HasColumnName("snapshot");
            e.Property(r => r.Version).HasColumnName("version");
            e.Property(r => r.Status).HasColumnName("status");
            e.Property(r => r.Format).HasColumnName("format");
            e.Property(r => r.ContentHash).HasColumnName("content_hash");
            e.Property(r => r.RecordCount).HasColumnName("record_count");
            e.Property(r => r.Counts).HasColumnName("counts").HasColumnType("jsonb");
            e.Property(r => r.LoadedBy).HasColumnName("loaded_by");
            e.Property(r => r.LoadedAt).HasColumnName("loaded_at").HasColumnType("timestamptz");
            e.HasIndex(r => new { r.LegalEntityId, r.Source, r.Snapshot, r.Version }).IsUnique().HasDatabaseName("ux_staging_snapshot_version");
        });

        model.Entity<StagingRecordRow>(e =>
        {
            e.ToTable("staging_record");
            e.HasKey(r => r.RecordId).HasName("pk_staging_record");
            e.Property(r => r.RecordId).HasColumnName("record_id");
            e.Property(r => r.SnapshotId).HasColumnName("snapshot_id");
            e.Property(r => r.Seq).HasColumnName("seq");
            e.Property(r => r.ObjectType).HasColumnName("object_type");
            e.Property(r => r.LegacyKey).HasColumnName("legacy_key");
            e.Property(r => r.Payload).HasColumnName("payload").HasColumnType("jsonb");
            e.Property(r => r.RowHash).HasColumnName("row_hash");
            e.HasIndex(r => new { r.SnapshotId, r.Seq }).IsUnique().HasDatabaseName("ux_staging_record_seq");
            e.HasIndex(r => new { r.SnapshotId, r.ObjectType, r.LegacyKey }).HasDatabaseName("ix_staging_record_key");
            e.HasOne<StagingSnapshotRow>().WithMany().HasForeignKey(r => r.SnapshotId).HasConstraintName("fk_staging_record_snapshot");
        });
    }
}

/// <summary>A parsed extract record before it is stored.</summary>
internal sealed record ExtractRecord(string ObjectType, string LegacyKey, string Payload);

/// <summary>
/// Reads and stores legacy extracts (BO-MIG-02). Raw and append-only: records are stored as received (JSON text per record), a reload
/// with changed content is a new snapshot version that supersedes the earlier one, and nothing is imported until a batch is created.
/// </summary>
internal sealed class LegacyStaging(MigrationDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock)
{
    /// <summary>Largest extract one load accepts.</summary>
    public const int MaxRecords = 100_000;

    private LegalEntityId LegalEntity => legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));

    public async Task<Result<StagingSnapshotView>> LoadAsync(StagingLoadBody body, CancellationToken cancellationToken)
    {
        var source = body.Source?.Trim().ToUpperInvariant() ?? string.Empty;
        var label = body.Snapshot?.Trim() ?? string.Empty;
        var format = body.Format?.Trim().ToUpperInvariant() ?? string.Empty;
        if (source.Length is 0 or > 8 || !source.All(char.IsAsciiLetterOrDigit))
        {
            return Invalid("The source is 1 to 8 letters or digits.");
        }

        if (label.Length is 0 or > 64)
        {
            return Invalid("The snapshot label is 1 to 64 characters.");
        }

        if (format is not ("JSON" or "CSV") || string.IsNullOrWhiteSpace(body.Content))
        {
            return Invalid("Give the extract text and a format of JSON or CSV.");
        }

        var parsed = format == "JSON" ? ParseJson(body.Content) : ParseCsv(body.Content);
        if (parsed.Error is not null)
        {
            return Invalid(parsed.Error);
        }

        var records = parsed.Records!;
        var legalEntity = LegalEntity;
        var hash = Sha256Hash.ComputeUtf8(body.Content).Value;
        var rows = await db.StagingSnapshots.Where(s => s.LegalEntityId == legalEntity && s.Source == source && s.Snapshot == label)
            .OrderByDescending(s => s.Version).ToListAsync(cancellationToken).ConfigureAwait(false);
        var active = rows.FirstOrDefault(r => r.Status == StagingStatus.Active);
        if (active is not null && active.ContentHash == hash)
        {
            return ToView(active, unchanged: true, superseded: null);
        }

        var counts = records.GroupBy(r => r.ObjectType, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count());
        var snapshot = new StagingSnapshotRow
        {
            SnapshotId = Guid.CreateVersion7(), LegalEntityId = legalEntity, Source = source, Snapshot = label, Version = (rows.Count == 0 ? 0 : rows[0].Version) + 1,
            Format = format, ContentHash = hash, RecordCount = records.Count, Counts = JsonSerializer.Serialize(counts), LoadedBy = context.Actor.ToString(), LoadedAt = clock.Now,
        };
        if (active is not null)
        {
            active.Status = StagingStatus.Superseded;
        }

        db.StagingSnapshots.Add(snapshot);
        var seq = 0;
        foreach (var record in records)
        {
            db.StagingRecords.Add(new StagingRecordRow
            {
                RecordId = Guid.CreateVersion7(), SnapshotId = snapshot.SnapshotId, Seq = ++seq, ObjectType = record.ObjectType, LegacyKey = record.LegacyKey, Payload = record.Payload,
                RowHash = Sha256Hash.ComputeUtf8($"{record.ObjectType}|{record.LegacyKey}|{record.Payload}").Value,
            });
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToView(snapshot, unchanged: null, superseded: active?.SnapshotId);
    }

    public async Task<Result<StagingSnapshotList>> SnapshotsAsync(string? source, CancellationToken cancellationToken)
    {
        var legalEntity = LegalEntity;
        var query = db.StagingSnapshots.AsNoTracking().Where(s => s.LegalEntityId == legalEntity);
        if (!string.IsNullOrWhiteSpace(source))
        {
            var code = source.Trim().ToUpperInvariant();
            query = query.Where(s => s.Source == code);
        }

        var rows = await query.OrderByDescending(s => s.LoadedAt).ThenByDescending(s => s.Version).Take(200).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new StagingSnapshotList([.. rows.Select(r => ToView(r, unchanged: null, superseded: null))]);
    }

    private static StagingSnapshotView ToView(StagingSnapshotRow r, bool? unchanged, Guid? superseded) => new(
        r.SnapshotId, r.Source, r.Snapshot, r.Version, r.Status, r.Format, r.ContentHash, r.RecordCount,
        JsonSerializer.Deserialize<Dictionary<string, int>>(r.Counts) ?? [], r.LoadedBy, r.LoadedAt.ToDateTimeOffset(), unchanged, superseded);

    private static DomainError Invalid(string detail) => DomainError.Of(ModuleCode.MIG, MigrationErrors.Validation, detail);

    internal sealed record Parsed(IReadOnlyList<ExtractRecord>? Records, string? Error);

    /// <summary>
    /// JSON: <c>{"records":[{"objectType","legacyKey","data":{}}]}</c>, a bare array of such records, or one scenario object like
    /// <c>tests/e2e/fixtures/legacy-gr1.json</c> (stored as one record, object type SCENARIO, key = its <c>id</c>).
    /// </summary>
    internal static Parsed ParseJson(string content)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(content);
        }
        catch (JsonException)
        {
            return new Parsed(null, "The extract is not valid JSON.");
        }

        using (doc)
        {
            var root = doc.RootElement;
            JsonElement? list = root.ValueKind == JsonValueKind.Array ? root
                : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("records", out var r) && r.ValueKind == JsonValueKind.Array ? r : null;
            if (list is null)
            {
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String)
                {
                    return new Parsed(null, "The JSON extract is a records array, an object with a records array, or one scenario object with an id.");
                }

                return new Parsed([new ExtractRecord("SCENARIO", id.GetString()!, root.GetRawText())], null);
            }

            var result = new List<ExtractRecord>();
            foreach (var item in list.Value.EnumerateArray())
            {
                if (result.Count >= MaxRecords)
                {
                    return new Parsed(null, $"An extract holds at most {MaxRecords} records.");
                }

                var type = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("objectType", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                var key = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("legacyKey", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
                if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(key) || type.Length > 64 || key.Length > 200)
                {
                    return new Parsed(null, $"Record {result.Count + 1} needs an objectType (up to 64) and a legacyKey (up to 200).");
                }

                var data = item.TryGetProperty("data", out var d) ? d.GetRawText() : "{}";
                result.Add(new ExtractRecord(type, key, data));
            }

            return new Parsed(result, null);
        }
    }

    /// <summary>CSV: a header row with <c>objectType</c> and <c>legacyKey</c>; every other column is a data field (text as received).</summary>
    internal static Parsed ParseCsv(string content)
    {
        var rows = ReadCsv(content);
        if (rows.Count == 0)
        {
            return new Parsed(null, "The CSV extract has no header row.");
        }

        var header = rows[0].Select(h => h.Trim()).ToList();
        var typeIndex = header.FindIndex(h => string.Equals(h, "objectType", StringComparison.OrdinalIgnoreCase));
        var keyIndex = header.FindIndex(h => string.Equals(h, "legacyKey", StringComparison.OrdinalIgnoreCase));
        if (typeIndex < 0 || keyIndex < 0)
        {
            return new Parsed(null, "The CSV header needs objectType and legacyKey columns.");
        }

        var result = new List<ExtractRecord>();
        for (var i = 1; i < rows.Count; i++)
        {
            if (result.Count >= MaxRecords)
            {
                return new Parsed(null, $"An extract holds at most {MaxRecords} records.");
            }

            var cells = rows[i];
            var type = typeIndex < cells.Count ? cells[typeIndex].Trim() : string.Empty;
            var key = keyIndex < cells.Count ? cells[keyIndex].Trim() : string.Empty;
            if (type.Length is 0 or > 64 || key.Length is 0 or > 200)
            {
                return new Parsed(null, $"CSV row {i + 1} needs an objectType (up to 64) and a legacyKey (up to 200).");
            }

            var data = new SortedDictionary<string, string>(StringComparer.Ordinal);
            for (var c = 0; c < header.Count; c++)
            {
                if (c != typeIndex && c != keyIndex && header[c].Length > 0)
                {
                    data[header[c]] = c < cells.Count ? cells[c] : string.Empty;
                }
            }

            result.Add(new ExtractRecord(type, key, JsonSerializer.Serialize(data)));
        }

        return new Parsed(result, null);
    }

    private static List<List<string>> ReadCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else if (ch == '"')
                {
                    quoted = false;
                }
                else
                {
                    cell.Append(ch);
                }

                continue;
            }

            switch (ch)
            {
                case '"':
                    quoted = true;
                    break;
                case ',':
                    row.Add(cell.ToString());
                    cell.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    row.Add(cell.ToString());
                    cell.Clear();
                    AddRow(rows, row);
                    row = [];
                    break;
                default:
                    cell.Append(ch);
                    break;
            }
        }

        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            AddRow(rows, row);
        }

        return rows;
    }

    private static void AddRow(List<List<string>> rows, List<string> row)
    {
        if (row.Count > 1 || (row.Count == 1 && row[0].Length > 0))
        {
            rows.Add(row);
        }
    }
}
