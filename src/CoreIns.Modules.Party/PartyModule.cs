using CoreIns.Modules.Party.Commands;
using CoreIns.Modules.Party.Contracts;
using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Modules.Party.Domain;
using CoreIns.Modules.Party.Persistence;
using CoreIns.Modules.Party.Queries;
using CoreIns.Modules.Party.Services;
using CoreIns.Platform;
using CoreIns.Platform.Approvals;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel.Identifiers;
using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Party;

/// <summary>
/// Composition entry point of the Party module (PRD-01 Party & distribution). The Host calls <see cref="AddPartyModule"/>;
/// the migrate job applies <see cref="Databases"/>. This is the SL-0 reference vertical: see docs/module-pattern.md.
/// </summary>
public static class PartyModule
{
    /// <summary>PostgreSQL schema owned by this module. No other module reads it.</summary>
    public const string Schema = "pty";

    /// <summary>All PostgreSQL schemas owned by this module, created by the migrate job.</summary>
    public static IReadOnlyList<string> Schemas { get; } = [Schema];

    /// <summary>The module database for the migrate job: EF Core migrations of <c>pty</c>, then SELECT/INSERT/UPDATE for the app role (no DELETE).</summary>
    public static IReadOnlyList<ModuleDatabaseDefinition> Databases { get; } =
        [ModuleDbContextRegistration.Define<PartyDbContext>(ModuleCode.PTY, Schema)];

    /// <summary>Registers the module's services: DbContext, commands, queries, in-process contracts, error definitions.</summary>
    public static IServiceCollection AddPartyModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<PartyOptions>().Bind(configuration.GetSection(PartyOptions.Section));
        services.AddModuleDbContext<PartyDbContext>(Schema);

        // Domain services and the read side.
        services.AddScoped<NameForms>();
        services.AddScoped<AddressBuilder>();
        services.AddScoped<PartyProtection>();
        services.AddScoped<PartyReader>();
        services.AddScoped<PartySearch>();
        services.AddScoped<IntermediaryQueries>();

        // Commands through the platform pipeline: validator, auditor, then the decorated handler.
        services.AddScoped<IValidator<CreateParty>, CreatePartyValidator>();
        services.AddCommandAuditor<CreateParty, PartyCreateResponse, CreatePartyAuditor>();
        services.AddCommand<CreateParty, PartyCreateResponse, CreatePartyHandler>(CommandDescriptor.For("pty.Party.create") with { SupportsDryRun = true });

        services.AddScoped<IValidator<UpdateParty>, UpdatePartyValidator>();
        services.AddCommandAuditor<UpdateParty, PartyUpdateResponse, UpdatePartyAuditor>();
        services.AddCommand<UpdateParty, PartyUpdateResponse, UpdatePartyHandler>(CommandDescriptor.For("pty.Party.update") with { SupportsDryRun = true });
        services.AddScoped<IValidator<VerifyIdentifier>, VerifyIdentifierValidator>();
        services.AddCommandAuditor<VerifyIdentifier, IdentifierVerifyResponse, VerifyIdentifierAuditor>();
        services.AddCommand<VerifyIdentifier, IdentifierVerifyResponse, VerifyIdentifierHandler>(CommandDescriptor.For("pty.Identifier.verify"));
        services.AddScoped<PartyValidationService>();

        services.AddScoped<IValidator<RevealParty>, RevealPartyValidator>();
        services.AddCommandAuditor<RevealParty, PartyGetResponse, RevealPartyAuditor>();
        services.AddCommand<RevealParty, PartyGetResponse, RevealPartyHandler>(
            CommandDescriptor.For("pty.Party.revealP2") with { RequiresIdempotencyKey = false, Idempotent = false });

        services.AddScoped<IValidator<CreateIntermediary>, CreateIntermediaryValidator>();
        services.AddCommandAuditor<CreateIntermediary, IntermediaryCreateResponse, IntermediaryAuditor>();
        services.AddCommand<CreateIntermediary, IntermediaryCreateResponse, CreateIntermediaryHandler>(CommandDescriptor.For("pty.Intermediary.create"));

