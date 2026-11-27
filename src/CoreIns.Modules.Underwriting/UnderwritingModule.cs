using CoreIns.Modules.Underwriting.Authority;
using CoreIns.Modules.Underwriting.Commands;
using CoreIns.Modules.Underwriting.Contracts;
using CoreIns.Modules.Underwriting.Contracts.Api;
using CoreIns.Modules.Underwriting.Domain;
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

        services.AddScoped<RuleSetAuthoringStore>();
        services.AddScoped<RuleSetAuthoringSupport>();
        services.AddScoped<RuleSetAuthoringQueries>();
        services.AddScoped<IValidator<CreateRuleSet>, CreateRuleSetValidator>();
        services.AddScoped<IValidator<DraftRuleSet>, DraftRuleSetValidator>();
        services.AddScoped<IValidator<EditRuleSet>, EditRuleSetValidator>();
        services.AddScoped<IValidator<ImportRuleSet>, ImportRuleSetValidator>();
        services.AddCommandAuditor<CreateRuleSet, RuleSetAuthoringResponse, RuleSetAuthoringAuditor<CreateRuleSet>>();
        services.AddCommandAuditor<DraftRuleSet, RuleSetAuthoringResponse, RuleSetAuthoringAuditor<DraftRuleSet>>();
        services.AddCommandAuditor<EditRuleSet, RuleSetAuthoringResponse, RuleSetAuthoringAuditor<EditRuleSet>>();
        services.AddCommandAuditor<ImportRuleSet, RuleSetAuthoringResponse, RuleSetAuthoringAuditor<ImportRuleSet>>();
        services.AddCommand<CreateRuleSet, RuleSetAuthoringResponse, CreateRuleSetHandler>(CommandDescriptor.For("uw.RuleSet.create"));
        services.AddCommand<DraftRuleSet, RuleSetAuthoringResponse, DraftRuleSetHandler>(CommandDescriptor.For("uw.RuleSet.draft"));
        services.AddCommand<EditRuleSet, RuleSetAuthoringResponse, EditRuleSetHandler>(CommandDescriptor.For("uw.RuleSet.edit"));
        services.AddCommand<ImportRuleSet, RuleSetAuthoringResponse, ImportRuleSetHandler>(CommandDescriptor.For("uw.RuleSet.import"));

        services.AddScoped<RuleSetReleaseSupport>();
        services.AddScoped<IValidator<SubmitRuleSet>, SubmitRuleSetValidator>();
        services.AddScoped<IValidator<ApproveRuleSet>, ApproveRuleSetValidator>();
        services.AddScoped<IValidator<ScheduleRuleSet>, ScheduleRuleSetValidator>();
        services.AddCommandAuditor<SubmitRuleSet, RuleSetReleaseResponse, RuleSetReleaseAuditor<SubmitRuleSet>>();
        services.AddCommandAuditor<ApproveRuleSet, RuleSetReleaseResponse, RuleSetReleaseAuditor<ApproveRuleSet>>();
        services.AddCommandAuditor<ScheduleRuleSet, RuleSetReleaseResponse, RuleSetReleaseAuditor<ScheduleRuleSet>>();
        services.AddCommand<SubmitRuleSet, RuleSetReleaseResponse, SubmitRuleSetHandler>(CommandDescriptor.For("uw.RuleSet.submit"));
        services.AddCommand<ApproveRuleSet, RuleSetReleaseResponse, ApproveRuleSetHandler>(CommandDescriptor.For("uw.RuleSet.approve"));
        services.AddCommand<ScheduleRuleSet, RuleSetReleaseResponse, ScheduleRuleSetHandler>(CommandDescriptor.For("uw.RuleSet.schedule"));

        services.AddScoped<IssueTypeStore>();
        services.AddScoped<IssueTypeQueries>();
        services.AddScoped<IValidator<UpsertIssueType>, UpsertIssueTypeValidator>();
        services.AddCommandAuditor<UpsertIssueType, IssueTypeItem, UpsertIssueTypeAuditor>();
        services.AddCommand<UpsertIssueType, IssueTypeItem, UpsertIssueTypeHandler>(CommandDescriptor.For("uw.IssueType.upsert"));

        services.AddUnderwritingAuthorityTypes();
        services.AddScoped<IValidator<DecideIssues>, DecideIssuesValidator>();
        services.AddCommandAuditor<DecideIssues, IssueDecideResponse, DecideIssuesAuditor>();
        services.AddCommand<DecideIssues, IssueDecideResponse, DecideIssuesHandler>(CommandDescriptor.For("uw.Issue.decide") with { SupportsDryRun = true });
        services.AddScoped<UnderwritingIssueQueries>();

        // BO-UW-03: batch evaluation (no effect on jobs) and the issue reads.
        services.AddScoped<IValidator<EvaluateRulesBatch>, EvaluateRulesBatchValidator>();
        services.AddCommandAuditor<EvaluateRulesBatch, RulesEvaluateBatchResponse, EvaluateRulesBatchAuditor>();
        services.AddCommand<EvaluateRulesBatch, RulesEvaluateBatchResponse, EvaluateRulesBatchHandler>(CommandDescriptor.For("uw.Rules.evaluateBatch") with { SupportsDryRun = true });
        services.AddScoped<IssueReads>();
        services.AddScoped<IssueReadQueries>();
        services.AddScoped<DecisionEligibility>();
        services.AddScoped<ReferralReads>();

        // BO-UW-05: referral commands and the review lock.
        services.AddScoped<ReferralStateStore>();
        services.AddScoped<ReferralSupport>();
        services.AddScoped<ReferralAuditNotes>();
        services.AddScoped<UwWorkTasks>();
        services.AddScoped<IValidator<RequestReferral>, RequestReferralValidator>();
        services.AddScoped<IValidator<WithdrawReferral>, WithdrawReferralValidator>();
        services.AddScoped<IValidator<ReferReferralUp>, ReferReferralUpValidator>();
        services.AddScoped<IValidator<RequestReferralInformation>, RequestReferralInformationValidator>();
        services.AddCommandAuditor<RequestReferral, ReferralRequestResponse, ReferralAuditor<RequestReferral, ReferralRequestResponse>>();
        services.AddCommandAuditor<WithdrawReferral, ReferralWithdrawResponse, ReferralAuditor<WithdrawReferral, ReferralWithdrawResponse>>();
        services.AddCommandAuditor<ReferReferralUp, ReferralReferUpResponse, ReferralAuditor<ReferReferralUp, ReferralReferUpResponse>>();
        services.AddCommandAuditor<RequestReferralInformation, ReferralRequestInformationResponse, ReferralAuditor<RequestReferralInformation, ReferralRequestInformationResponse>>();
        services.AddCommandAuditor<LockReview, ReviewLockResponse, ReferralAuditor<LockReview, ReviewLockResponse>>();
        services.AddCommandAuditor<ReleaseReview, ReviewReleaseResponse, ReferralAuditor<ReleaseReview, ReviewReleaseResponse>>();
        services.AddCommand<RequestReferral, ReferralRequestResponse, RequestReferralHandler>(CommandDescriptor.For("uw.Referral.request"));
        services.AddCommand<WithdrawReferral, ReferralWithdrawResponse, WithdrawReferralHandler>(CommandDescriptor.For("uw.Referral.withdraw"));
        services.AddCommand<ReferReferralUp, ReferralReferUpResponse, ReferReferralUpHandler>(CommandDescriptor.For("uw.Referral.referUp"));
        services.AddCommand<RequestReferralInformation, ReferralRequestInformationResponse, RequestReferralInformationHandler>(CommandDescriptor.For("uw.Referral.requestInformation"));
        services.AddCommand<LockReview, ReviewLockResponse, LockReviewHandler>(CommandDescriptor.For("uw.Review.lock"));
        services.AddCommand<ReleaseReview, ReviewReleaseResponse, ReleaseReviewHandler>(CommandDescriptor.For("uw.Review.release"));
        services.AddScoped<ReferralQueries>();

        services.AddScoped<IUnderwritingRulesService, UnderwritingRulesService>();
        services.AddScoped<IUnderwritingIssueService, UnderwritingIssueService>();

        services.AddSingleton<CoreIns.SharedKernel.Dsar.IDsarParticipant, CoreIns.Modules.Underwriting.Services.Dsar.UnderwritingDsarParticipant>();
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
        ErrorDefinition.For(ModuleCode.UW, "RULESET-NOT-FOUND", 404, "Δεν βρέθηκε το σύνολο κανόνων", "The rule set was not found")
            .Describe("Δεν υπάρχει σύνολο κανόνων με αυτόν τον κωδικό και αυτή την έκδοση.", "There is no rule set with this code and version."),
        ErrorDefinition.For(ModuleCode.UW, "RULESET-STATE", 409, "Η έκδοση δεν είναι πρόχειρη", "The version is not a draft")
            .Describe("Μόνο πρόχειρη έκδοση μπορεί να τροποποιηθεί.", "Only a draft version can be edited."),
        ErrorDefinition.For(ModuleCode.UW, "RULESET-EXISTS", 409, "Το σύνολο κανόνων υπάρχει ήδη", "The rule set already exists")
            .Describe("Υπάρχει ήδη σύνολο κανόνων με αυτόν τον κωδικό και αυτή την έκδοση.", "A rule set with this code and version already exists."),
        ErrorDefinition.For(ModuleCode.UW, "RULE-UNTESTED", 422, "Υπάρχει κανόνας χωρίς δοκιμή", "A rule has no test case")
            .Describe("Κάθε κανόνας πρέπει να αναμένεται να ενεργοποιείται σε τουλάχιστον μία δοκιμαστική περίπτωση που περνά.", "Every rule must be expected to hit in at least one passing test case."),
        ErrorDefinition.For(ModuleCode.UW, "DUPLICATE", 409, "Ο τύπος ζητήματος υπάρχει ήδη", "The issue type already exists")
            .Describe("Στείλτε την τρέχουσα έκδοση εγγραφής για να τον αλλάξετε.", "Send the current record version to change it."),
        ErrorDefinition.For(ModuleCode.UW, "NOT-REFERRABLE", 422, "Η εργασία δεν μπορεί να παραπεμφθεί", "The job cannot be referred")
            .Describe("Η εργασία δεν έχει ανοιχτό ζήτημα ανάληψης ή η παραπομπή έχει αποσυρθεί.", "The job has no Open underwriting issue, or the referral was withdrawn."),
        ErrorDefinition.For(ModuleCode.UW, "LOCKED-BY-OTHER", 409, "Η παραπομπή εξετάζεται από άλλον αναδόχο", "Another underwriter is reviewing the referral", retryable: true)
            .Describe("Άλλος ανάδοχος κρατά το κλείδωμα εξέτασης· περιμένετε να λήξει ή ζητήστε από διευθυντή να το άρει.", "Another underwriter holds the review lock; wait for it to expire or ask a manager to break it."),
        ErrorDefinition.For(ModuleCode.UW, "REASON-REQUIRED", 422, "Απαιτείται αιτιολογία", "A reason is required")
            .Describe("Η άρση του κλειδώματος άλλου αναδόχου απαιτεί αιτιολογία.", "Breaking another underwriter's lock needs a reason."),
        ErrorDefinition.For(ModuleCode.UW, "AUTHORITY-DENIED", 403, "Δεν έχετε εξουσιοδότηση για την ενέργεια", "You are not authorised for this action")
            .Describe("Η ενέργεια επιτρέπεται μόνο στον αιτούντα ή σε διευθυντή ανάληψης.", "Only the requester or an underwriting manager may do this."),
        ErrorDefinition.For(ModuleCode.UW, "LIMIT-EXCEEDED", 422, "Η παρτίδα είναι πολύ μεγάλη", "The batch is too large")
            .Describe("Μια παρτίδα αξιολογεί έως 200 κινδύνους· χωρίστε την σε περισσότερα αιτήματα.", "A batch evaluates at most 200 risks; split it into several requests."),
        ErrorDefinition.For(ModuleCode.UW, "ISSUE-NOT-FOUND", 404, "Δεν βρέθηκε το ζήτημα", "The issue was not found")
            .Describe("Δεν υπάρχει ζήτημα με αυτό το αναγνωριστικό στη νομική οντότητά σας.", "There is no issue with this id in your legal entity."),
        ErrorDefinition.For(ModuleCode.UW, "STALE", 409, "Το ζήτημα άλλαξε στο μεταξύ", "The issue changed meanwhile")
            .Describe("Φορτώστε ξανά το ζήτημα και αποφασίστε πάλι.", "Reload the issue and decide again."),
    ];
}
