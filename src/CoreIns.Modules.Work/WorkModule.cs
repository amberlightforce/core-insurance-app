using CoreIns.Modules.Market.Contracts.Events;
using CoreIns.Modules.Work.Commands;
using CoreIns.Modules.Work.Contracts;
using CoreIns.Modules.Work.Contracts.Api;
using CoreIns.Modules.Work.Domain;
using CoreIns.Modules.Work.Events;
using CoreIns.Modules.Work.Jobs;
using CoreIns.Modules.Work.Persistence;
using CoreIns.Modules.Work.Queries;
using CoreIns.Modules.Work.Services;
using CoreIns.Modules.Work.Services.Dsar;
using CoreIns.Platform;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel.Dsar;
using CoreIns.SharedKernel.Identifiers;
using FluentValidation;
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

    /// <summary>
    /// Registers the module's services: its DbContext, the activity core (create, list, get, assign, complete, skip, cancel, getNext), the
    /// pack-rollback review consumer, the SLA sweep (worker role) and the DSAR participant.
    /// </summary>
    public static IServiceCollection AddWorkModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddModuleDbContext<WorkDbContext>(Schema);
        services.AddSingleton<IDsarParticipant, WorkDsarParticipant>();

        services.AddScoped<ActivityEngine>();
        services.AddScoped<ActivityReader>();
        services.AddScoped<IValidator<CreateActivity>, CreateActivityValidator>();
        services.AddCommandAuditor<CreateActivity, ActivityCreateResponse, CreateActivityAuditor>();
        services.AddCommand<CreateActivity, ActivityCreateResponse, CreateActivityHandler>(CommandDescriptor.For("wrk.Activity.create"));
        services.AddScoped<IValidator<AssignActivity>, AssignActivityValidator>();
        services.AddCommandAuditor<AssignActivity, ActivityAssignResponse, AssignActivityAuditor>();
        services.AddCommand<AssignActivity, ActivityAssignResponse, AssignActivityHandler>(CommandDescriptor.For("wrk.Activity.assign"));
        services.AddScoped<IValidator<CompleteActivity>, CompleteActivityValidator>();
        services.AddCommandAuditor<CompleteActivity, ActivityCompleteResponse, CompleteActivityAuditor>();
        services.AddCommand<CompleteActivity, ActivityCompleteResponse, CompleteActivityHandler>(CommandDescriptor.For("wrk.Activity.complete"));
        services.AddScoped<IValidator<SkipActivity>, SkipActivityValidator>();
        services.AddCommandAuditor<SkipActivity, ActivitySkipResponse, SkipActivityAuditor>();
        services.AddCommand<SkipActivity, ActivitySkipResponse, SkipActivityHandler>(CommandDescriptor.For("wrk.Activity.skip"));
        services.AddScoped<IValidator<CancelActivity>, CancelActivityValidator>();
        services.AddCommandAuditor<CancelActivity, ActivityCancelResponse, CancelActivityAuditor>();
        services.AddCommand<CancelActivity, ActivityCancelResponse, CancelActivityHandler>(CommandDescriptor.For("wrk.Activity.cancel"));
        services.AddCommandAuditor<GetNextActivity, QueueGetNextResponse, GetNextActivityAuditor>();
        services.AddCommand<GetNextActivity, QueueGetNextResponse, GetNextActivityHandler>(CommandDescriptor.For("wrk.Queue.getNext"));
        services.AddScoped<IWorkActivityService, WorkActivityService>();

        // D-SL5-10: a pack rollback gets a review activity in the rollback queue.
        services.AddEventHandler<PackRolledBackV1, PackRolledBackReviewHandler>(
            EventDescriptor.From(PackRolledBackV1.Descriptor), PackRolledBackReviewHandler.Name, ModuleCode.WRK);

        // SLABreached: the sweep marks and publishes once per activity (worker role only).
        services.AddOptions<WorkOptions>().Bind(configuration.GetSection(WorkOptions.Section));
        services.AddSingleton<SlaSweepRunner>();
        if (string.Equals(configuration["APP_ROLE"], "worker", StringComparison.OrdinalIgnoreCase))
        {
            services.AddHostedService<SlaSweeper>();
        }

        services.AddErrorDefinitions(WorkErrors.Definitions);
        return services;
    }
}
