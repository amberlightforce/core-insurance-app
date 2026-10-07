using CoreIns.Modules.Party.Commands;
using CoreIns.Modules.Party.Contracts;
using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Modules.Party.Domain;
using CoreIns.Modules.Party.Persistence;
using CoreIns.Modules.Party.Queries;
using CoreIns.Modules.Party.Services;
using CoreIns.Platform;
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
        services.AddScoped<PartyProtection>();
        services.AddScoped<PartyReader>();
        services.AddScoped<PartySearch>();
        services.AddScoped<IntermediaryQueries>();

        // Commands through the platform pipeline: validator, auditor, then the decorated handler.
        services.AddScoped<IValidator<CreateParty>, CreatePartyValidator>();
        services.AddCommandAuditor<CreateParty, PartyCreateResponse, CreatePartyAuditor>();
        services.AddCommand<CreateParty, PartyCreateResponse, CreatePartyHandler>(CommandDescriptor.For("pty.Party.create") with { SupportsDryRun = true });

        services.AddScoped<IValidator<RevealParty>, RevealPartyValidator>();
        services.AddCommandAuditor<RevealParty, PartyGetResponse, RevealPartyAuditor>();
        services.AddCommand<RevealParty, PartyGetResponse, RevealPartyHandler>(
            CommandDescriptor.For("pty.Party.revealP2") with { RequiresIdempotencyKey = false, Idempotent = false });

        services.AddScoped<IValidator<CreateIntermediary>, CreateIntermediaryValidator>();
        services.AddCommandAuditor<CreateIntermediary, IntermediaryCreateResponse, IntermediaryAuditor>();
        services.AddCommand<CreateIntermediary, IntermediaryCreateResponse, CreateIntermediaryHandler>(CommandDescriptor.For("pty.Intermediary.create"));

        services.AddScoped<IValidator<UpdateIntermediary>, UpdateIntermediaryValidator>();
        services.AddCommandAuditor<UpdateIntermediary, IntermediaryUpdateResponse, IntermediaryAuditor>();
        services.AddCommand<UpdateIntermediary, IntermediaryUpdateResponse, UpdateIntermediaryHandler>(CommandDescriptor.For("pty.Intermediary.update"));

        // In-process contracts other modules call (D-ARC-16).
        services.AddScoped<IPartyPartyService, PartyPartyService>();
        services.AddScoped<IPartyProducerCodeService, PartyProducerCodeService>();

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
        ErrorDefinition.For(ModuleCode.PTY, "QUERY-TOO-SHORT", 422, "Η αναζήτηση είναι πολύ σύντομη", "The search is too short")
            .Describe("Δώστε τουλάχιστον 2 χαρακτήρες, αριθμό προσώπου ή αναγνωριστικό.", "Give at least 2 characters, a party number or an identifier."),
        ErrorDefinition.For(ModuleCode.PTY, "POSTCODE-FORMAT", 422, "Ο ταχυδρομικός κώδικας δεν έχει τη σωστή μορφή", "The postcode does not have the right format")
            .Describe("Ο ταχυδρομικός κώδικας δεν ταιριάζει με τη μορφή της χώρας της διεύθυνσης.", "The postcode does not match the format of the address's country."),
        ErrorDefinition.For(ModuleCode.PTY, "ACTIVATION-INCOMPLETE", 422, "Η ενεργοποίηση δεν είναι δυνατή ακόμη", "Activation is not possible yet")
            .Describe("Λείπουν προϋποθέσεις (missing). Συμπληρώστε τες και επαναλάβετε.", "Preconditions are missing (missing). Complete them and try again."),
        ErrorDefinition.For(ModuleCode.PTY, "NOT-AVAILABLE", 501, "Η λειτουργία δεν είναι ακόμη διαθέσιμη", "The operation is not available yet")
            .Describe("Η λειτουργία ανήκει σε επόμενο πακέτο εργασιών.", "The operation belongs to a later work package."),
    ];
}
