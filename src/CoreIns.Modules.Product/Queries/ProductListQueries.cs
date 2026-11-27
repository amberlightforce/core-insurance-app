using System.Globalization;
using CoreIns.Modules.Product.Contracts.Api;
using CoreIns.Platform.Context;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Dapper;

namespace CoreIns.Modules.Product.Queries;

/// <summary>
/// <c>pfc.Product.list</c> (NX-UI-14b): the products of the caller's legal entity with every version. A plain read of
/// <c>pfc.product</c> and <c>pfc.product_version</c>, scoped to the caller's legal entity; no artefact content.
/// </summary>
internal sealed class ProductListQueries(DbSession session, RequestContext context, ILegalEntityDirectory legalEntities)
{
    public async Task<ProductListPage> ListAsync(CancellationToken cancellationToken)
    {
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var products = (await connection.QueryAsync<ProductRecord>(new CommandDefinition(
            """
            SELECT product_id AS ProductId, jurisdiction AS Jurisdiction, code AS Code, line_code AS LineCode, line_family AS LineFamily,
                   name_el AS NameEl, name_en AS NameEn, product_type AS ProductType, customer_type AS CustomerType
              FROM pfc.product WHERE legal_entity_id = @le ORDER BY code
            """, new { le = legalEntity.Value }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();
        var versions = (await connection.QueryAsync<VersionRecord>(new CommandDefinition(
            """
            SELECT v.product_id AS ProductId, v.major AS Major, v.minor AS Minor, v.status AS Status, v.is_abstract AS IsAbstract,
                   v.channels AS Channels, v.contract_currency AS ContractCurrency,
                   v.new_business_from AS NewBusinessFrom, v.new_business_to AS NewBusinessTo,
                   v.renewal_from AS RenewalFrom, v.renewal_to AS RenewalTo, v.artefact_hash AS ArtefactHash,
                   f.major AS FallbackMajor, f.minor AS FallbackMinor, r.major AS ReplacesMajor, r.minor AS ReplacesMinor
              FROM pfc.product_version v
              LEFT JOIN pfc.product_version f ON f.product_version_id = v.fallback_of_version_id
              LEFT JOIN pfc.product_version r ON r.product_version_id = v.replaces_version_id
             WHERE v.legal_entity_id = @le
             ORDER BY v.major DESC, v.minor DESC
            """, new { le = legalEntity.Value }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        var items = products.Select(p => new ProductListItem
        {
            Code = p.Code,
            Jurisdiction = p.Jurisdiction,
            LineCode = p.LineCode,
            LineFamily = p.LineFamily,
            Name = new CoreIns.Modules.Product.Contracts.Api.LocalizedText { El = p.NameEl, En = p.NameEn },
            ProductType = p.ProductType,
            CustomerType = p.CustomerType,
            Versions = versions.Where(v => v.ProductId == p.ProductId).Select(v => new ProductListVersion
            {
                Version = new ProductVersionNumber(v.Major, v.Minor),
                Status = v.Status,
                IsAbstract = v.IsAbstract,
                Channels = v.Channels ?? [],
                ContractCurrency = Currency.FromCode(v.ContractCurrency),
                NewBusinessFrom = Day(v.NewBusinessFrom),
                NewBusinessTo = v.NewBusinessTo is { } nbt ? Day(nbt) : null,
                RenewalFrom = Day(v.RenewalFrom),
                RenewalTo = v.RenewalTo is { } rt ? Day(rt) : null,
                ArtefactHash = Sha256Hash.Parse(v.ArtefactHash),
                FallbackOfVersion = v.FallbackMajor is { } fm ? new ProductVersionNumber(fm, v.FallbackMinor ?? 0) : null,
                ReplacesVersion = v.ReplacesMajor is { } rm ? new ProductVersionNumber(rm, v.ReplacesMinor ?? 0) : null,
            }).ToList(),
        }).ToList();
        return new ProductListPage { Items = items, NextCursor = null };
    }

    private static string Day(DateTime value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private sealed class ProductRecord
    {
        public Guid ProductId { get; set; }

        public string Jurisdiction { get; set; } = string.Empty;

        public string Code { get; set; } = string.Empty;

        public string LineCode { get; set; } = string.Empty;

        public string LineFamily { get; set; } = string.Empty;

        public string NameEl { get; set; } = string.Empty;

        public string NameEn { get; set; } = string.Empty;

        public string ProductType { get; set; } = string.Empty;

        public string CustomerType { get; set; } = string.Empty;
    }

    private sealed class VersionRecord
    {
        public Guid ProductId { get; set; }

        public int Major { get; set; }

        public int Minor { get; set; }

        public string Status { get; set; } = string.Empty;

        public bool IsAbstract { get; set; }

        public string[]? Channels { get; set; }

        public string ContractCurrency { get; set; } = string.Empty;

        public DateTime NewBusinessFrom { get; set; }

        public DateTime? NewBusinessTo { get; set; }

        public DateTime RenewalFrom { get; set; }

        public DateTime? RenewalTo { get; set; }

        public string ArtefactHash { get; set; } = string.Empty;

        public int? FallbackMajor { get; set; }

        public int? FallbackMinor { get; set; }

        public int? ReplacesMajor { get; set; }

        public int? ReplacesMinor { get; set; }
    }
}
