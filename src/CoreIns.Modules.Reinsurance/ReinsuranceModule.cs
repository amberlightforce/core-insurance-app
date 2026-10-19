using CoreIns.Modules.Reinsurance.Authority;
using CoreIns.Modules.Reinsurance.Contracts;
using CoreIns.Modules.Reinsurance.Contracts.Api;
using CoreIns.Modules.Reinsurance.Persistence;
using CoreIns.Modules.Reinsurance.Registry;
using CoreIns.Platform;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel.Identifiers;
using FluentValidation;
using Hangfire;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Reinsurance;

/// <summary>
/// Composition entry point of the Reinsurance module (PRD-08 Reinsurance). The Host calls <see cref="AddReinsuranceModule"/>;
/// the migrate job applies <see cref="Databases"/>. SL4-RI-REGISTRY builds the module foundation and the XoL treaty
/// registry: contract, version, section, layer, clause and participation, the Draft → Submitted → Approved → Active →
/// Expired lifecycle with a maker-checker approval through PLT bound to the content hash, activation and expiry at the
/// period boundaries (Athens), and the registry queries. PTY is reached only through its generated contract (D-ARC-16).
/// </summary>
public static class ReinsuranceModule
{
    /// <summary>PostgreSQL schema owned by this module. No other module reads it.</summary>
    public const string Schema = "ri";

    /// <summary>The gapless numbering series of reinsurance contracts per legal entity (REQ-RI-030); configured in <c>Platform:Numbering:Schemes</c>.</summary>
    public const string ContractSeries = "RI_CONTRACT";

    /// <summary>All PostgreSQL schemas owned by this module, created by the migrate job.</summary>
    public static IReadOnlyList<string> Schemas { get; } = [Schema];

    /// <summary>
    /// The module database for the migrate job. Least privilege per table: the contract header and the version move
    /// (SELECT, INSERT, UPDATE; triggers freeze identity and an approved version); the section, layer, clause and
    /// participation rows are append-only (SELECT, INSERT). Nothing gets DELETE (REQ-RI-065).
    /// </summary>
    public static IReadOnlyList<ModuleDatabaseDefinition> Databases { get; } =
    [
        new(
            ModuleCode.RI,
            Schema,
            connectionString => ModuleDbContextRegistration.CreateForMigration<ReinsuranceDbContext>(connectionString, Schema),
            appRole =>
            [
                $"REVOKE ALL ON ALL TABLES IN SCHEMA {Schema} FROM {appRole}",
                $"GRANT SELECT, INSERT, UPDATE ON {Schema}.contract, {Schema}.contract_version TO {appRole}",
                $"GRANT SELECT, INSERT ON {Schema}.section, {Schema}.layer, {Schema}.clause, {Schema}.participation TO {appRole}",
            ]),
    ];

    /// <summary>Registers the module's services: DbContext, commands, queries, in-process contract, scanner, error definitions.</summary>
    public static IServiceCollection AddReinsuranceModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddReinsuranceAuthorityTypes();

        services.AddOptions<ReinsuranceOptions>().Bind(configuration.GetSection(ReinsuranceOptions.Section)).ValidateDataAnnotations().ValidateOnStart();
        services.AddModuleDbContext<ReinsuranceDbContext>(Schema);

        services.AddScoped<PartyDirectory>();
        services.AddScoped<ContractLifecycleService>();
        services.AddScoped<ContractReader>();

        services.AddScoped<IValidator<CreateContract>, CreateContractValidator>();
        services.AddCommandAuditor<CreateContract, ContractCreateResponse, CreateContractAuditor>();
        services.AddCommand<CreateContract, ContractCreateResponse, CreateContractHandler>(
            CommandDescriptor.For("ri.Contract.create") with { SupportsDryRun = true });
        services.AddScoped<IValidator<UpdateContract>, UpdateContractValidator>();
        services.AddCommandAuditor<UpdateContract, ContractUpdateResponse, UpdateContractAuditor>();
        services.AddCommand<UpdateContract, ContractUpdateResponse, UpdateContractHandler>(
            CommandDescriptor.For("ri.Contract.update") with { SupportsDryRun = true });
        services.AddScoped<IValidator<SubmitContract>, SubmitContractValidator>();
        services.AddCommandAuditor<SubmitContract, ContractSubmitResponse, SubmitContractAuditor>();
        services.AddCommand<SubmitContract, ContractSubmitResponse, SubmitContractHandler>(
            CommandDescriptor.For("ri.Contract.submit") with { SupportsDryRun = true });
        services.AddScoped<IValidator<ApproveContract>, ApproveContractValidator>();
        services.AddCommandAuditor<ApproveContract, ContractApproveResponse, ApproveContractAuditor>();
        services.AddCommand<ApproveContract, ContractApproveResponse, ApproveContractHandler>(
            CommandDescriptor.For("ri.Contract.approve") with { SupportsDryRun = true });

