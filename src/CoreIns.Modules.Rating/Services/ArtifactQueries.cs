using System.Data;
using System.Globalization;
using CoreIns.Modules.Rating.Contracts.Api;
using CoreIns.Modules.Rating.Domain;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Persistence;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Paging;
using Dapper;

namespace CoreIns.Modules.Rating.Services;

/// <summary>
/// Read-only catalogue of rating artefacts (BO-RAT-04): get, keyset-paged list and the artefact-vs-product-version compatibility check.
/// The lifecycle status is derived from the activation rows (no activation = DRAFT). Roles outside <see cref="AuthoringRoles"/>
/// never see DRAFT artefacts: they are filtered out of lists and answer 404 on get, like an unknown hash.
/// </summary>
internal sealed class ArtifactQueries(DbSession session, RequestContext context, IClock clock)
{
    /// <summary>Roles that author artefacts and may therefore see unreleased (DRAFT) ones.</summary>
    internal static readonly string[] AuthoringRoles = ["Staff.UnderwritingManager", "Platform.Admin"];

    private static readonly string[] Lifecycles = ["DRAFT", "SCHEDULED", "ACTIVE", "SUPERSEDED", "WITHDRAWN"];
    private static readonly string[] DataStatuses = ["ILLUSTRATIVE_TEST_DATA", "APPROVED"];

    // One row per artefact with its derived lifecycle. The catalogue is a platform-level library of product-version
    // artefacts (rat.rating_artifact carries no legal entity); the caller's legal entity is required by the request pipeline.
    private const string Projection =
        """
        SELECT a.artefact_hash AS ArtefactHash, a.artefact_code AS ArtefactCode, a.label AS Label, a.product_code AS ProductCode,
               a.product_version AS ProductVersion, a.engine_version AS EngineVersion, a.data_status AS DataStatus,
               a.definition::text AS Definition, a.created_at AS CreatedAt,
               CASE WHEN x.n IS NULL THEN 'DRAFT'
                    WHEN x.cur THEN 'ACTIVE'
                    WHEN x.fut THEN 'SCHEDULED'
                    WHEN x.live THEN 'SUPERSEDED'
                    ELSE 'WITHDRAWN' END AS Lifecycle
          FROM rat.rating_artifact a
          LEFT JOIN (
              SELECT artefact_hash, count(*) AS n,
                     bool_or(status <> 'Withdrawn' AND effective_from <= @today AND (effective_to IS NULL OR effective_to >= @today)) AS cur,
                     bool_or(status <> 'Withdrawn' AND effective_from > @today) AS fut,
                     bool_or(status <> 'Withdrawn') AS live
                FROM rat.rate_activation GROUP BY artefact_hash) x ON x.artefact_hash = a.artefact_hash
        """;

    private bool SeesDrafts => context.Roles.Any(r => AuthoringRoles.Contains(r, StringComparer.Ordinal));

    private DateOnly Today => DateOnly.FromDateTime(clock.Now.ToUtcDateTime());

    public async Task<RatingArtifactGetResponse> GetAsync(string id, CancellationToken cancellationToken)
    {
        RequireLegalEntity();
        var row = await LoadAsync(id, cancellationToken).ConfigureAwait(false);
        var activations = await ActivationsAsync([row.ArtefactHash], cancellationToken).ConfigureAwait(false);
        var mine = activations.GetValueOrDefault(row.ArtefactHash) ?? [];
        return new RatingArtifactGetResponse
        {
            Manifest = Manifest(row),
            Status = StatusOf(row, mine),
            CompatibilityRecords = CompatibilityOf(row, mine, null),
        };
    }

