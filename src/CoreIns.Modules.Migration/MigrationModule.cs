using CoreIns.Modules.Migration.Services.Dsar;
using CoreIns.SharedKernel.Dsar;
using CoreIns.Modules.Migration.Authority;
using CoreIns.Modules.Migration.Commands;
using CoreIns.Modules.Migration.Contracts;
using CoreIns.Modules.Migration.Domain;
using CoreIns.Modules.Migration.Persistence;
using CoreIns.Modules.Migration.Services;
using CoreIns.Platform;
using CoreIns.Platform.Approvals;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel.Identifiers;
using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Migration;

/// <summary>
/// Composition entry point of the Migration module (PRD-16). SL6-MIG-FRAME builds the framework: xref registry, batches with
/// dry run and idempotent import through <c>IMigrationImporter</c>, maker-checker approval of the real run, reverse through
/// <c>IMigrationReverser</c> and coexistence routing. Modules register their importer and reverser as scoped services.
/// </summary>
public static class MigrationModule
{
    /// <summary>PostgreSQL schema owned by this module. No other module reads it.</summary>
    public const string Schema = "mig";

    /// <summary>All PostgreSQL schemas owned by this module, created by the migrate job.</summary>
    public static IReadOnlyList<string> Schemas { get; } = [Schema];

    /// <summary>The module database for the migrate job: SELECT, INSERT, UPDATE for the app role (no DELETE).</summary>
    public static IReadOnlyList<ModuleDatabaseDefinition> Databases { get; } =
        [ModuleDbContextRegistration.Define<MigrationDbContext>(ModuleCode.MIG, Schema)];

    /// <summary>Registers the module's services.</summary>
    public static IServiceCollection AddMigrationModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddModuleDbContext<MigrationDbContext>(Schema);
        services.AddSingleton<IDsarParticipant, MigrationDsarParticipant>();
        services.AddMigrationAuthorityTypes();
        services.AddOwnerDecidedApprovalType(BatchApproval.Type);
        services.AddScoped<BatchRunner>();
        services.AddScoped<MigrationReader>();

        services.AddScoped<IValidator<CreateBatch>, CreateBatchValidator>();
        services.AddCommand<CreateBatch, BatchResponse, CreateBatchHandler>(CommandDescriptor.For("mig.Batch.create"));
        services.AddCommand<StartBatch, BatchResponse, StartBatchHandler>(CommandDescriptor.For("mig.Batch.start"));
        services.AddCommand<ApproveBatch, BatchResponse, ApproveBatchHandler>(CommandDescriptor.For("mig.Batch.approve"));
        services.AddCommand<PauseBatch, BatchResponse, PauseBatchHandler>(CommandDescriptor.For("mig.Batch.pause"));
        services.AddCommand<ResumeBatch, BatchResponse, PauseBatchHandler>(CommandDescriptor.For("mig.Batch.resume"));
        services.AddCommand<ReverseBatch, BatchResponse, ReverseBatchHandler>(CommandDescriptor.For("mig.Batch.reverse"));
        services.AddScoped<IValidator<RegisterXref>, RegisterXrefValidator>();
        services.AddCommand<RegisterXref, XrefView, RegisterXrefHandler>(CommandDescriptor.For("mig.Xref.register"));
        services.AddScoped<IValidator<OverrideRouting>, OverrideRoutingValidator>();
        services.AddCommand<OverrideRouting, RoutingView, OverrideRoutingHandler>(CommandDescriptor.For("mig.Routing.override"));

        services.AddScoped<IMigrationXrefService, MigrationXrefService>();
        services.AddScoped<IMigrationRoutingService, MigrationRoutingService>();

        services.AddErrorDefinitions(MigrationErrors.Definitions);
        return services;
    }
}
