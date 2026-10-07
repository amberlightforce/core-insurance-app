using System.Text.Json;
using CoreIns.Modules.Product.Contracts;
using CoreIns.Modules.Product.Contracts.Api;
using CoreIns.Modules.Product.Queries;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Product.Services;

// The in-process contracts POL, RAT, UW, BIL, FIN and DOC call (D-ARC-16). Every operation here is a read, so these are
// plain services over the queries, not pipeline commands: a caller's Idempotency-Key must not store a result. A failure is
// thrown as a DomainException carrying the PFC-ERR code. Members of the other PFC interfaces (describe, availability, POG,
// regulatory mapping, renewal conversion, policy-draft validation, legacy import) arrive with their work packages and are
// not registered yet.

/// <summary><see cref="IProductProductVersionService"/>: <c>pfc.ProductVersion.resolve</c> (REQ-PFC-001, -167, -221).</summary>
internal sealed class ProductProductVersionService(ProductReader reader, IClock clock) : IProductProductVersionService
{
    public async Task<ProductVersionResolveResponse> ResolveAsync(
        ProductVersionResolveRequest request, ValidAt? validAt = null, Instant? knownAt = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = clock.Now;
        var result = await reader.ResolveAsync(request, Dates.ValidDate(validAt, now), knownAt ?? now, cancellationToken).ConfigureAwait(false);
        return Dates.Unwrap(result);
    }
}

/// <summary><see cref="IProductCatalogueService"/>: <c>pfc.Catalogue.get</c> and <c>getItem</c> (REQ-PFC-003).</summary>
internal sealed class ProductCatalogueService(CatalogueQueries queries) : IProductCatalogueService
{
    public async Task<CatalogueGetResponse> GetAsync(string id, string? scope = null, string? code = null, CancellationToken cancellationToken = default) =>
        Dates.Unwrap(await queries.GetCatalogueAsync(id, scope, code, cancellationToken).ConfigureAwait(false));

    public async Task<CatalogueGetItemResponse> GetItemAsync(Sha256Hash? hash = null, string? scope = null, string? code = null, CancellationToken cancellationToken = default) =>
        Dates.Unwrap(await queries.GetItemAsync(hash?.Value ?? string.Empty, scope, code, cancellationToken).ConfigureAwait(false));
}

/// <summary><see cref="IProductChargeTypeService"/>: <c>pfc.ChargeType.list</c> (REQ-PFC-004).</summary>
internal sealed class ProductChargeTypeService(CatalogueQueries queries) : IProductChargeTypeService
{
    public async Task<ChargeTypeListPage> ListAsync(
        string? cursor = null, int? limit = null, Sha256Hash? hash = null, string? filters = null, CancellationToken cancellationToken = default) =>
        Dates.Unwrap(await queries.ListChargeTypesAsync(hash, filters, cursor, limit, cancellationToken).ConfigureAwait(false));
}

/// <summary><see cref="IProductQuestionSetService"/>: <c>pfc.QuestionSet.get</c> and <c>evaluate</c> (REQ-PFC-006).</summary>
internal sealed class ProductQuestionSetService(CatalogueQueries queries) : IProductQuestionSetService
{
    public async Task<QuestionSetEvaluateResponse> EvaluateAsync(QuestionSetEvaluateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Dates.Unwrap(await queries.EvaluateQuestionSetAsync(request, cancellationToken).ConfigureAwait(false));
    }

    public async Task<QuestionSetGetResponse> GetAsync(string id, string? set = null, CancellationToken cancellationToken = default) =>
        Dates.Unwrap(await queries.GetQuestionSetAsync(id, set, cancellationToken).ConfigureAwait(false));
}

/// <summary><see cref="IProductArtifactService"/>: <c>pfc.Artifact.get</c> (REQ-PFC-002, -227). <c>parts</c> is not supported yet: the whole artefact is returned.</summary>
internal sealed class ProductArtifactService(CatalogueQueries queries) : IProductArtifactService
{
    public async Task<ArtifactGetResponse> GetAsync(string id, string? parts = null, CancellationToken cancellationToken = default)
    {
        var result = Dates.Unwrap(await queries.GetArtefactAsync(id, cancellationToken).ConfigureAwait(false));
        return new ArtifactGetResponse { CanonicalJsonArtefact = result.ToElement() };
    }
}

internal static class Dates
{
    public static BusinessDate ValidDate(ValidAt? validAt, Instant now) =>
        validAt is { Date: { } date } ? date
        : validAt is { Instant: { } instant } ? new BusinessDate(DateOnly.FromDateTime(instant.ToUtcDateTime()))
        : new BusinessDate(DateOnly.FromDateTime(now.ToUtcDateTime()));

    public static T Unwrap<T>(Result<T> result) => result.IsSuccess ? result.Value : throw new DomainException(result.Error);

    public static JsonElement ToElement(string json) => JsonSerializer.Deserialize<JsonElement>(json, SharedKernelJson.Options);
}
