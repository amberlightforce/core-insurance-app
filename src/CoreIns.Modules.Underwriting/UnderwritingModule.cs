using CoreIns.Modules.Underwriting.Commands;
using CoreIns.Modules.Underwriting.Contracts;
using CoreIns.Modules.Underwriting.Contracts.Api;
using CoreIns.Modules.Underwriting.Persistence;
using CoreIns.Modules.Underwriting.Queries;
using CoreIns.Modules.Underwriting.Services;
using CoreIns.Platform;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel.Identifiers;
using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Underwriting;

/// <summary>
/// Composition entry point of the Underwriting module (PRD-04). SL-RAT-UW slice: the evaluation runtime on the shared
/// rule engine (illustrative rule set), the decision record and a minimal issue lifecycle (Open, Closed). The referral
/// workbench, decide/approve, declines with refusal documents and authoring screens are later work packages.
/// </summary>
public static class UnderwritingModule
{
    /// <summary>PostgreSQL schema owned by this module. No other module reads it.</summary>
    public const string Schema = "uw";

    /// <summary>All PostgreSQL schemas owned by this module, created by the migrate job.</summary>
    public static IReadOnlyList<string> Schemas { get; } = [Schema];

    /// <summary>The module database for the migrate job: EF Core migrations of <c>uw</c>, then SELECT/INSERT/UPDATE for the app role (no DELETE).</summary>
    public static IReadOnlyList<ModuleDatabaseDefinition> Databases { get; } =
        [ModuleDbContextRegistration.Define<UnderwritingDbContext>(ModuleCode.UW, Schema)];

    /// <summary>Registers the module's services: DbContext, store, command, in-process contracts, error definitions.</summary>
    public static IServiceCollection AddUnderwritingModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<UnderwritingOptions>().Bind(configuration.GetSection(UnderwritingOptions.Section));
        services.AddModuleDbContext<UnderwritingDbContext>(Schema);
        services.AddScoped<UnderwritingStore>();

        services.AddScoped<IValidator<EvaluateRules>, EvaluateRulesValidator>();
        services.AddCommandAuditor<EvaluateRules, RulesEvaluateResponse, EvaluateRulesAuditor>();
        services.AddCommand<EvaluateRules, RulesEvaluateResponse, EvaluateRulesHandler>(CommandDescriptor.For("uw.Rules.evaluate") with { SupportsDryRun = true });

        services.AddScoped<IUnderwritingRulesService, UnderwritingRulesService>();
        services.AddScoped<IUnderwritingIssueService, UnderwritingIssueService>();

        services.AddErrorDefinitions(Errors);
        return services;
    }

    /// <summary>Status, bilingual title and description of every UW-ERR code the module raises (RFC 9457, D-API-15).</summary>
    internal static ErrorDefinition[] Errors { get; } =
    [
        ErrorDefinition.For(ModuleCode.UW, "RULESET-UNRESOLVED", 422, "Δεν βρέθηκε ενεργό σύνολο κανόνων αναδοχής", "No underwriting rule set is active")
            .Describe("Δεν υπάρχει ενεργό σύνολο κανόνων για το προϊόν και την ημερομηνία.", "There is no active rule set for the product and date."),
        ErrorDefinition.For(ModuleCode.UW, "SNAPSHOT", 422, "Το στιγμιότυπο κινδύνου δεν είναι έγκυρο", "The risk snapshot is not valid")
            .Describe("Λείπουν στοιχεία κινδύνου ή έχουν λάθος μορφή.", "Risk data is missing or malformed."),
        ErrorDefinition.For(ModuleCode.UW, "EVAL-UNAVAILABLE", 503, "Η αξιολόγηση κανόνων δεν είναι διαθέσιμη", "Rule evaluation is unavailable", retryable: true)
            .Describe("Ο υπολογισμός των κανόνων απέτυχε· δοκιμάστε ξανά.", "Rule evaluation failed; try again."),
        ErrorDefinition.For(ModuleCode.UW, "NOT-AVAILABLE", 501, "Η λειτουργία δεν είναι ακόμη διαθέσιμη", "The operation is not available yet")
            .Describe("Η λειτουργία ανήκει σε επόμενο πακέτο εργασιών.", "The operation belongs to a later work package."),
    ];
}
