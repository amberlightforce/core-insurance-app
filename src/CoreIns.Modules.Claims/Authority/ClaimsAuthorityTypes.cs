using CoreIns.Platform;
using CoreIns.Platform.Authority;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Claims.Authority;

/// <summary>
/// The claims authority types CLM registers with the platform (CD-05, <c>plt.AuthorityType.register</c> at build time;
/// PRD-07 authority table: <c>CLM.RESERVE</c>, <c>CLM.PAYMENT</c>). The slice checks the amount of one financial
/// transaction (D-SL2-03); <c>costType</c> is declared so limits per cost type can be configured later. The grants
/// themselves are configuration (<c>Platform:Authority:Grants</c>), illustrative in the claims slice.
/// </summary>
public static class ClaimsAuthorityTypes
{
    /// <summary>Reserve change (amount of the reserve transaction, EUR in the slice).</summary>
    public static AuthorityTypeCode Reserve { get; } = AuthorityTypeCode.Parse("CLM.RESERVE");

    /// <summary>Claim payment (amount of the payment transaction, EUR in the slice).</summary>
    public static AuthorityTypeCode Payment { get; } = AuthorityTypeCode.Parse("CLM.PAYMENT");

    /// <summary>The money dimension.</summary>
    public const string AmountDimension = "amount";

    /// <summary>The cost-type code dimension (Indemnity, ExpenseAllocated, … — D-SL2-04).</summary>
    public const string CostTypeDimension = "costType";

    /// <summary>The definitions.</summary>
    public static IReadOnlyList<AuthorityTypeDefinition> Definitions { get; } =
    [
        new AuthorityTypeDefinition(
            Reserve, ModuleCode.CLM, new LocalizedText("Μεταβολή αποθέματος ζημιάς", "Claim reserve change"),
            [new AuthorityDimensionDefinition(AmountDimension, DimensionKind.Money), new AuthorityDimensionDefinition(CostTypeDimension, DimensionKind.Code)]),
        new AuthorityTypeDefinition(
            Payment, ModuleCode.CLM, new LocalizedText("Πληρωμή ζημιάς", "Claim payment"),
            [new AuthorityDimensionDefinition(AmountDimension, DimensionKind.Money), new AuthorityDimensionDefinition(CostTypeDimension, DimensionKind.Code)]),
    ];

    /// <summary>Registers the claims authority types.</summary>
    public static IServiceCollection AddClaimsAuthorityTypes(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        foreach (var definition in Definitions)
        {
            services.AddAuthorityType(definition);
        }

        return services;
    }
}
