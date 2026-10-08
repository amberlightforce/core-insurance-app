using CoreIns.Platform.Authority;
using CoreIns.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Underwriting.Authority;

/// <summary>
/// The underwriting authority types UW registers with the platform (CD-05, REQ-UW-105; PRD-04 §12.2). This slice registers
/// <c>UW.ISSUE_APPROVAL</c> ("every issue decision") with its issue-type dimension (comparison IN_SET, REQ-UW-106); the
/// product, line, territory and transaction-type dimensions and the other twelve types come with the full workbench. Grants
/// are configuration (<c>Platform:Authority:Grants</c>), illustrative in development.
/// </summary>
public static class UnderwritingAuthorityTypes
{
    /// <summary>Approve or reject an underwriting issue.</summary>
    public static AuthorityTypeCode IssueApproval { get; } = AuthorityTypeCode.Parse("UW.ISSUE_APPROVAL");

    /// <summary>The issue-type code dimension.</summary>
    public const string IssueTypeDimension = "issueType";

    /// <summary>The definitions.</summary>
    public static IReadOnlyList<AuthorityTypeDefinition> Definitions { get; } =
    [
        new AuthorityTypeDefinition(
            IssueApproval, ModuleCode.UW, new LocalizedText("Έγκριση ζητήματος ανάληψης", "Approve underwriting issue"),
            [new AuthorityDimensionDefinition(IssueTypeDimension, DimensionKind.Code)]),
    ];

    /// <summary>Registers the underwriting authority types.</summary>
    public static IServiceCollection AddUnderwritingAuthorityTypes(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        foreach (var definition in Definitions)
        {
            services.AddAuthorityType(definition);
        }

        return services;
    }
}
