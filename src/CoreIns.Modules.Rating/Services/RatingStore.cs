using System.Collections.Concurrent;
using System.Data;
using CoreIns.Modules.Rating.Domain;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Persistence;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Dapper;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Rating.Services;

/// <summary>Module options (<c>Rating:*</c>).</summary>
internal sealed class RatingOptions
{
    public const string Section = "Rating";

    /// <summary>Product codes for which the illustrative motor artefact is published (SL-RAT-UW: stands in for rate-table import and approval).</summary>
    public List<string> SeedProductCodes { get; set; } = [BuiltInArtefacts.DefaultProductCode];
}

/// <summary>A stored rate table version as read.</summary>
internal sealed class TableRecord
{
    public string Hash { get; set; } = string.Empty;

    public string Definition { get; set; } = string.Empty;
}

/// <summary>An artefact as found in the database.</summary>
internal sealed record ArtefactRecord(string ArtefactHash, string ProductCode, string ProductVersion, string DataStatus);

internal sealed record FallbackActivationResult(string? ArtefactHash, bool Created, string? Refusal);

/// <summary>
/// Reads and writes the rat schema through Dapper on the scope's connection (inside the caller's transaction when one is
/// open). Tables, artefacts and worksheets are content-addressed: an identical insert is a no-op, so concurrent
/// requests, retries and rolled-back callers cannot corrupt anything. Compiled artefacts are cached by hash (immutable).
/// </summary>
internal sealed class RatingStore(DbSession session, IClock clock, RequestContext context, IOptions<RatingOptions> options)
{
    private static readonly ConcurrentDictionary<string, CompiledArtefact> Cache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, SeedBundle> Seeds = new(StringComparer.Ordinal);

    /// <summary>Product versions the illustrative tariff is published for, with the date each activates (SL3: MOTOR-GR 1.1 carries the same tariff, referenced by the same tables, from 2026-10-01; 1.0 is untouched).</summary>
    private static readonly (string Version, DateOnly From)[] SeedVersions = [(BuiltInArtefacts.DefaultProductVersion, new DateOnly(2026, 1, 1)), ("1.1", new DateOnly(2026, 10, 1))];

    private sealed record SeedBundle(ArtefactDefinition Definition, string Hash, IReadOnlyList<(string Hash, TableDto Dto)> Tables);