        // The scanner's per-contract command (internal, no HTTP; exactly-once by the row lock and the state it finds).
        services.AddCommandAuditor<ApplyDueLifecycle, string, ApplyDueLifecycleAuditor>();
        services.AddCommand<ApplyDueLifecycle, string, ApplyDueLifecycleHandler>(
            CommandDescriptor.For("ri.Contract.applyDueLifecycle") with { RequiresIdempotencyKey = false, Idempotent = false });
        services.AddScoped<LifecycleScanner>();
        services.AddScoped<ReinsuranceLifecycleJob>();
        if (string.Equals(configuration["APP_ROLE"], "worker", StringComparison.OrdinalIgnoreCase))
        {
            services.AddHostedService<LifecycleJobRegistrar>();
        }

        // In-process contract other modules call (D-ARC-16).
        services.AddScoped<IReinsuranceContractService, ReinsuranceContractService>();

        services.AddErrorDefinitions(Errors);
        return services;
    }

    /// <summary>Status, bilingual title and description of every RI-ERR code the module raises (RFC 9457, D-API-15).</summary>
    internal static ErrorDefinition[] Errors { get; } =
    [
        ErrorDefinition.For(ModuleCode.RI, "NOT-FOUND", 404, "Δεν βρέθηκε", "Not found")
            .Describe("Η σύμβαση δεν υπάρχει στη νομική σας οντότητα.", "The contract does not exist in your legal entity."),
        ErrorDefinition.For(ModuleCode.RI, "STALE", 409, "Η σύμβαση άλλαξε στο μεταξύ", "The contract changed meanwhile", retryable: true)
            .Describe("Κάποιος άλλος άλλαξε τη σύμβαση. Φορτώστε τη νεότερη έκδοση και επαναλάβετε.", "Someone else changed the contract. Load the newer version and try again."),
        ErrorDefinition.For(ModuleCode.RI, "STATE", 422, "Η ενέργεια δεν επιτρέπεται σε αυτή την κατάσταση", "The action is not allowed in this state")
            .Describe("Η κατάσταση της σύμβασης δεν επιτρέπει αυτή την ενέργεια.", "The contract's status does not allow this action."),
        ErrorDefinition.For(ModuleCode.RI, "SOD", 403, "Διαχωρισμός καθηκόντων", "Separation of duties")
            .Describe("Όποιος καταχώρισε, επεξεργάστηκε ή υπέβαλε τη σύμβαση δεν μπορεί να την εγκρίνει.", "Whoever entered, edited or submitted the contract cannot approve it."),
        ErrorDefinition.For(ModuleCode.RI, "SIGNED-LINES", 422, "Τα υπογεγραμμένα μερίδια δεν είναι έγκυρα", "The signed lines are not valid")
            .Describe("Το άθροισμα των υπογεγραμμένων μεριδίων πρέπει να ισούται με το τοποθετημένο ποσοστό, με ακριβώς έναν ηγέτη.", "Signed lines must add up to the placed share, with exactly one lead."),
        ErrorDefinition.For(ModuleCode.RI, "REINSURER-ID", 422, "Μη έγκυρος αντασφαλιστής ή μεσίτης", "The reinsurer or broker is not valid")
            .Describe("Ο αντασφαλιστής και ο μεσίτης πρέπει να είναι ενεργά νομικά πρόσωπα του μητρώου μερών.", "The reinsurer and the broker must be usable organisation parties."),
        ErrorDefinition.For(ModuleCode.RI, "NOT-AVAILABLE", 501, "Η λειτουργία δεν είναι ακόμη διαθέσιμη", "The operation is not available yet")
            .Describe("Η λειτουργία ανήκει σε επόμενο πακέτο εργασιών.", "The operation belongs to a later work package."),
    ];
}

/// <summary>
/// Registers the recurring lifecycle scan with Hangfire when the process runs as the worker (D-ARC-04): activation at the
/// period start and expiry at the period end, every <see cref="ReinsuranceOptions.ScannerIntervalMinutes"/> minutes.
/// </summary>
internal sealed partial class LifecycleJobRegistrar(IRecurringJobManager jobs, IOptions<ReinsuranceOptions> options, ILogger<LifecycleJobRegistrar> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var minutes = options.Value.ScannerIntervalMinutes;
            jobs.AddOrUpdate<ReinsuranceLifecycleJob>(
                ReinsuranceLifecycleJob.JobId, job => job.RunAsync(CancellationToken.None), $"*/{minutes} * * * *");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogRegistrationFailed(logger, ex);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Error, Message = "The reinsurance lifecycle job could not be registered with Hangfire.")]
    private static partial void LogRegistrationFailed(ILogger logger, Exception exception);
}
