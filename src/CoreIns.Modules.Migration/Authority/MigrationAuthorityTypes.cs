using CoreIns.Modules.Migration.Domain;
using CoreIns.Platform;
using CoreIns.Platform.Authority;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Migration.Authority;

/// <summary>
/// <c>MIG.BATCH_RUN</c>: the authority a checker must hold to approve the real run of a migration batch (MIG-005). Grants are
/// configuration (<c>permissions/mig.json</c>), illustrative.
/// </summary>
internal static class MigrationAuthorityTypes
{
    public static AuthorityTypeCode BatchRun { get; } = AuthorityTypeCode.Parse(BatchApproval.Type);

    public static IServiceCollection AddMigrationAuthorityTypes(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddAuthorityType(new AuthorityTypeDefinition(
            BatchRun, ModuleCode.MIG, new LocalizedText("Έγκριση εκτέλεσης δέσμης μετάπτωσης", "Approve migration batch run"),
            [new AuthorityDimensionDefinition(BatchApproval.ScopeDimension, DimensionKind.Code)]));
        return services;
    }
}
