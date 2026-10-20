using CoreIns.Modules.Work.Persistence;
using CoreIns.Modules.Work.Services.Dsar;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel.Dsar;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Work;

/// <summary>
/// Composition entry point of the Work module (PRD-13 Work management).
/// The Host calls <see cref="AddWorkModule"/>; the module registers its own services here.
/// </summary>
public static class WorkModule
{
    /// <summary>PostgreSQL schema owned by this module. No other module reads it.</summary>
    public const string Schema = "wrk";

    /// <summary>All PostgreSQL schemas owned by this module, created by the migrate job.</summary>
    public static IReadOnlyList<string> Schemas { get; } = [Schema];

    /// <summary>The module database for the migrate job: SELECT, INSERT, UPDATE for the app role (no DELETE).</summary>
    public static IReadOnlyList<ModuleDatabaseDefinition> Databases { get; } =
        [ModuleDbContextRegistration.Define<WorkDbContext>(ModuleCode.WRK, Schema)];

    /// <summary>Registers the module's services: its DbContext and DSAR participant (the task core follows in NX-HD-05).</summary>
    public static IServiceCollection AddWorkModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddModuleDbContext<WorkDbContext>(Schema);
        services.AddSingleton<IDsarParticipant, WorkDsarParticipant>();
        return services;
    }
}
