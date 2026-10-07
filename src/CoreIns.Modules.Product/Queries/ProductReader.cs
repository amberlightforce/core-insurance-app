using System.Collections.Concurrent;
using System.Data;
using CoreIns.Modules.Product.Contracts.Api;
using CoreIns.Modules.Product.Domain;
using CoreIns.Platform.Context;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using CoreIns.SharedKernel.Results;
using Dapper;

namespace CoreIns.Modules.Product.Queries;

/// <summary>
/// Compiled artefacts by hash. An artefact never changes (content-addressed), so a loaded one is cached for the life of the
/// process (REQ-PFC-220 in spirit: no database read on the hot path after the first use).
/// </summary>
internal sealed class ArtifactCache
{
    private readonly ConcurrentDictionary<string, CompiledArtefact> _byHash = new(StringComparer.Ordinal);

    public bool TryGet(string hash, out CompiledArtefact artefact) => _byHash.TryGetValue(hash, out artefact!);

    public void Put(CompiledArtefact artefact) => _byHash[artefact.Hash.Value] = artefact;
}

/// <summary>
/// Read side of the product factory: version resolution by date (REQ-PFC-001, -167) and artefact access by hash
/// (REQ-PFC-002, -227). Plain queries, not pipeline commands: nothing here is personal data and a caller's
/// Idempotency-Key must never cause a result to be stored. Every query is filtered by the caller's legal entity.
/// </summary>
internal sealed class ProductReader(DbSession session, RequestContext context, ILegalEntityDirectory legalEntities, ArtifactCache cache)
{
    /// <summary>Resolves the Locked version in force on <paramref name="validAt"/> for a transaction (REQ-PFC-167).</summary>
    public async Task<Result<ProductVersionResolveResponse>> ResolveAsync(
        ProductVersionResolveRequest request, BusinessDate validAt, Instant knownAt, CancellationToken cancellationToken)
    {
        var legalEntity = LegalEntityOf(request.LegalEntity);
        if (legalEntity is null)
        {
            return DomainError.Of(ModuleCode.PFC, "UNKNOWN-PRODUCT", $"Product '{request.Product}' is unknown for this legal entity and jurisdiction.");
        }

        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var productExists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM pfc.product WHERE legal_entity_id = @le AND jurisdiction = @jurisdiction AND code = @code)",
            new { le = legalEntity.Value.Value, jurisdiction = request.Jurisdiction, code = request.Product }, session.Transaction, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        if (!productExists)
        {
            return DomainError.Of(ModuleCode.PFC, "UNKNOWN-PRODUCT", $"Product '{request.Product}' is unknown for this legal entity and jurisdiction.");
        }