        services.AddScoped<IValidator<SuspendIntermediary>, SuspendIntermediaryValidator>();
        services.AddScoped<IValidator<TerminateIntermediary>, TerminateIntermediaryValidator>();
        services.AddScoped<IValidator<ImportIntermediaryRegister>, ImportIntermediaryRegisterValidator>();
        services.AddCommandAuditor<SuspendIntermediary, IntermediarySuspendResponse, IntermediaryLifecycleAuditor>();
        services.AddCommandAuditor<TerminateIntermediary, IntermediaryTerminateResponse, IntermediaryLifecycleAuditor>();
        services.AddCommandAuditor<ImportIntermediaryRegister, IntermediaryImportRegisterResponse, IntermediaryLifecycleAuditor>();
        services.AddCommand<SuspendIntermediary, IntermediarySuspendResponse, SuspendIntermediaryHandler>(CommandDescriptor.For("pty.Intermediary.suspend"));
        services.AddCommand<TerminateIntermediary, IntermediaryTerminateResponse, TerminateIntermediaryHandler>(CommandDescriptor.For("pty.Intermediary.terminate"));
        services.AddCommand<ImportIntermediaryRegister, IntermediaryImportRegisterResponse, ImportIntermediaryRegisterHandler>(CommandDescriptor.For("pty.Intermediary.importRegister"));
        services.AddScoped<IValidator<UpdateIntermediary>, UpdateIntermediaryValidator>();
        services.AddCommandAuditor<UpdateIntermediary, IntermediaryUpdateResponse, IntermediaryAuditor>();
        services.AddCommand<UpdateIntermediary, IntermediaryUpdateResponse, UpdateIntermediaryHandler>(CommandDescriptor.For("pty.Intermediary.update"));

        services.AddScoped<LocationWriter>();
        services.AddScoped<IValidator<AddLocation>, AddLocationValidator>();
        services.AddScoped<IValidator<UpdateLocation>, UpdateLocationValidator>();
        services.AddScoped<IValidator<RemoveLocation>, RemoveLocationValidator>();
        services.AddCommandAuditor<AddLocation, LocationAddResponse, LocationAuditor>();
        services.AddCommandAuditor<UpdateLocation, LocationUpdateResponse, LocationAuditor>();
        services.AddCommandAuditor<RemoveLocation, LocationRemoveResponse, LocationAuditor>();
        services.AddCommand<AddLocation, LocationAddResponse, AddLocationHandler>(CommandDescriptor.For("pty.Location.add"));
        services.AddCommand<UpdateLocation, LocationUpdateResponse, UpdateLocationHandler>(CommandDescriptor.For("pty.Location.update"));
        services.AddCommand<RemoveLocation, LocationRemoveResponse, RemoveLocationHandler>(CommandDescriptor.For("pty.Location.remove"));
        services.AddScoped<IPartyLocationService, PartyLocationService>();

        services.AddScoped<IValidator<AssignPartyRole>, AssignPartyRoleValidator>();
        services.AddScoped<IValidator<EndPartyRole>, EndPartyRoleValidator>();
        services.AddScoped<IValidator<AddRelationship>, AddRelationshipValidator>();
        services.AddScoped<IValidator<EndRelationship>, EndRelationshipValidator>();
        services.AddCommandAuditor<AssignPartyRole, PartyRoleAssignResponse, RoleAuditor>();
        services.AddCommandAuditor<EndPartyRole, PartyRoleEndResponse, RoleAuditor>();
        services.AddCommandAuditor<AddRelationship, RelationshipAddResponse, RoleAuditor>();
        services.AddCommandAuditor<EndRelationship, RelationshipEndResponse, RoleAuditor>();
        services.AddCommand<AssignPartyRole, PartyRoleAssignResponse, AssignPartyRoleHandler>(CommandDescriptor.For("pty.PartyRole.assign"));
        services.AddCommand<EndPartyRole, PartyRoleEndResponse, EndPartyRoleHandler>(CommandDescriptor.For("pty.PartyRole.end"));
        services.AddCommand<AddRelationship, RelationshipAddResponse, AddRelationshipHandler>(CommandDescriptor.For("pty.Relationship.add"));
        services.AddCommand<EndRelationship, RelationshipEndResponse, EndRelationshipHandler>(CommandDescriptor.For("pty.Relationship.end"));

