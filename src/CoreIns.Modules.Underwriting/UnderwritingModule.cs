using CoreIns.Modules.Underwriting.Authority;
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
/// rule engine (illustrative rule set), the decision record and the issue lifecycle with decisions (uw.Issue.decide under
/// UW.ISSUE_APPROVAL authority; an approval holds while the fingerprint of the risk facts is unchanged). Referral routing,
/// conditions, declines with refusal documents and authoring screens are later work packages.
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

        services.AddUnderwritingAuthorityTypes();
        services.AddScoped<IValidator<DecideIssues>, DecideIssuesValidator>();
        services.AddCommandAuditor<DecideIssues, IssueDecideResponse, DecideIssuesAuditor>();
        services.AddCommand<DecideIssues, IssueDecideResponse, DecideIssuesHandler>(CommandDescriptor.For("uw.Issue.decide") with { SupportsDryRun = true });
        services.AddScoped<UnderwritingIssueQueries>();
        services.AddScoped<ReferralReads>();
        services.AddScoped<ReferralQueries>();

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
        ErrorDefinition.For(ModuleCode.UW, "HUMAN-DECISION-REQUIRED", 422, "Την απόφαση πρέπει να την πάρει άνθρωπος", "A person must take this decision")
            .Describe("Ζητήματα ανάληψης αποφασίζονται μόνο από χρήστη, ποτέ από υπηρεσία ή AI.", "Underwriting issues are decided only by a person, never by a service or AI."),
        ErrorDefinition.For(ModuleCode.UW, "SOD", 403, "Δεν μπορείτε να αποφασίσετε για εργασία που χειριστήκατε", "You cannot decide on a job you handled")
            .Describe(
                "Όποιος έκανε την προσφορά ή τη σύναψη της εργασίας δεν αποφασίζει για τα ζητήματά της· ζητήστε την από άλλο ανάδοχο με εξουσιοδότηση.",
                "Whoever quoted or bound the job may not decide its issues; ask another underwriter with authority."),
        ErrorDefinition.For(ModuleCode.UW, "AUTHORITY-REFER", 403, "Απαιτείται υψηλότερη εξουσιοδότηση", "Higher authority is needed")
            .Describe("Η απόφαση υπερβαίνει την εξουσιοδότησή σας· παραπέμψτε τη στον ρόλο που αναφέρεται.", "The decision is above your authority; refer it to the role named."),
        ErrorDefinition.For(ModuleCode.UW, "ISSUE-TRANSITION", 422, "Το ζήτημα δεν μπορεί να αλλάξει έτσι", "The issue cannot change this way")
            .Describe("Μόνο ανοιχτό ζήτημα μπορεί να εγκριθεί ή να απορριφθεί.", "Only an Open issue can be approved or rejected."),
        ErrorDefinition.For(ModuleCode.UW, "STALE", 409, "Το ζήτημα άλλαξε στο μεταξύ", "The issue changed meanwhile")
            .Describe("Φορτώστε ξανά το ζήτημα και αποφασίστε πάλι.", "Reload the issue and decide again."),
    ];
}
