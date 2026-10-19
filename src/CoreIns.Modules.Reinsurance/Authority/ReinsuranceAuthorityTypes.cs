using CoreIns.Platform;
using CoreIns.Platform.Authority;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Reinsurance.Authority;

/// <summary>
/// The reinsurance authority types RI registers with the platform (CD-05; PRD-08 §11). <c>RI.CONTRACT_APPROVE</c> is the
/// authority a checker must hold to approve (or return) a reinsurance treaty; the approval request carries the contract
/// type as its code dimension. The grants themselves are configuration (<c>Platform:Authority:Grants</c> in
/// <c>permissions/ri.json</c>), illustrative in the slice.
/// </summary>
public static class ReinsuranceAuthorityTypes
{
    /// <summary>Approve or return a reinsurance contract (the maker-checker of REQ-RI-057).</summary>
    public static AuthorityTypeCode ContractApprove { get; } = AuthorityTypeCode.Parse("RI.CONTRACT_APPROVE");

    /// <summary>The contract-type code dimension.</summary>
    public const string ContractTypeDimension = "contractType";

    /// <summary>The definitions.</summary>
    public static IReadOnlyList<AuthorityTypeDefinition> Definitions { get; } =
    [
        new AuthorityTypeDefinition(
            ContractApprove, ModuleCode.RI, new LocalizedText("Έγκριση αντασφαλιστικής σύμβασης", "Approve reinsurance contract"),
            [new AuthorityDimensionDefinition(ContractTypeDimension, DimensionKind.Code)]),
    ];

    /// <summary>Registers the reinsurance authority types.</summary>
    public static IServiceCollection AddReinsuranceAuthorityTypes(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        foreach (var definition in Definitions)
        {
            services.AddAuthorityType(definition);
        }

        return services;
    }
}