        // New business and renewal resolve by the term start date against the version's window (REQ-PFC-167, BR-PFC-003).
        var renewal = request.TransactionType == ProductVersionResolveRequest.TransactionTypeValue.Renewal;
        var from = renewal ? "v.renewal_from" : "v.new_business_from";
        var to = renewal ? "v.renewal_to" : "v.new_business_to";
        var args = new DynamicParameters(new
        {
            le = legalEntity.Value.Value, jurisdiction = request.Jurisdiction, code = request.Product, channel = request.Channel,
            knownAt = knownAt.ToUtcDateTime(),
        });
        args.Add("validAt", validAt.Value, DbType.Date);
        var row = await connection.QueryFirstOrDefaultAsync<ResolvedRow>(new CommandDefinition(
            $"""
             SELECT v.major AS Major, v.minor AS Minor, v.is_abstract AS IsAbstract, v.artefact_hash AS ArtefactHash
               FROM pfc.product p
               JOIN pfc.product_version v ON v.product_id = p.product_id
              WHERE p.legal_entity_id = @le AND p.jurisdiction = @jurisdiction AND p.code = @code
                AND v.status = 'LOCKED' AND v.created_at <= @knownAt AND @channel = ANY (v.channels)
                AND {from} <= @validAt AND ({to} IS NULL OR @validAt < {to})
              ORDER BY v.major DESC, v.minor DESC
              LIMIT 1
             """, args, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (row is null)
        {
            return DomainError.Of(ModuleCode.PFC, "NO-VERSION",
                $"No Locked version of '{request.Product}' is in force for channel {request.Channel} on {validAt} ({(renewal ? "renewal" : "new business")}).");
        }

        if (row.IsAbstract)
        {
            return DomainError.Of(ModuleCode.PFC, "ABSTRACT", $"Version {row.Major}.{row.Minor} of '{request.Product}' is an abstract base and cannot be resolved.");
        }

        var artefact = await LoadAsync(row.ArtefactHash, cancellationToken).ConfigureAwait(false);
        if (artefact is null)
        {
            return DomainError.Of(ModuleCode.PFC, "UNKNOWN-HASH", "The artefact of the resolved version is missing.");
        }

        if (artefact.Artefact.References.Rating is not { } rating)
        {
            return DomainError.Of(ModuleCode.PFC, "NO-RATING", "The resolved version declares no rating slot.");
        }

        var a = artefact.Artefact;
        var manifest = new ProductVersionResolveResponse.ResolutionManifestDetail
        {
            ArtefactHash = artefact.Hash,
            RatingArtefactHash = rating.PinnedArtefactHash,
            UwRuleSetVersions = a.References.UwRuleSets?.ToDictionary(p => p.Key, p => p.Value.Code, StringComparer.Ordinal),
            PaymentPlanVersions = a.References.PaymentPlans?.TryGetValue(request.Channel, out var plans) == true ? plans : null,
        };
        var version = new ProductVersionNumber(row.Major, row.Minor);

        // The resolution hash fixes what was resolved and on what basis (REQ-PFC-221): replaying it later proves the same answer.
        var resolutionHash = new ResolutionHash(CanonicalJson.HashOf(new
        {
            product = request.Product, version = version.ToString(), jurisdiction = request.Jurisdiction, legalEntity = request.LegalEntity,
            channel = request.Channel, transactionType = request.TransactionType.ToString(), validAt = validAt.ToString(), manifest,
        }, SharedKernelJson.Options));
        return new ProductVersionResolveResponse { Version = version, ArtefactHash = artefact.Hash, ResolutionManifest = manifest, ResolutionHash = resolutionHash };
    }

    /// <summary>The artefact with this hash, when a version of the caller's legal entity carries it (null otherwise: another entity's artefact is "not found").</summary>
    public async Task<CompiledArtefact?> GetArtefactAsync(string hash, CancellationToken cancellationToken)
    {
        if (!Sha256Hash.TryParse(hash, out var parsed))
        {
            return null;
        }

        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var visible = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM pfc.product_version WHERE legal_entity_id = @le AND artefact_hash = @hash)",
            new { le = legalEntity.Value, hash = parsed.Value }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return visible ? await LoadAsync(parsed.Value, cancellationToken).ConfigureAwait(false) : null;
    }

    private async Task<CompiledArtefact?> LoadAsync(string hash, CancellationToken cancellationToken)
    {
        if (cache.TryGet(hash, out var cached))
        {
            return cached;
        }

        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var json = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT canonical_json FROM pfc.artifact WHERE artefact_hash = @hash", new { hash }, session.Transaction, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        if (json is null)
        {
            return null;
        }

        var artefact = new CompiledArtefact(ArtefactCompiler.Parse(json), json, Sha256Hash.Parse(hash));
        cache.Put(artefact);
        return artefact;
    }

    private LegalEntityId? LegalEntityOf(string code)
    {
        // A caller sees only its own legal entity (REQ-PTY-035 pattern): any other code is "unknown".
        if (context.LegalEntity is not { } caller || !string.Equals(caller.Value, code, StringComparison.Ordinal))
        {
            return null;
        }

        return legalEntities.Resolve(caller);
    }

    private sealed class ResolvedRow
    {
        public int Major { get; set; }

        public int Minor { get; set; }

        public bool IsAbstract { get; set; }

        public string ArtefactHash { get; set; } = string.Empty;
    }
}
