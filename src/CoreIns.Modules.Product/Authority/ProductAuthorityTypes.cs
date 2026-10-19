using CoreIns.Platform;
using CoreIns.Platform.Authority;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Product.Authority;

/// <summary>
/// The authority types PFC registers with the platform (PRD-02 §12.2). Registering a type grants nobody anything: the grants
/// are configuration (<c>Platform:Authority:Grants</c> in <c>permissions/pfc.json</c>), illustrative and dropped in Production (D-SL2-03).
/// The PRD names the type <c>PFC.EmergencyChange</c>; the platform's authority-type code format is upper case, so it is written
/// <c>PFC.EMERGENCY_CHANGE</c> (as <c>BIL.REFUND</c> for BIL.Refund).
/// </summary>
public static class ProductAuthorityTypes
{
    /// <summary>Emergency change and fall-back (PRD-02 §12.2: dimensions product line and jurisdiction).</summary>
    public static AuthorityTypeCode EmergencyChange { get; } = AuthorityTypeCode.Parse("PFC.EMERGENCY_CHANGE");

    /// <summary>The product line code dimension.</summary>
    public const string ProductLineDimension = "productLine";

    /// <summary>The jurisdiction code dimension.</summary>
    public const string JurisdictionDimension = "jurisdiction";

    /// <summary>The PLT approval type of a fall-back request (D-SL5-09).</summary>
    public const string FallbackApprovalType = "PFC.Fallback";

    /// <summary>The role whose inbox receives a fall-back approval (D-SL5-08, illustrative).</summary>
    public const string CheckerRole = "Platform.DesignAuthority";

    /// <summary>The subject type of a fall-back approval.</summary>
    public const string FallbackSubjectType = "ProductFallback";

    /// <summary>The definitions.</summary>
    public static IReadOnlyList<AuthorityTypeDefinition> Definitions { get; } =
    [
        new AuthorityTypeDefinition(
            EmergencyChange, ModuleCode.PFC, new LocalizedText("Επείγουσα αλλαγή προϊόντος και επαναφορά", "Product emergency change and fall-back"),
            [
                new AuthorityDimensionDefinition(ProductLineDimension, DimensionKind.Code),
                new AuthorityDimensionDefinition(JurisdictionDimension, DimensionKind.Code),
            ]),
    ];

    /// <summary>Registers the product authority types.</summary>
    public static IServiceCollection AddProductAuthorityTypes(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        foreach (var definition in Definitions)
        {
            services.AddAuthorityType(definition);
        }

        return services;
    }
}
