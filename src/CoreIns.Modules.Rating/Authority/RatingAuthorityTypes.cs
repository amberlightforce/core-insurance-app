using CoreIns.Platform;
using CoreIns.Platform.Authority;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Rating.Authority;

/// <summary>
/// The authority type and PLT approval type of a rating-artefact release (BO-RAT-06, PRD-03 §12). Registering a type grants nobody
/// anything: the grants are configuration (<c>Platform:Authority:Grants</c> in <c>permissions/rat.json</c>), illustrative and dropped in
/// Production (D-SL2-03). The PRD's <c>RAT.RateArtifactApproval</c> and <c>RAT.RateActivation</c> are one decision here: the checker's
/// approval of a submitted artefact also sets its activation date, so one authority <c>RAT.RELEASE</c> covers both.
/// </summary>
public static class RatingAuthorityTypes
{
    /// <summary>Release sign-off of a rating artefact (dimensions product code and jurisdiction).</summary>
    public static AuthorityTypeCode Release { get; } = AuthorityTypeCode.Parse("RAT.RELEASE");

    /// <summary>The product code dimension.</summary>
    public const string ProductDimension = "productCode";

    /// <summary>The jurisdiction code dimension.</summary>
    public const string JurisdictionDimension = "jurisdiction";

    /// <summary>The PLT approval type of a release.</summary>
    public const string ReleaseApprovalType = "RAT.Release";

    /// <summary>The subject type of a release approval.</summary>
    public const string ReleaseSubjectType = "RatingRelease";

    /// <summary>The role whose inbox receives a release approval (illustrative).</summary>
    public const string CheckerRole = "Platform.DesignAuthority";

    /// <summary>The definitions.</summary>
    public static IReadOnlyList<AuthorityTypeDefinition> Definitions { get; } =
    [
        new AuthorityTypeDefinition(
            Release, ModuleCode.RAT, new LocalizedText("Έγκριση κυκλοφορίας πακέτου τιμολόγησης", "Rating artefact release sign-off"),
            [
                new AuthorityDimensionDefinition(ProductDimension, DimensionKind.Code),
                new AuthorityDimensionDefinition(JurisdictionDimension, DimensionKind.Code),
            ]),
    ];

    /// <summary>Registers the rating authority types.</summary>
    public static IServiceCollection AddRatingAuthorityTypes(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        foreach (var definition in Definitions)
        {
            services.AddAuthorityType(definition);
        }

        return services;
    }
}
