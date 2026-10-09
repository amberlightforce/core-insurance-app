using CoreIns.Platform.Approvals;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Authorization;
using CoreIns.Platform.Authority;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Configuration;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Contracts.Api;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.Platform.Numbering;
using CoreIns.Platform.Persistence;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel.Identifiers;
using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CoreIns.Platform;

/// <summary>
/// Composition entry point of the Platform (PRD-14): time service, request context, unit of work, transactional
/// outbox and dispatcher, audit, idempotency, Problem Details, authority framework and configuration runtime.
/// The Host calls <see cref="AddPlatformModule"/> before any business module. Modules then register, from their own
/// <c>Add&lt;X&gt;Module</c>: their DbContext (<c>AddModuleDbContext</c>), commands (<c>AddCommand</c>), event handlers
/// (<c>AddEventHandler</c>), authority types (<see cref="AddAuthorityType"/>) and error definitions
/// (<see cref="AddErrorDefinitions"/>).
/// </summary>
public static class PlatformModule
{
    /// <summary>PostgreSQL schema owned by the platform (outbox, audit, idempotency; configuration, flags and calendars later).</summary>
    public const string Schema = PlatformDbContext.SchemaName;

    /// <summary>All PostgreSQL schemas owned by the platform, created by the migrate job.</summary>
    public static IReadOnlyList<string> Schemas { get; } = [Schema];

    /// <summary>The platform's database for the migrate job: EF Core migrations of <c>plt</c> and the application role's privileges.</summary>
    public static IReadOnlyList<ModuleDatabaseDefinition> Databases { get; } =
    [
        new ModuleDatabaseDefinition(
            ModuleCode.PLT,
            Schema,
            connectionString => ModuleDbContextRegistration.CreateForMigration<PlatformDbContext>(connectionString, Schema),
            PlatformGrants),
    ];

    /// <summary>Registers platform services (every app role).</summary>
    public static IServiceCollection AddPlatformModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<StampOptions>().Bind(configuration.GetSection(StampOptions.Section));
        services.AddOptions<OutboxOptions>().Bind(configuration.GetSection(OutboxOptions.Section));
        services.AddOptions<AuthorityOptions>().Bind(configuration.GetSection(AuthorityOptions.Section));
        services.AddSingleton<IPostConfigureOptions<AuthorityOptions>>(sp => new PostConfigureOptions<AuthorityOptions>(
            Options.DefaultName, options => AuthorityOptions.DropIllustrativeIn(options, sp.GetService<IHostEnvironment>())));

        services.TryAddSingleton<IClock>(sp => configuration[ClockConfiguration.ModeKey] is { Length: > 0 }
            ? ClockConfiguration.Create(configuration, sp.GetRequiredService<IHostEnvironment>(), sp.GetService<IClockOffsetSource>())
            : SystemClock.Instance);

        // Libraries written against the BCL TimeProvider (e.g. data protection key rings) follow IClock too.
        services.TryAddSingleton<TimeProvider>(sp => new ClockTimeProvider(sp.GetRequiredService<IClock>()));

        services.TryAddScoped<RequestContext>();
        services.TryAddScoped<DbSession>();
        services.TryAddScoped<OutboxStaging>();
        services.AddScoped<ITransactionParticipant>(sp => sp.GetRequiredService<OutboxStaging>());
        services.TryAddScoped<AuditStaging>();
        services.AddScoped<ITransactionParticipant>(sp => sp.GetRequiredService<AuditStaging>());
        services.TryAddScoped<IAuditWriter>(sp => sp.GetRequiredService<AuditStaging>());
        services.TryAddScoped<IEventPublisher, EventPublisher>();

        services.TryAddSingleton<EventHandlerRegistry>();
        services.TryAddSingleton<OutboxProcessor>();
        services.TryAddSingleton<OutboxReplayService>();
        services.TryAddSingleton<AuditChainVerifier>();

        services.TryAddSingleton<ErrorCatalog>();
        services.TryAddSingleton<ProblemDetailsMapper>();
        services.AddExceptionHandler<CoreInsExceptionHandler>();

        services.TryAddSingleton<IAuthorityTypeRegistry, AuthorityTypeRegistry>();
        services.TryAddSingleton<IAuthorityService, ConfiguredAuthorityService>();
        services.TryAddSingleton<IConfigurationResolver, InMemoryConfigurationResolver>();

