using CoreIns.Modules.Migration.Domain;
using CoreIns.Modules.Migration.Services.Mapping;
using CoreIns.Modules.Migration.Services.SignOff;
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
        services.AddAuthorityType(new AuthorityTypeDefinition(
            AuthorityTypeCode.Parse(SignOffApproval.Type), ModuleCode.MIG, new LocalizedText("Έγκριση ολοκλήρωσης κύματος μετάπτωσης", "Sign off a migration wave"),
            [new AuthorityDimensionDefinition(SignOffApproval.SignatoryDimension, DimensionKind.Code)]));
        services.AddAuthorityType(new AuthorityTypeDefinition(
            AuthorityTypeCode.Parse(MappingApproval.Type), ModuleCode.MIG, new LocalizedText("Έγκριση προδιαγραφής αντιστοίχισης", "Approve a mapping spec"),
            [new AuthorityDimensionDefinition(MappingApproval.ScopeDimension, DimensionKind.Code)]));
        return services;
    }
}