        services.AddScoped<IValidator<CreateAccount>, CreateAccountValidator>();
        services.AddScoped<IValidator<SetAccountHolder>, SetAccountHolderValidator>();
        services.AddScoped<IValidator<AddAccountMember>, AddAccountMemberValidator>();
        services.AddScoped<IValidator<RemoveAccountMember>, RemoveAccountMemberValidator>();
        services.AddScoped<IValidator<WithdrawAccount>, WithdrawAccountValidator>();
        services.AddCommandAuditor<CreateAccount, AccountCreateResponse, AccountAuditor>();
        services.AddCommandAuditor<SetAccountHolder, AccountSetHolderResponse, AccountAuditor>();
        services.AddCommandAuditor<AddAccountMember, AccountAddMemberResponse, AccountAuditor>();
        services.AddCommandAuditor<RemoveAccountMember, AccountRemoveMemberResponse, AccountAuditor>();
        services.AddCommandAuditor<WithdrawAccount, AccountWithdrawResponse, AccountAuditor>();
        services.AddCommand<CreateAccount, AccountCreateResponse, CreateAccountHandler>(CommandDescriptor.For("pty.Account.create") with { SupportsDryRun = true });
        services.AddCommand<SetAccountHolder, AccountSetHolderResponse, SetAccountHolderHandler>(CommandDescriptor.For("pty.Account.setHolder"));
        services.AddCommand<AddAccountMember, AccountAddMemberResponse, AddAccountMemberHandler>(CommandDescriptor.For("pty.Account.addMember"));
        services.AddCommand<RemoveAccountMember, AccountRemoveMemberResponse, RemoveAccountMemberHandler>(CommandDescriptor.For("pty.Account.removeMember"));
        services.AddCommand<WithdrawAccount, AccountWithdrawResponse, WithdrawAccountHandler>(CommandDescriptor.For("pty.Account.withdraw"));

        services.AddOptions<CommissionOptions>().Bind(configuration.GetSection(CommissionOptions.Section));
        services.AddPartyAuthorityTypes();
        services.AddOwnerDecidedApprovalType(PartyAuthorityTypes.CommissionApprovalType);
        services.AddScoped<CommissionAgreementQueries>();
        services.AddScoped<CommissionResolution>();
        services.AddScoped<IValidator<CreateCommissionVersion>, CreateCommissionVersionValidator>();
        services.AddScoped<IValidator<SubmitCommissionVersion>, SubmitCommissionVersionValidator>();
        services.AddScoped<IValidator<ApproveCommissionVersion>, ApproveCommissionVersionValidator>();
        services.AddCommandAuditor<CreateCommissionVersion, CommissionAgreementCreateVersionResponse, CommissionAuditor>();
        services.AddCommandAuditor<SubmitCommissionVersion, CommissionAgreementSubmitResponse, CommissionAuditor>();
        services.AddCommandAuditor<ApproveCommissionVersion, CommissionAgreementApproveResponse, CommissionAuditor>();
        services.AddCommand<CreateCommissionVersion, CommissionAgreementCreateVersionResponse, CreateCommissionVersionHandler>(CommandDescriptor.For("pty.CommissionAgreement.createVersion"));
        services.AddCommand<SubmitCommissionVersion, CommissionAgreementSubmitResponse, SubmitCommissionVersionHandler>(CommandDescriptor.For("pty.CommissionAgreement.submit"));
        services.AddCommand<ApproveCommissionVersion, CommissionAgreementApproveResponse, ApproveCommissionVersionHandler>(CommandDescriptor.For("pty.CommissionAgreement.approve") with { SupportsDryRun = true });