        // Legal entity directory (D-CON-33; MKT registry later), permissions (REQ-PLT-075/079 subset), numbering (REQ-PLT-014).
        services.TryAddSingleton<ILegalEntityDirectory, StampLegalEntityDirectory>();
        services.AddOptions<PermissionOptions>().Bind(configuration.GetSection(PermissionOptions.Section));
        services.TryAddSingleton<IPermissionEvaluator, ConfiguredPermissionEvaluator>();
        services.TryAddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddSingleton<IAuthorizationHandler, PermissionHandler>();
        services.AddOptions<NumberingOptions>().Bind(configuration.GetSection(NumberingOptions.Section))
            .Validate(o => o.Validate().Count == 0, "Platform:Numbering is invalid (see NumberingOptions.Validate).")
            .ValidateOnStart();
        services.TryAddSingleton<INumberFormat, CoreNumberFormat>();
        services.TryAddSingleton<NumberBlockCache>();
        services.TryAddScoped<INumberingService, NumberingService>();
        services.AddErrorDefinitions(
            ErrorDefinition.For(ModuleCode.PLT, NumberingErrors.RangeExhausted, 422, "Η σειρά αρίθμησης εξαντλήθηκε", "The numbering series is exhausted")
                .Describe("Δεν απομένουν αριθμοί στη σειρά· ο διαχειριστής πρέπει να ορίσει νέα σειρά.", "No number is left in the series; an administrator must define a new series."),
            ErrorDefinition.For(ModuleCode.PLT, NumberingErrors.UnknownScheme, 500, "Δεν έχει οριστεί σειρά αρίθμησης", "No numbering series is defined")
                .Describe("Ο τύπος αναγνωριστικού δεν έχει ορισμό σειράς στις ρυθμίσεις της πλατφόρμας.", "The identifier type has no series definition in the platform settings."));
        services.AddApprovals();
        services.AddSupportAuthorityTypes();
        return services;
    }

    /// <summary>
    /// The maker-checker service (REQ-PLT-004, SL2-PLT subset): <c>plt.Approval.request/decide</c> through the command
    /// pipeline, the reads, the in-process contract <see cref="IPlatformApprovalService"/> and the PLT-ERR definitions.
    /// </summary>
    private static IServiceCollection AddApprovals(this IServiceCollection services)
    {
        services.AddScoped<IValidator<RequestApproval>, RequestApprovalValidator>();
        services.AddCommandAuditor<RequestApproval, ApprovalRequestResponse, RequestApprovalAuditor>();
        services.AddCommand<RequestApproval, ApprovalRequestResponse, RequestApprovalHandler>(CommandDescriptor.For("plt.Approval.request"));

        services.AddScoped<IValidator<DecideApproval>, DecideApprovalValidator>();
        services.AddCommandAuditor<DecideApproval, ApprovalDecideResponse, DecideApprovalAuditor>();
        services.AddCommand<DecideApproval, ApprovalDecideResponse, DecideApprovalHandler>(CommandDescriptor.For("plt.Approval.decide"));

        services.TryAddScoped<ApprovalQueries>();
        services.TryAddSingleton<OwnerDecidedApprovalTypes>();
        services.TryAddScoped<IPlatformApprovalService, PlatformApprovalService>();
        services.AddErrorDefinitions(
            ErrorDefinition.For(ModuleCode.PLT, ApprovalErrors.SelfApproval, 403, "Δεν μπορείτε να εγκρίνετε δικό σας αίτημα", "You cannot decide your own request")
                .Describe("Ο συντάκτης ενός αιτήματος δεν μπορεί να το αποφασίσει· απαιτείται άλλος εξουσιοδοτημένος χρήστης (τέσσερα μάτια).", "The maker of a request cannot decide it; another authorised user must (four eyes)."),
            ErrorDefinition.For(ModuleCode.PLT, ApprovalErrors.EditorCannotApprove, 403, "Όποιος επεξεργάστηκε το περιεχόμενο δεν μπορεί να το εγκρίνει", "An editor of the content cannot decide it")
                .Describe("Έχετε υποβάλει προηγούμενη έκδοση του ίδιου περιεχομένου· απαιτείται άλλος εξουσιοδοτημένος χρήστης.", "You submitted an earlier version of the same content; another authorised user must decide."),
            ErrorDefinition.For(ModuleCode.PLT, ApprovalErrors.CheckerMustBeHuman, 422, "Την απόφαση παίρνει μόνο πρόσωπο", "Only a person can decide")
                .Describe("Υπηρεσίες και πράκτορες τεχνητής νοημοσύνης δεν μπορούν να εγκρίνουν ή να απορρίψουν αιτήματα.", "Services and AI agents can never approve or reject a request."),
            ErrorDefinition.For(ModuleCode.PLT, ApprovalErrors.Stale, 409, "Το αίτημα έγκρισης άλλαξε στο μεταξύ", "The approval request changed meanwhile")
                .Describe("Το αίτημα αποφασίστηκε ή αντικαταστάθηκε, ή το περιεχόμενο άλλαξε μετά τον έλεγχό σας. Φορτώστε το ξανά.", "The request was decided or superseded, or its content changed after you reviewed it. Reload it."),
            ErrorDefinition.For(ModuleCode.PLT, ApprovalErrors.HashMismatch, 422, "Το περιεχόμενο διαφέρει από αυτό που εγκρίθηκε", "The content differs from what was approved")
                .Describe("Η εκτέλεση επιτρέπεται μόνο για το περιεχόμενο που εγκρίθηκε (ίδιο αποτύπωμα SHA-256).", "Execution is allowed only for the approved content (same SHA-256 hash)."),
            ErrorDefinition.For(ModuleCode.PLT, ApprovalErrors.OwnerDecided, 409, "Η έγκριση γίνεται στην οθόνη του υπεύθυνου τομέα", "Decided in the owning module")
                .Describe("Αυτός ο τύπος αιτήματος εγκρίνεται ή απορρίπτεται μόνο από την οθόνη του τομέα που τον κατέχει (π.χ. επιστροφές χρημάτων από τη χρέωση), όχι από τα εισερχόμενα εγκρίσεων.", "This request type is approved or rejected only in the screen of the module that owns it (for example refunds in billing), not in the approvals inbox."),
            ErrorDefinition.For(ModuleCode.PLT, ApprovalErrors.SubjectMismatch, 422, "Η έγκριση αφορά άλλο αντικείμενο", "The approval is for another subject")
                .Describe("Το αίτημα έγκρισης είναι άλλου τύπου ή αφορά άλλο αντικείμενο από αυτό που εκτελείται.", "The approval request is of another type or for another subject than the one being executed."));
        return services;
    }

    /// <summary>Runs the outbox dispatcher as a hosted service (worker role only).</summary>
    public static IServiceCollection AddOutboxDispatcher(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHostedService<OutboxDispatcherService>();
        return services;
    }

    /// <summary>Registers an authority type owned by a module (<c>plt.AuthorityType.register</c> at build time).</summary>
    public static IServiceCollection AddAuthorityType(this IServiceCollection services, AuthorityTypeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(definition);
        services.AddSingleton(definition);
        return services;
    }

    /// <summary>Registers a module's error definitions (status, localized title, retryable) for Problem Details.</summary>
    public static IServiceCollection AddErrorDefinitions(this IServiceCollection services, params ErrorDefinition[] definitions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(definitions);
        foreach (var definition in definitions)
        {
            services.AddSingleton(definition);
        }

        return services;
    }

    /// <summary>
    /// Privileges of the application role on <c>plt</c> (run by the migrate job after migrating): read/write on the
    /// operational tables, <b>INSERT and SELECT only</b> on <c>plt.audit_event</c> (D-ARC-15; a trigger also refuses
    /// UPDATE, DELETE and TRUNCATE for every role) and <b>SELECT only</b> on <c>plt.audit_chain_head</c>. The head is
    /// created and locked through the SECURITY DEFINER function <c>plt.audit_chain_lock(date)</c> and advanced only by the
    /// SECURITY DEFINER insert trigger on <c>plt.audit_event</c>, which refuses a row that does not extend the chain.
    /// </summary>
    public static IReadOnlyList<string> PlatformGrants(string appRole) =>
    [
        $"REVOKE ALL ON ALL TABLES IN SCHEMA {Schema} FROM {appRole}",
        $"GRANT SELECT, INSERT, UPDATE, DELETE ON {Schema}.outbox_message, {Schema}.aggregate_sequence, {Schema}.processed_event, "
            + $"{Schema}.outbox_dead_letter, {Schema}.event_archive, {Schema}.idempotency_record TO {appRole}",
        $"GRANT SELECT, INSERT, UPDATE ON {Schema}.number_series, {Schema}.data_key TO {appRole}",

        // Approval requests: no DELETE; a decided request is frozen by trigger (decided once, REQ-PLT-114).
        $"GRANT SELECT, INSERT, UPDATE ON {Schema}.approval_request TO {appRole}",
        // Dev clock offset (D-SL3-12): never deleted (trigger); only read/advanced when the Development dev clock is registered.
        $"GRANT SELECT, UPDATE ON {Schema}.dev_clock TO {appRole}",
        $"GRANT SELECT, INSERT ON {Schema}.audit_event TO {appRole}",
        $"GRANT SELECT ON {Schema}.audit_chain_head TO {appRole}",
        $"REVOKE ALL ON FUNCTION {Schema}.audit_chain_lock(date) FROM PUBLIC",
        $"GRANT EXECUTE ON FUNCTION {Schema}.audit_chain_lock(date) TO {appRole}",
        $"GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA {Schema} TO {appRole}",
    ];
}
