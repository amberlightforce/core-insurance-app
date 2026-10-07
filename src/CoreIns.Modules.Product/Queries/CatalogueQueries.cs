using System.Globalization;
using System.Text.Json;
using CoreIns.Modules.Product.Contracts.Api;
using CoreIns.Modules.Product.Domain;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Product.Queries;

/// <summary>
/// Catalogue, charge-type, question-set and artefact reads over a compiled artefact (REQ-PFC-003, -004, -006, -226, -227).
/// Pure functions over the immutable artefact after one legal-entity-scoped lookup by hash. Shared by the REST controllers
/// and the in-process services, so both answer identically.
/// </summary>
internal sealed class CatalogueQueries(ProductReader reader)
{
    internal const int DefaultPageSize = 100;
    internal const int MaxPageSize = 200;

    public async Task<Result<ArtefactGetResult>> GetArtefactAsync(string hash, CancellationToken cancellationToken)
    {
        var artefact = await reader.GetArtefactAsync(hash, cancellationToken).ConfigureAwait(false);
        return artefact is null
            ? DomainError.Of(ModuleCode.PFC, "UNKNOWN-HASH", "No artefact with this hash exists for this legal entity.")
            : new ArtefactGetResult(artefact.CanonicalJson);
    }

    /// <summary><c>pfc.Catalogue.get</c>: coverages with terms and options, elements, and the final-key bindings (REQ-PFC-003).</summary>
    public async Task<Result<CatalogueGetResponse>> GetCatalogueAsync(string hash, string? scope, string? code, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(hash, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        var artefact = loaded.Value.Artefact;
        var wantCoverages = scope is null || scope is "all" or "coverages";
        var wantElements = scope is null || scope is "all" or "elements";
        if (!wantCoverages && !wantElements)
        {
            return DomainError.Of(ModuleCode.PFC, "UNKNOWN-ITEM", $"Scope '{scope}' is not one of: all, coverages, elements.");
        }

        var coverages = wantCoverages ? Ordered(artefact.Coverages, c => c.DisplayOrder).Where(c => code is null || c.Code == code).ToList() : null;
        var elements = wantElements ? artefact.Elements.Where(e => code is null || e.Code == code).ToList() : null;
        if (code is not null && (coverages?.Count ?? 0) + (elements?.Count ?? 0) == 0)
        {
            return DomainError.Of(ModuleCode.PFC, "UNKNOWN-ITEM", $"Item '{code}' is not in this artefact.");
        }

        return new CatalogueGetResponse
        {
            ArtefactHash = loaded.Value.Hash,
            Product = artefact.Product.Code,
            Version = artefact.Version,
            Coverages = coverages,
            Elements = elements,
            Finals = FinalsOf(coverages ?? []),
        };
    }

    /// <summary><c>pfc.Catalogue.getItem</c>: one coverage (scope <c>coverage</c>, default) or element (scope <c>element</c>) by code.</summary>
    public async Task<Result<CatalogueGetItemResponse>> GetItemAsync(string hash, string? scope, string? code, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(hash, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        var artefact = loaded.Value.Artefact;
        if (string.IsNullOrEmpty(code))
        {
            return DomainError.Of(ModuleCode.PFC, "UNKNOWN-ITEM", "An item code is required.");
        }

        switch (scope ?? "coverage")
        {
            case "coverage":
                var coverage = artefact.Coverages.FirstOrDefault(c => c.Code == code);
                return coverage is null
                    ? DomainError.Of(ModuleCode.PFC, "UNKNOWN-ITEM", $"Coverage '{code}' is not in this artefact.")
                    : new CatalogueGetItemResponse { Coverage = coverage, Finals = FinalsOf([coverage]) };
            case "element":
                var element = artefact.Elements.FirstOrDefault(e => e.Code == code);
                return element is null
                    ? DomainError.Of(ModuleCode.PFC, "UNKNOWN-ITEM", $"Element '{code}' is not in this artefact.")
                    : new CatalogueGetItemResponse { Element = element, Finals = [] };
            default:
                return DomainError.Of(ModuleCode.PFC, "UNKNOWN-ITEM", $"Scope '{scope}' is not one of: coverage, element.");
        }
    }

    /// <summary><c>pfc.ChargeType.list</c>: the charge-type catalogue of a version (REQ-PFC-004), filters <c>category=</c> and <c>code=</c> (comma separated).</summary>
    public async Task<Result<ChargeTypeListPage>> ListChargeTypesAsync(Sha256Hash? hash, string? filters, string? cursor, int? limit, CancellationToken cancellationToken)
    {
        if (hash is not { } h)
        {
            return DomainError.Of(ModuleCode.PFC, "UNKNOWN-HASH", "An artefact hash is required.");
        }

        if (!int.TryParse(cursor ?? "0", NumberStyles.None, CultureInfo.InvariantCulture, out var offset) || limit is < 1 or > MaxPageSize)
        {
            return DomainError.Of(ModuleCode.PFC, "VALIDATION", "The cursor is malformed or the limit is outside 1..200.");
        }

        var loaded = await LoadAsync(h.Value, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        IEnumerable<ChargeTypeListItem> charges = Ordered(loaded.Value.Artefact.ChargeTypes, c => c.DisplayOrder);
        foreach (var token in (filters ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = token.Split('=', 2);
            if (parts is [var name, var value])
            {
                charges = name switch
                {
                    "category" => charges.Where(c => string.Equals(Codes.Of(c.Category), value, StringComparison.OrdinalIgnoreCase)),
                    "code" => charges.Where(c => c.Code == value),
                    _ => charges,
                };
            }
        }

        var all = charges.ToList();
        var size = limit ?? DefaultPageSize;
        var page = all.Skip(offset).Take(size).ToList();
        var next = offset + page.Count < all.Count ? (offset + page.Count).ToString(CultureInfo.InvariantCulture) : null;
        return new ChargeTypeListPage { Items = page, NextCursor = next, Limit = size };
    }

    /// <summary><c>pfc.QuestionSet.get</c>: the question set with all questions and answer outcomes (answers never in the URL, D-SLC-05).</summary>
    public async Task<Result<QuestionSetGetResponse>> GetQuestionSetAsync(string hash, string? set, CancellationToken cancellationToken)
    {
        var found = await FindSetAsync(hash, set, cancellationToken).ConfigureAwait(false);
        return found.IsFailure
            ? found.Error
            : new QuestionSetGetResponse { ArtefactHash = found.Value.Hash, QuestionSet = found.Value.Set };
    }

    /// <summary><c>pfc.QuestionSet.evaluate</c>: visible/required flags, knock-outs and referrals for the answers given so far (REQ-PFC-006).</summary>
    public async Task<Result<QuestionSetEvaluateResponse>> EvaluateQuestionSetAsync(QuestionSetEvaluateRequest request, CancellationToken cancellationToken)
    {
        var found = await FindSetAsync(request.Hash.Value, request.Set, cancellationToken).ConfigureAwait(false);
        return found.IsFailure
            ? found.Error
            : QuestionEvaluator.Evaluate(found.Value.Set, request.Answers.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal));
    }

    private async Task<Result<(Sha256Hash Hash, QuestionSetDef Set)>> FindSetAsync(string hash, string? set, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(hash, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        var sets = loaded.Value.Artefact.QuestionSets;
        var found = set is null ? (sets.Count == 1 ? sets[0] : null) : sets.FirstOrDefault(s => s.Code == set);
        return found is null
            ? DomainError.Of(ModuleCode.PFC, "UNKNOWN-ITEM", $"Question set '{set ?? "(none given)"}' is not in this artefact.")
            : (loaded.Value.Hash, found);
    }

    private async Task<Result<CompiledArtefact>> LoadAsync(string hash, CancellationToken cancellationToken)
    {
        var artefact = await reader.GetArtefactAsync(hash, cancellationToken).ConfigureAwait(false);
        return artefact is null
            ? DomainError.Of(ModuleCode.PFC, "UNKNOWN-HASH", "No artefact with this hash exists for this legal entity.")
            : artefact;
    }

    private static List<T> Ordered<T>(IEnumerable<T> items, Func<T, int?> order) => [.. items.OrderBy(i => order(i) ?? int.MaxValue)];

    private static List<CatalogueFinal> FinalsOf(IEnumerable<CatalogueCoverage> coverages) =>
    [
        .. coverages.SelectMany(c => c.Terms.Where(t => t.FinalBinding is not null).Select(t => new CatalogueFinal { Coverage = c.Code, Term = t.Code, Binding = t.FinalBinding! })),
    ];
}

/// <summary>The canonical JSON of an artefact, as stored.</summary>
internal sealed record ArtefactGetResult(string CanonicalJson)
{
    public JsonElement ToElement() => JsonSerializer.Deserialize<JsonElement>(CanonicalJson, SharedKernelJson.Options);
}