        services.AddScoped<ProducerOfRecordWriter>();
        services.AddScoped<IValidator<ChangeProducerOfRecord>, ChangeProducerOfRecordValidator>();
        services.AddScoped<IValidator<BulkTransferProducerOfRecord>, BulkTransferProducerOfRecordValidator>();
        services.AddScoped<IValidator<CancelProducerOfRecord>, CancelProducerOfRecordValidator>();
        services.AddCommandAuditor<ChangeProducerOfRecord, ProducerOfRecordChangeResponse, ProducerOfRecordAuditor>();
        services.AddCommandAuditor<BulkTransferProducerOfRecord, ProducerOfRecordBulkTransferResponse, ProducerOfRecordAuditor>();
        services.AddCommandAuditor<CancelProducerOfRecord, ProducerOfRecordCancelResponse, ProducerOfRecordAuditor>();
        services.AddCommand<ChangeProducerOfRecord, ProducerOfRecordChangeResponse, ChangeProducerOfRecordHandler>(CommandDescriptor.For("pty.ProducerOfRecord.change") with { SupportsDryRun = true });
        services.AddCommand<BulkTransferProducerOfRecord, ProducerOfRecordBulkTransferResponse, BulkTransferProducerOfRecordHandler>(CommandDescriptor.For("pty.ProducerOfRecord.bulkTransfer") with { SupportsDryRun = true });
        services.AddCommand<CancelProducerOfRecord, ProducerOfRecordCancelResponse, CancelProducerOfRecordHandler>(CommandDescriptor.For("pty.ProducerOfRecord.cancel"));

        services.AddOwnerDecidedApprovalType(PartyAuthorityTypes.PartyMergeApprovalType);
        services.AddScoped<DuplicateFinder>();
        services.AddScoped<PartyMergeQueries>();
        services.AddScoped<IValidator<RequestPartyMerge>, RequestPartyMergeValidator>();
        services.AddScoped<IValidator<DecidePartyMerge>, DecidePartyMergeValidator>();
        services.AddScoped<IValidator<UnmergeParty>, UnmergePartyValidator>();
        services.AddCommandAuditor<RequestPartyMerge, PartyMergeResponse, MergeAuditor>();
        services.AddCommandAuditor<DecidePartyMerge, PartyMergeApproveResponse, MergeAuditor>();
        services.AddCommandAuditor<UnmergeParty, PartyUnmergeResponse, MergeAuditor>();
        services.AddCommand<RequestPartyMerge, PartyMergeResponse, RequestPartyMergeHandler>(CommandDescriptor.For("pty.Party.merge"));
        services.AddCommand<DecidePartyMerge, PartyMergeApproveResponse, DecidePartyMergeHandler>(CommandDescriptor.For("pty.Party.approveMerge"));
        services.AddCommand<UnmergeParty, PartyUnmergeResponse, UnmergePartyHandler>(CommandDescriptor.For("pty.Party.unmerge"));

        services.AddScoped<VulnerabilityQueries>();
        services.AddScoped<VulnerabilityWriter>();
        services.AddScoped<IValidator<RecordVulnerability>, RecordVulnerabilityValidator>();
        services.AddCommandAuditor<RecordVulnerability, VulnerabilityRecordResponse, VulnerabilityAuditor>();
        services.AddCommand<RecordVulnerability, VulnerabilityRecordResponse, RecordVulnerabilityHandler>(CommandDescriptor.For("pty.Vulnerability.record"));
        services.AddScoped<IValidator<RemoveVulnerability>, RemoveVulnerabilityValidator>();
        services.AddCommandAuditor<RemoveVulnerability, VulnerabilityRemoveResponse, VulnerabilityAuditor>();
        services.AddCommand<RemoveVulnerability, VulnerabilityRemoveResponse, RemoveVulnerabilityHandler>(CommandDescriptor.For("pty.Vulnerability.remove"));

        // In-process contracts other modules call (D-ARC-16).
        services.AddScoped<IPartyPartyService, PartyPartyService>();
        services.AddSingleton<CoreIns.SharedKernel.Dsar.IDsarParticipant, CoreIns.Modules.Party.Services.Dsar.PartyDsarParticipant>();
        services.AddScoped<IPartyProducerCodeService, PartyProducerCodeService>();
        services.AddScoped<IPartyVulnerabilityService, PartyVulnerabilityService>();
        services.AddScoped<IPartyAccountService, PartyAccountService>();
        services.AddScoped<IPartyProducerOfRecordService, PartyProducerOfRecordService>();
        services.AddScoped<IPartyCommissionAgreementService, PartyCommissionAgreementService>();

