using System.Text.Json.Nodes;
using CoreIns.Modules.Product.Contracts.Api;
using CoreIns.Modules.Product.Domain;
using CoreIns.Modules.Product.Persistence;
using CoreIns.Platform.Context;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Product.Queries;

/// <summary>Reads of BO-PFC-03: the diff of two Locked versions (<c>pfc.ProductVersion.diff</c>) and the YAML or JSON export of one (<c>pfc.ProductVersion.export</c>).</summary>
internal sealed class ProductSourceQueries(ProductDbContext db, RequestContext context, ILegalEntityDirectory legalEntities)
{
    public async Task<Result<ProductVersionDiffResponse>> DiffAsync(string? product, string? from, string? to, CancellationToken cancellationToken)
    {
        if (!TryVersion(from, out var fromNumber) || !TryVersion(to, out var toNumber))
        {
            return Bad("from", "Give from and to as version numbers such as 1.0.");
        }

        var older = await LoadAsync(product, fromNumber, cancellationToken).ConfigureAwait(false);
        if (older.IsFailure)
        {
            return older.Error;
        }

        var newer = await LoadAsync(product, toNumber, cancellationToken).ConfigureAwait(false);
        if (newer.IsFailure)
        {
            return newer.Error;
        }

        var groups = ProductDiff.Compute(JsonNode.Parse(older.Value.Json)!, JsonNode.Parse(newer.Value.Json)!);
        return new ProductVersionDiffResponse
        {
            Product = product!,
            From = fromNumber,
            To = toNumber,
            Total = groups.Sum(g => g.Entries.Count),
            Groups = [.. groups],
        };
    }

    public async Task<Result<ProductVersionExportResponse>> ExportAsync(string? product, string? version, string? format, CancellationToken cancellationToken)
    {
        if (!TryVersion(version, out var number))
        {
            return Bad("version", "Give version as a version number such as 1.0.");
        }

        var yaml = !string.Equals(format, "JSON", StringComparison.OrdinalIgnoreCase);
        if (format is not null && yaml && !string.Equals(format, "YAML", StringComparison.OrdinalIgnoreCase))
        {
            return Bad("format", "format is YAML or JSON.");
        }

        var loaded = await LoadAsync(product, number, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        return new ProductVersionExportResponse
        {
            Product = product!,
            Version = number,
            ArtefactHash = Sha256Hash.Parse(loaded.Value.Hash),
            Format = yaml ? ProductVersionExportResponse.FormatValue.Yaml : ProductVersionExportResponse.FormatValue.Json,
            Source = yaml ? ProductYaml.Write(JsonNode.Parse(loaded.Value.Json)!) : loaded.Value.Json,
        };
    }

    private async Task<Result<(string Json, string Hash)>> LoadAsync(string? product, ProductVersionNumber number, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(product))
        {
            return Bad("product", "product is required.");
        }

        var caller = context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.");
        var legalEntity = legalEntities.Resolve(caller);
        var major = number.Major;
        var minor = number.Minor;
        var row = await (
                from p in db.Products.AsNoTracking()
                join v in db.Versions.AsNoTracking() on p.ProductId equals v.ProductId
                join a in db.Artifacts.AsNoTracking() on v.ArtefactHash equals a.ArtefactHash
                where p.LegalEntityId == legalEntity && p.Code == product && v.Major == major && v.Minor == minor && v.Status == "LOCKED"
                select new { a.CanonicalJson, a.ArtefactHash })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return row is null
            ? DomainError.Of(ModuleCode.PFC, "NO-VERSION", $"Version {number} of '{product}' is not a Locked version.")
            : (row.CanonicalJson, row.ArtefactHash);
    }

    private static bool TryVersion(string? text, out ProductVersionNumber number)
    {
        number = default;
        var parts = (text ?? string.Empty).Split('.');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor) || major < 0 || minor < 0)
        {
            return false;
        }

        number = new ProductVersionNumber(major, minor);
        return true;
    }

    private static DomainError Bad(string field, string message) =>
        new(ErrorCode.For(ModuleCode.PFC, "INVALID-REQUEST"), message) { FieldErrors = [new FieldError(field, "PFC-INVALID-REQUEST", "pfc.invalid-request", message)] };
}