    /// <summary>Publishes the built-in illustrative artefact and its activation when they are not stored yet (idempotent).</summary>
    public async Task EnsureSeededAsync(CancellationToken cancellationToken)
    {
        foreach (var productCode in options.Value.SeedProductCodes)
        {
            foreach (var (seedVersion, seedFrom) in SeedVersions)
            {
                var seed = Seeds.GetOrAdd(productCode + "|" + seedVersion, _ => Build(productCode, seedVersion));
                var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                var exists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                    "SELECT EXISTS (SELECT 1 FROM rat.rating_artifact WHERE artefact_hash = @hash)", new { hash = seed.Hash }, session.Transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
                if (exists)
                {
                    continue;
                }

                var now = clock.Now.ToUtcDateTime();
                var actor = context.Actor.ToString();
                foreach (var (hash, dto) in seed.Tables)
                {
                    await connection.ExecuteAsync(new CommandDefinition(
                        """
                        INSERT INTO rat.rate_table_version (table_hash, table_code, version_no, hit_policy, data_status, definition, created_at, created_by)
                        VALUES (@hash, @code, @version, @hitPolicy, @status, @definition::jsonb, @now, @actor) ON CONFLICT DO NOTHING
                        """,
                        new { hash, code = dto.Code, version = dto.Version, hitPolicy = dto.HitPolicy, status = dto.DataStatus, definition = ArtefactJson.Serialize(dto), now, actor },
                        session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
                }

                var def = seed.Definition;
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO rat.rating_artifact (artefact_hash, artefact_code, label, product_code, product_version, engine_version, data_status, definition, created_at, created_by)
                    VALUES (@hash, @code, @label, @product, @version, @engine, @status, @definition::jsonb, @now, @actor) ON CONFLICT DO NOTHING
                    """,
                    new
                    {
                        hash = seed.Hash, code = def.Code, label = def.Label, product = def.ProductCode, version = def.ProductVersion,
                        engine = def.EngineVersion, status = def.Metadata.DataStatus, definition = ArtefactJson.Serialize(def), now, actor,
                    },
                    session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
                var activation = new DynamicParameters(new
                {
                    id = Guid.CreateVersion7(), hash = seed.Hash, product = def.ProductCode, version = def.ProductVersion, now, actor,
                });
                activation.Add("fromDate", seedFrom, DbType.Date);
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO rat.rate_activation (activation_id, artefact_hash, product_code, product_version, effective_from, effective_to, status, created_at, created_by)
                    VALUES (@id, @hash, @product, @version, @fromDate, NULL, 'Active', @now, @actor) ON CONFLICT DO NOTHING
                    """,
                    activation, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            }
        }
    }

    /// <summary>The artefact active for the product (and version, when given) on <paramref name="basisDate"/> (REQ-RAT-064).</summary>
    public async Task<ArtefactRecord?> ResolveAsync(string productCode, string? productVersion, DateOnly basisDate, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var args = new DynamicParameters(new { product = productCode, version = productVersion });
        args.Add("basis", basisDate, DbType.Date);
        return await connection.QueryFirstOrDefaultAsync<ArtefactRecord>(new CommandDefinition(
            """
            SELECT a.artefact_hash AS ArtefactHash, a.product_code AS ProductCode, a.product_version AS ProductVersion, a.data_status AS DataStatus
              FROM rat.rate_activation r JOIN rat.rating_artifact a ON a.artefact_hash = r.artefact_hash
             WHERE r.product_code = @product AND (@version::text IS NULL OR r.product_version = @version)
               AND r.status = 'Active' AND r.effective_from <= @basis AND (r.effective_to IS NULL OR r.effective_to > @basis)
             ORDER BY r.effective_from DESC, r.product_version DESC LIMIT 1
            """, args, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Records a source-tariff activation without cloning or changing immutable artefacts.</summary>
    public async Task<FallbackActivationResult> ActivateFallbackAsync(
        string productCode, string sourceVersion, string newVersion, DateOnly fromDate, Guid sourceEventId, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var replayArgs = new DynamicParameters(new { sourceEventId, productCode, sourceVersion, newVersion });
        replayArgs.Add("fromDate", fromDate, DbType.Date);
        var prior = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            """
            SELECT artefact_hash FROM rat.rate_activation WHERE source_event_id = @sourceEventId
                AND product_code = @productCode AND product_version = @newVersion
                AND fallback_source_version = @sourceVersion AND effective_from = @fromDate
            """, replayArgs, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (prior is not null)
        {
            // Replaying the publication preserves its original source hash, even after later tariff changes.
            return new(prior, false, null);
        }

        var source = await ResolveAsync(productCode, sourceVersion, fromDate, cancellationToken).ConfigureAwait(false);
        if (source is null)
        {
            return new(null, false, "The fallback source has no active rating artefact; no other tariff is substituted.");
        }
        if (!string.Equals(source.ProductCode, productCode, StringComparison.Ordinal)
            || (!string.Equals(source.ProductVersion, sourceVersion, StringComparison.Ordinal)
                && !await HasFallbackBindingAsync(productCode, sourceVersion, source.ArtefactHash, fromDate, cancellationToken).ConfigureAwait(false)))
        {
            return new(null, false, "The source activation's artefact does not belong to the source product version.");
        }

        var args = new DynamicParameters(new
        {
            id = Guid.CreateVersion7(), hash = source.ArtefactHash, product = productCode, version = newVersion,
            sourceVersion, sourceEventId, now = clock.Now.ToUtcDateTime(), actor = context.Actor.ToString(),
        });
        args.Add("fromDate", fromDate, DbType.Date);
        var inserted = await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO rat.rate_activation (activation_id, artefact_hash, product_code, product_version, effective_from,
                effective_to, status, created_at, created_by, fallback_source_version, source_event_id)
            VALUES (@id, @hash, @product, @version, @fromDate, NULL, 'Active', @now, @actor, @sourceVersion, @sourceEventId)
            ON CONFLICT DO NOTHING
            """, args, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (inserted == 1)
        {
            return new(source.ArtefactHash, true, null);
        }

        var same = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            """
            SELECT EXISTS (SELECT 1 FROM rat.rate_activation WHERE product_code = @product AND product_version = @version
                AND effective_from = @fromDate AND artefact_hash = @hash AND fallback_source_version = @sourceVersion
                AND source_event_id IS NOT NULL AND status = 'Active')
            """, args, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return same ? new(source.ArtefactHash, false, null) : new(null, false, "The new version already has a different activation; it is never overwritten.");
    }

    /// <summary>Only a persisted fallback activation can alias an immutable source artefact to another product version.</summary>
    public async Task<bool> HasFallbackBindingAsync(string product, string version, string hash, DateOnly? activeAt, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var args = new DynamicParameters(new { product, version, hash });
        args.Add("basis", activeAt, DbType.Date);
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            """
            SELECT EXISTS (SELECT 1 FROM rat.rate_activation WHERE product_code = @product AND product_version = @version
                AND artefact_hash = @hash AND fallback_source_version IS NOT NULL AND source_event_id IS NOT NULL
                AND ((@basis::date IS NULL AND status IN ('Active', 'Superseded', 'RolledBack'))
                  OR (@basis::date IS NOT NULL AND status = 'Active' AND effective_from <= @basis
                    AND (effective_to IS NULL OR effective_to > @basis))))
            """, args, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Loads and compiles an artefact by hash; null when unknown.</summary>
    public async Task<CompiledArtefact?> LoadAsync(string hash, CancellationToken cancellationToken)
    {
        if (Cache.TryGetValue(hash, out var cached))
        {
            return cached;
        }

        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var json = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT definition::text FROM rat.rating_artifact WHERE artefact_hash = @hash", new { hash }, session.Transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (json is null)
        {
            return null;
        }

        var definition = ArtefactJson.Deserialize<ArtefactDefinition>(json);
        var tableRows = (await connection.QueryAsync<TableRecord>(new CommandDefinition(
            "SELECT table_hash AS Hash, definition::text AS Definition FROM rat.rate_table_version WHERE table_hash = ANY(@hashes)",
            new { hashes = definition.Tables.Values.ToArray() }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false))
            .ToDictionary(r => r.Hash, r => ArtefactJson.Deserialize<TableDto>(r.Definition), StringComparer.Ordinal);
        var compiled = CompiledArtefact.Compile(definition, tableHash =>
            tableRows.TryGetValue(tableHash, out var dto)
                ? dto
                : throw new DomainException(DomainError.Of(ModuleCode.RAT, "UNKNOWN-ARTEFACT", "A table the artefact pins is missing.")));
        if (!string.Equals(compiled.Hash.Value, hash, StringComparison.Ordinal))
        {
            throw new DomainException(DomainError.Of(ModuleCode.RAT, "UNKNOWN-ARTEFACT", "The stored artefact does not match its hash."));
        }

        return Cache.GetOrAdd(hash, compiled);
    }

    /// <summary>Stores the worksheet (content-addressed) and its index row.</summary>
    public async Task<bool> SaveWorksheetAsync(
        string worksheetId, Guid legalEntity, string jurisdiction, string artefactHash, string configurationHash, string inputHash,
        string dataStatus, string body, QuoteId? quote, JobId? job, PolicyTransactionId? transaction, string mode, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.Now.ToUtcDateTime();
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO rat.worksheet (worksheet_id, legal_entity_id, jurisdiction, artefact_hash, configuration_hash, input_hash, engine_version, data_status, body, created_at, created_by)
            VALUES (@worksheetId, @legalEntity, @jurisdiction, @artefactHash, @configurationHash, @inputHash, @engine, @dataStatus, @body::jsonb, @now, @actor)
            ON CONFLICT DO NOTHING
            """,
            new { worksheetId, legalEntity, jurisdiction, artefactHash, configurationHash, inputHash, engine = EngineVersion.Current, dataStatus, body, now, actor = context.Actor.ToString() },
            session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        var seen = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            """
            SELECT EXISTS (SELECT 1 FROM rat.worksheet_index WHERE worksheet_id = @worksheetId AND legal_entity_id = @legalEntity
               AND quote_id IS NOT DISTINCT FROM @quote AND job_id IS NOT DISTINCT FROM @job AND transaction_id IS NOT DISTINCT FROM @transaction AND mode = @mode)
            """,
            new { worksheetId, legalEntity, quote = quote?.Value, job = job?.Value, transaction = transaction?.Value, mode },
            session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (seen)
        {
            return false; // a retry for the same lineage: no second index row and no second event
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO rat.worksheet_index (worksheet_id, legal_entity_id, quote_id, job_id, transaction_id, mode, retention_state, created_at)
            VALUES (@worksheetId, @legalEntity, @quote, @job, @transaction, @mode, @retention, @now)
            """,
            new
            {
                worksheetId, legalEntity, quote = quote?.Value, job = job?.Value, transaction = transaction?.Value, mode,
                retention = transaction is null ? "QUOTE" : "ATTACHED", now,
            },
            session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return true;
    }

    /// <summary>The stored worksheet body of the caller's legal entity; null when unknown (another entity's worksheet is "not found").</summary>
    public async Task<string?> GetWorksheetAsync(string worksheetId, Guid legalEntity, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT body::text FROM rat.worksheet WHERE worksheet_id = @worksheetId AND legal_entity_id = @legalEntity",
            new { worksheetId, legalEntity }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private static SeedBundle Build(string productCode, string productVersion)
    {
        var tables = BuiltInArtefacts.Tables().Select(t => CompiledArtefact.CompileTable(t)).ToList();
        var definition = BuiltInArtefacts.Definition(
            productCode, productVersion, tables.ToDictionary(t => t.Dto.Code, t => t.Hash, StringComparer.Ordinal));
        return new SeedBundle(definition, ArtefactJson.HashOf(definition).Value, [.. tables.Select(t => (t.Hash, t.Dto))]);
    }
}