        // BO-MIG-06: migration SPI (importer and reverser of Person, Organisation and Contact).
        services.AddScoped<CoreIns.Modules.Migration.Contracts.Spi.IMigrationImporter, MigrationImport.PartyMigrationImporter>();
        services.AddScoped<CoreIns.Modules.Migration.Contracts.Spi.IMigrationReverser, MigrationImport.PartyMigrationReverser>();

        services.AddErrorDefinitions(Errors);
        return services;
    }

    /// <summary>Status, bilingual title and description of every PTY-ERR code the module raises (RFC 9457, D-API-15).</summary>
    internal static ErrorDefinition[] Errors { get; } =
    [
        ErrorDefinition.For(ModuleCode.PTY, "ID-CHECKDIGIT", 422, "Λάθος ψηφίο ελέγχου αναγνωριστικού", "The identifier's check digit is wrong")
            .Describe("Ο αριθμός δεν περνά τον έλεγχο του σχήματος (π.χ. ΑΦΜ). Ελέγξτε τα ψηφία.", "The number fails the scheme's check (for example the AFM). Check the digits."),
        ErrorDefinition.For(ModuleCode.PTY, "DUPLICATE-IDENTIFIER", 409, "Το αναγνωριστικό ανήκει ήδη σε άλλο πρόσωπο", "The identifier already belongs to another party")
            .Describe("Χρησιμοποιήστε το υπάρχον πρόσωπο (existingPartyId) αντί να δημιουργήσετε διπλότυπο.", "Use the existing party (existingPartyId) instead of creating a duplicate."),
        ErrorDefinition.For(ModuleCode.PTY, "STALE", 409, "Η εγγραφή άλλαξε στο μεταξύ", "The record changed meanwhile")
            .Describe("Κάποιος άλλος άλλαξε την εγγραφή. Φορτώστε τη νεότερη έκδοση και επαναλάβετε.", "Someone else changed the record. Load the newer version and try again."),
        ErrorDefinition.For(ModuleCode.PTY, "NOT-FOUND", 404, "Δεν βρέθηκε", "Not found")
            .Describe("Η εγγραφή δεν υπάρχει στη νομική σας οντότητα.", "The record does not exist in your legal entity."),
        ErrorDefinition.For(ModuleCode.PTY, "MERGED", 409, "Το πρόσωπο έχει συγχωνευθεί", "The party was merged")
            .Describe("Χρησιμοποιήστε το πρόσωπο που επικράτησε στη συγχώνευση.", "Use the surviving party of the merge."),
        ErrorDefinition.For(ModuleCode.PTY, "RESTRICTED", 409, "Η εγγραφή είναι περιορισμένη", "The record is restricted")
            .Describe("Η εγγραφή του προσώπου περιορίζεται λόγω αιτήματος υποκειμένου δεδομένων και δεν μπορεί να αλλάξει.", "The party record is restricted by a data-subject request and cannot be changed."),
        ErrorDefinition.For(ModuleCode.PTY, "QUERY-TOO-SHORT", 422, "Η αναζήτηση είναι πολύ σύντομη", "The search is too short")
            .Describe("Δώστε τουλάχιστον 2 χαρακτήρες, αριθμό προσώπου ή αναγνωριστικό.", "Give at least 2 characters, a party number or an identifier."),
        ErrorDefinition.For(ModuleCode.PTY, "POSTCODE-FORMAT", 422, "Ο ταχυδρομικός κώδικας δεν έχει τη σωστή μορφή", "The postcode does not have the right format")
            .Describe("Ο ταχυδρομικός κώδικας δεν ταιριάζει με τη μορφή της χώρας της διεύθυνσης.", "The postcode does not match the format of the address's country."),
        ErrorDefinition.For(ModuleCode.PTY, "ACTIVATION-INCOMPLETE", 422, "Η ενεργοποίηση δεν είναι δυνατή ακόμη", "Activation is not possible yet")
            .Describe("Λείπουν προϋποθέσεις (missing). Συμπληρώστε τες και επαναλάβετε.", "Preconditions are missing (missing). Complete them and try again."),
        ErrorDefinition.For(ModuleCode.PTY, "SCREEN-INPUT", 422, "Τα στοιχεία ελέγχου κυρώσεων δεν είναι έγκυρα", "The screening input is not valid")
            .Describe("Δώστε είτε πρόσωπο (partyId) είτε έκτακτο δικαιούχο με όνομα, όχι και τα δύο.", "Give either a party (partyId) or an ad-hoc payee with a name, not both."),
        ErrorDefinition.For(ModuleCode.PTY, "ROLE-DUPLICATE", 409, "Το πρόσωπο έχει ήδη αυτόν τον ρόλο", "The party already holds this role")
            .Describe("Ο ίδιος ρόλος στο ίδιο πλαίσιο δεν μπορεί να επικαλύπτεται χρονικά. Τερματίστε τον υπάρχοντα πρώτα.", "The same role in the same context cannot overlap in time. End the existing one first."),
        ErrorDefinition.For(ModuleCode.PTY, "ROLE-TYPE", 422, "Άγνωστος τύπος ρόλου ή σχέσης", "Unknown role or relationship type")
            .Describe("Ο τύπος δεν υπάρχει στον κατάλογο ρόλων και σχέσεων.", "The type is not in the role and relationship catalogue."),
        ErrorDefinition.For(ModuleCode.PTY, "ROLE-DATA-MISSING", 422, "Λείπουν στοιχεία για τον ρόλο", "Data is missing for the role")
            .Describe("Ο ρόλος απαιτεί στοιχεία που λείπουν από το πρόσωπο (π.χ. ΑΦΜ για προμηθευτή).", "The role needs data the party does not have (for example an AFM for a vendor)."),
        ErrorDefinition.For(ModuleCode.PTY, "OVERLAP", 422, "Η περίοδος επικαλύπτεται", "The period overlaps")
            .Describe("Υπάρχει ήδη η ίδια σχέση για επικαλυπτόμενη περίοδο.", "The same relationship already exists for an overlapping period."),
        ErrorDefinition.For(ModuleCode.PTY, "RELATIONSHIP-CYCLE", 422, "Η σχέση θα δημιουργούσε κύκλο", "The relationship would close a cycle")
            .Describe("Στις ιεραρχικές σχέσεις ένα πρόσωπο δεν μπορεί να είναι άμεσα ή έμμεσα πάνω από τον εαυτό του.", "In a hierarchical relationship a party cannot sit above itself, directly or indirectly."),
        ErrorDefinition.For(ModuleCode.PTY, "ACCOUNT-WITHDRAWN", 409, "Ο λογαριασμός έχει αποσυρθεί", "The account was withdrawn")
            .Describe("Ένας αποσυρθείς λογαριασμός δεν μπορεί να αλλάξει.", "A withdrawn account cannot be changed."),
        ErrorDefinition.For(ModuleCode.PTY, "ACCOUNT-HAS-JOBS", 422, "Ο λογαριασμός έχει ενεργά ασφαλιστήρια", "The account still has active policies")
            .Describe("Ο κάτοχος έχει ασφαλιστήριο σε ισχύ, προγραμματισμένο ή σε εκκρεμή ακύρωση. Ακυρώστε ή μεταφέρτε το πρώτα.", "The holder has a policy in force, scheduled or pending cancellation. Cancel or move it first."),
        ErrorDefinition.For(ModuleCode.PTY, "MEMBER-IN-USE", 422, "Το μέλος ανήκει ήδη σε άλλο νοικοκυριό", "The member already belongs to another household")
            .Describe("Ένα πρόσωπο μπορεί να ανήκει σε ένα μόνο νοικοκυριό. Αφαιρέστε το πρώτα από το άλλο.", "A party can belong to one household only. Remove it from the other one first."),
        ErrorDefinition.For(ModuleCode.PTY, "MEMBER-DUPLICATE", 409, "Το πρόσωπο είναι ήδη μέλος", "The party is already a member")
            .Describe("Το πρόσωπο είναι ήδη μέλος αυτού του λογαριασμού.", "The party is already a member of this account."),
        ErrorDefinition.For(ModuleCode.PTY, "MEMBER-LIMIT", 422, "Έχει συμπληρωθεί το όριο μελών", "The member limit is reached")
            .Describe("Ο λογαριασμός δεν μπορεί να έχει περισσότερα μέλη από το επιτρεπόμενο όριο.", "The account cannot have more members than the allowed limit."),
        ErrorDefinition.For(ModuleCode.PTY, "RATE-ABOVE-MAX", 422, "Το ποσοστό υπερβαίνει το μέγιστο", "The rate is above the maximum")
            .Describe("Ένα ποσοστό προμήθειας υπερβαίνει το μέγιστο που επιτρέπεται.", "A commission rate is above the maximum allowed."),
        ErrorDefinition.For(ModuleCode.PTY, "BACKDATE", 422, "Η ημερομηνία ισχύος είναι σε διακανονισμένη περίοδο", "The effective date is in a settled period")
            .Describe("Η έκδοση δεν μπορεί να ισχύει από ημερομηνία πριν ή στη διακανονισμένη, ούτε πριν από την τελευταία ενεργή έκδοση.", "The version cannot take effect on or before the settled date, or before the latest active version."),
        ErrorDefinition.For(ModuleCode.PTY, "INTERMEDIARY-NOT-ACTIVE", 422, "Ο διαμεσολαβητής είναι σε αναστολή ή έχει τερματιστεί", "The intermediary is suspended or terminated")
            .Describe("Ένας διαμεσολαβητής σε αναστολή ή τερματισμένος δεν μπορεί να αποκτήσει νέα συμφωνία προμήθειας.", "A suspended or terminated intermediary cannot get a new commission agreement."),
        ErrorDefinition.For(ModuleCode.PTY, "POR-CODE-INVALID", 422, "Ο κωδικός παραγωγού δεν μπορεί να αναλάβει το ασφαλιστήριο", "The producer code cannot take the policy")
            .Describe("Ο κωδικός είναι ανενεργός, ο διαμεσολαβητής σε αναστολή ή τερματισμένος, ή το προϊόν δεν είναι διορισμένο.", "The code is inactive, its intermediary is suspended or terminated, or the product is not appointed."),
        ErrorDefinition.For(ModuleCode.PTY, "POR-LETTER-REQUIRED", 422, "Απαιτείται η επιστολή αλλαγής διαμεσολαβητή", "The broker-of-record letter is required")
            .Describe("Η αλλαγή σε άλλον διαμεσολαβητή χρειάζεται αναφορά της επιστολής του πελάτη (letterReference).", "A change to another intermediary needs the customer's letter reference (letterReference)."),
        ErrorDefinition.For(ModuleCode.PTY, "STATE", 409, "Η ενέργεια δεν επιτρέπεται στην τρέχουσα κατάσταση", "The action is not allowed in the current state")
            .Describe("Η έκδοση ή η συμφωνία δεν βρίσκεται σε κατάσταση που επιτρέπει την ενέργεια.", "The version or agreement is not in a state that allows the action."),
        ErrorDefinition.For(ModuleCode.PTY, "MERGE-IDENTIFIER-CONFLICT", 422, "Τα πρόσωπα έχουν διαφορετικά έγκυρα ΑΦΜ", "The parties hold different valid AFMs")
            .Describe("Δύο πρόσωπα με διαφορετικά έγκυρα ΑΦΜ δεν είναι το ίδιο πρόσωπο και δεν μπορούν να συγχωνευθούν.", "Two parties with different valid AFMs are not the same party and cannot be merged."),
        ErrorDefinition.For(ModuleCode.PTY, "WINDOW-CLOSED", 422, "Το περιθώριο αναίρεσης έχει λήξει", "The unmerge window has closed")
            .Describe("Η συγχώνευση δεν μπορεί πλέον να αναιρεθεί.", "The merge can no longer be unmerged."),
        ErrorDefinition.For(ModuleCode.PTY, "NOT-AVAILABLE", 501, "Η λειτουργία δεν είναι ακόμη διαθέσιμη", "The operation is not available yet")
            .Describe("Η λειτουργία ανήκει σε επόμενο πακέτο εργασιών.", "The operation belongs to a later work package."),
    ];
}