    public async Task<RatingArtifactListPage> ListAsync(string? cursor, int? limit, string? productCode, string? status, string? dataStatus, CancellationToken cancellationToken)
    {
        RequireLegalEntity();
        if (!KeysetPaging.TryTake(limit, KeysetPaging.DefaultPageSize, out var take))
        {
            throw Invalid("limit must be between 1 and " + KeysetPaging.MaxPageSize + ".");
        }

        status = status?.ToUpperInvariant();
        if ((status is not null && !Lifecycles.Contains(status)) || (dataStatus is not null && !DataStatuses.Contains(dataStatus)))
        {
            throw Invalid("Unknown status or dataStatus filter.");
        }

        if (!KeysetPaging.TryDecode(cursor, 2, out var parts) || (parts is not null && !KeysetPaging.TryLong(parts[0], out _)))
        {
            throw Invalid("The cursor is malformed.");
        }

        DateTime? after = null;
        string? afterHash = null;
        if (parts is not null)
        {
            after = new DateTime(long.Parse(parts[0], CultureInfo.InvariantCulture), DateTimeKind.Utc);
            afterHash = parts[1];
        }

        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var args = new DynamicParameters(new { product = productCode, status, dataStatus, drafts = SeesDrafts, afterHash, take = take + 1, after });
        args.Add("today", Today, DbType.Date);
        var rows = (await connection.QueryAsync<Row>(new CommandDefinition(
            "SELECT * FROM (" + Projection + ") r WHERE (@product::text IS NULL OR r.ProductCode = @product) AND (@dataStatus::text IS NULL OR r.DataStatus = @dataStatus)" +
            " AND (@status::text IS NULL OR r.Lifecycle = @status) AND (@drafts OR r.Lifecycle <> 'DRAFT')" +
            " AND (@after::timestamptz IS NULL OR r.CreatedAt < @after OR (r.CreatedAt = @after AND r.ArtefactHash > @afterHash))" +
            " ORDER BY r.CreatedAt DESC, r.ArtefactHash ASC LIMIT @take",
            args, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();
        var (page, next) = KeysetPaging.Slice(rows, take, r => KeysetPaging.Encode(KeysetPaging.Part(r.CreatedAt.ToUniversalTime().Ticks), r.ArtefactHash));
        var activations = await ActivationsAsync([.. page.Select(r => r.ArtefactHash)], cancellationToken).ConfigureAwait(false);
        return new RatingArtifactListPage
        {
            Items = [.. page.Select(r =>
            {
                var mine = activations.GetValueOrDefault(r.ArtefactHash) ?? [];
                return new RatingArtifactListItem { Manifest = Manifest(r), Status = StatusOf(r, mine), CompatibilityRecords = CompatibilityOf(r, mine, null) };
            })],
            NextCursor = next,
            Limit = take,
        };
    }

    public async Task<RatingArtifactCompatibilityResponse> CompatibilityAsync(string? artefactHash, string? productVersion, CancellationToken cancellationToken)
    {
        RequireLegalEntity();
        if (string.IsNullOrWhiteSpace(artefactHash))
        {
            throw Invalid("artefactHash is required.");
        }

        var row = await LoadAsync(artefactHash, cancellationToken).ConfigureAwait(false);
        var activations = await ActivationsAsync([row.ArtefactHash], cancellationToken).ConfigureAwait(false);
        var mine = activations.GetValueOrDefault(row.ArtefactHash) ?? [];
        return new RatingArtifactCompatibilityResponse
        {
            Manifest = Manifest(row),
            Status = StatusOf(row, mine),
            CompatibilityRecords = CompatibilityOf(row, mine, string.IsNullOrWhiteSpace(productVersion) ? null : productVersion),
        };
    }

    private void RequireLegalEntity()
    {
        if (context.LegalEntity is null)
        {
            throw Invalid("The request has no legal entity.");
        }
    }

    private async Task<Row> LoadAsync(string id, CancellationToken ct)
    {
        var connection = await session.OpenConnectionAsync(ct).ConfigureAwait(false);
        var args = new DynamicParameters(new { id });
        args.Add("today", Today, DbType.Date);
        var row = await connection.QueryFirstOrDefaultAsync<Row>(new CommandDefinition(
            "SELECT * FROM (" + Projection + ") r WHERE r.ArtefactHash = @id", args, session.Transaction, cancellationToken: ct)).ConfigureAwait(false);
        if (row is null || (row.Lifecycle == "DRAFT" && !SeesDrafts))
        {
            throw new DomainException(DomainError.Of(ModuleCode.RAT, PlatformErrors.NotFound, "The rating artefact does not exist."));
        }

        return row;
    }

    private async Task<Dictionary<string, List<ActivationReadRow>>> ActivationsAsync(string[] hashes, CancellationToken ct)
    {
        if (hashes.Length == 0)
        {
            return [];
        }

        var connection = await session.OpenConnectionAsync(ct).ConfigureAwait(false);
        var rows = await connection.QueryAsync<ActivationReadRow>(new CommandDefinition(
            """
            SELECT activation_id AS ActivationId, artefact_hash AS ArtefactHash, product_version AS ProductVersion, status AS Status,
                   effective_from AS EffectiveFrom, effective_to AS EffectiveTo
              FROM rat.rate_activation WHERE artefact_hash = ANY(@hashes) ORDER BY effective_from, activation_id
            """,
            new { hashes }, session.Transaction, cancellationToken: ct)).ConfigureAwait(false);
        return rows.GroupBy(r => r.ArtefactHash, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
    }

    private static RatingArtifactManifest Manifest(Row row)
    {
        var def = ArtefactJson.Deserialize<ArtefactDefinition>(row.Definition);
        return new RatingArtifactManifest
        {
            ArtefactHash = row.ArtefactHash,
            ArtefactCode = row.ArtefactCode,
            Label = row.Label,
            ProductCode = row.ProductCode,
            ProductVersion = row.ProductVersion,
            EngineVersion = row.EngineVersion,
            DataStatus = row.DataStatus,
            StepCount = def.Steps.Count,
            Tables = [.. def.Tables.OrderBy(t => t.Key, StringComparer.Ordinal).Select(t => new RatingArtifactManifest.TableItem { Slot = t.Key, Hash = t.Value })],
            CreatedAt = DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture),
        };
    }

    private static RatingArtifactStatus StatusOf(Row row, List<ActivationReadRow> activations)
    {
        var live = activations.Where(a => a.Status != "Withdrawn").ToList();
        return new RatingArtifactStatus
        {
            Lifecycle = Enum.Parse<RatingArtifactStatus.LifecycleValue>(row.Lifecycle, ignoreCase: true),
            EffectiveFrom = live.Count == 0 ? null : Iso(live.Min(a => a.EffectiveFrom)),
            EffectiveTo = live.Count == 0 || live.Any(a => a.EffectiveTo is null) ? null : Iso(live.Max(a => a.EffectiveTo!.Value)),
            Activations = [.. activations.Select(a => new RatingArtifactStatus.ActivationItem
            {
                ActivationId = a.ActivationId.ToString("D"),
                ProductVersion = a.ProductVersion,
                Status = a.Status,
                EffectiveFrom = Iso(a.EffectiveFrom),
                EffectiveTo = a.EffectiveTo is null ? null : Iso(a.EffectiveTo.Value),
            })],
        };
    }

    // TODO(hardening): also check the PFC product version's `references` (rating artefact slot, reference tables) once the product module exposes a read for them.
    private static List<RatingArtifactCompatibilityRecord> CompatibilityOf(Row row, List<ActivationReadRow> activations, string? requestedVersion)
    {
        var records = new List<RatingArtifactCompatibilityRecord>
        {
            new() { ProductCode = row.ProductCode, ProductVersion = row.ProductVersion, Relation = RatingArtifactCompatibilityRecord.RelationValue.Pinned, Compatible = true, Reason = null },
        };
        foreach (var version in activations.Where(a => a.Status != "Withdrawn").Select(a => a.ProductVersion).Distinct(StringComparer.Ordinal).Where(v => v != row.ProductVersion))
        {
            records.Add(new RatingArtifactCompatibilityRecord { ProductCode = row.ProductCode, ProductVersion = version, Relation = RatingArtifactCompatibilityRecord.RelationValue.Activated, Compatible = true, Reason = null });
        }

        if (requestedVersion is not null && !records.Any(r => r.ProductVersion == requestedVersion))
        {
            records.Add(new RatingArtifactCompatibilityRecord
            {
                ProductCode = row.ProductCode, ProductVersion = requestedVersion, Relation = RatingArtifactCompatibilityRecord.RelationValue.Activated, Compatible = false,
                Reason = $"The artefact is pinned to product version {row.ProductVersion} and has no activation for {requestedVersion}.",
            });
        }

        return records;
    }

    private static string Iso(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DomainException Invalid(string message) => new(DomainError.Of(ModuleCode.RAT, PlatformErrors.Validation, message));

    private sealed class Row
    {
        public string ArtefactHash { get; set; } = string.Empty;

        public string ArtefactCode { get; set; } = string.Empty;

        public string Label { get; set; } = string.Empty;

        public string ProductCode { get; set; } = string.Empty;

        public string ProductVersion { get; set; } = string.Empty;

        public string EngineVersion { get; set; } = string.Empty;

        public string DataStatus { get; set; } = string.Empty;

        public string Definition { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; }

        public string Lifecycle { get; set; } = string.Empty;
    }

    private sealed class ActivationReadRow
    {
        public Guid ActivationId { get; set; }

        public string ArtefactHash { get; set; } = string.Empty;

        public string ProductVersion { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public DateTime EffectiveFrom { get; set; }

        public DateTime? EffectiveTo { get; set; }
    }
}
