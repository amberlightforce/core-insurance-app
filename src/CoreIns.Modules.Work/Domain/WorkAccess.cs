using System.Security.Cryptography;
using System.Text;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Work.Domain;

/// <summary>WRK error names (<c>WRK-ERR-*</c>, contracts/openapi/wrk.yaml) and their definitions.</summary>
internal static class WorkErrors
{
    public const string Duplicate = "DUPLICATE";
    public const string PatternNotApplicable = "PATTERN-NOT-APPLICABLE";
    public const string Permission = "PERMISSION";
    public const string OutcomeRequired = "OUTCOME-REQUIRED";
    public const string NoteRequired = "NOTE-REQUIRED";
    public const string QueueEmpty = "QUEUE-EMPTY";
    public const string Stale = "STALE";
    public const string LimitExceeded = "LIMIT-EXCEEDED";
    public const string TargetIneligible = "TARGET-INELIGIBLE";
    public const string NotAvailable = "NOT-AVAILABLE";

    public static DomainError Of(string name, string detail) => DomainError.Of(ModuleCode.WRK, name, detail);

    public static DomainError NotFound() => DomainError.Of(ModuleCode.WRK, PlatformErrors.NotFound, "The activity does not exist.");

    public static DomainError Invalid(string detail) => DomainError.Of(ModuleCode.WRK, PlatformErrors.Validation, detail);

    public static ErrorDefinition[] Definitions { get; } =
    [
        ErrorDefinition.For(ModuleCode.WRK, Duplicate, 409, "Η εργασία υπάρχει ήδη", "The activity already exists")
            .Describe("Υπάρχει ήδη ανοικτή εργασία του ίδιου μοτίβου για το ίδιο αντικείμενο και την ίδια αιτία.", "An open activity of the same pattern already exists for the same object and cause."),
        ErrorDefinition.For(ModuleCode.WRK, PatternNotApplicable, 422, "Το μοτίβο δεν εφαρμόζεται", "The pattern does not apply")
            .Describe("Το μοτίβο είναι άγνωστο ή δεν επιτρέπεται στην ενότητα που το ζητά.", "The pattern is unknown or the raising module may not create it."),
        ErrorDefinition.For(ModuleCode.WRK, Permission, 403, "Δεν επιτρέπεται η ενέργεια", "The action is not permitted")
            .Describe("Μόνο ο υπεύθυνος της εργασίας ή ένας προϊστάμενος μπορεί να την αλλάξει.", "Only the assignee or a manager may change the activity."),
        ErrorDefinition.For(ModuleCode.WRK, OutcomeRequired, 422, "Απαιτείται αποτέλεσμα", "An outcome is required")
            .Describe("Η ολοκλήρωση της εργασίας απαιτεί κωδικό αποτελέσματος.", "Completing an activity needs an outcome code."),
        ErrorDefinition.For(ModuleCode.WRK, NoteRequired, 422, "Απαιτείται αιτιολογία", "A reason is required")
            .Describe("Η παράλειψη ή ακύρωση εργασίας απαιτεί αιτιολογία.", "Skipping or cancelling an activity needs a reason."),
        ErrorDefinition.For(ModuleCode.WRK, QueueEmpty, 404, "Η ουρά είναι άδεια", "The queue is empty")
            .Describe("Δεν υπάρχει ανοικτή εργασία για ανάληψη στις ουρές σας.", "No open activity is waiting in your queues."),
        ErrorDefinition.For(ModuleCode.WRK, Stale, 409, "Η εργασία άλλαξε στο μεταξύ", "The activity changed meanwhile", retryable: true)
            .Describe("Κάποιος άλλος άλλαξε ή ανέλαβε την εργασία. Φορτώστε τη νεότερη έκδοση και επαναλάβετε.", "Someone else changed or claimed the activity. Load the newer version and try again."),
        ErrorDefinition.For(ModuleCode.WRK, LimitExceeded, 422, "Υπέρβαση ορίου", "A limit was exceeded")
            .Describe("Το αίτημα ξεπερνά το επιτρεπτό πλήθος στοιχείων.", "The request exceeds the allowed number of items."),
        ErrorDefinition.For(ModuleCode.WRK, NotAvailable, 501, "Η λειτουργία δεν είναι ακόμη διαθέσιμη", "The operation is not available yet")
            .Describe("Η λειτουργία ανήκει σε επόμενο πακέτο εργασιών.", "The operation belongs to a later work package."),
        ErrorDefinition.For(ModuleCode.WRK, TargetIneligible, 422, "Ο αποδέκτης δεν είναι κατάλληλος", "The target is not eligible")
            .Describe("Η εργασία δεν μπορεί να ανατεθεί στον συγκεκριμένο αποδέκτη.", "The activity cannot be assigned to that target."),
    ];
}

/// <summary>Who is asking and what they may see, derived from the request context (the roles come from the token, never from a DTO).</summary>
internal sealed class WorkActor
{
    public const string AdminRole = "Platform.Admin";

    private WorkActor(ActorRef actor, IReadOnlyCollection<string> roles)
    {
        Actor = actor;
        Roles = roles;
    }

    public ActorRef Actor { get; }

    public IReadOnlyCollection<string> Roles { get; }

    /// <summary>The user id used in assignments; null for services (a service never holds an assignment).</summary>
    public string? UserId => Actor.Kind == ActorKind.User ? Actor.Id : null;

    public bool IsAdmin => Roles.Contains(AdminRole, StringComparer.Ordinal);

    /// <summary>Managers (<c>Staff.*Manager</c>) and the platform admin may assign to others.</summary>
    public bool IsManager => IsAdmin || Roles.Any(r => r.StartsWith("Staff.", StringComparison.Ordinal) && r.EndsWith("Manager", StringComparison.Ordinal));

    public static WorkActor From(RequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new WorkActor(context.Actor, context.Roles);
    }

    /// <summary>Queue codes the actor may see: the admin sees every queue.</summary>
    public IReadOnlyList<string> VisibleQueues =>
        [.. (IsAdmin ? WorkCatalog.Queues : WorkCatalog.QueuesFor(Roles)).Select(q => q.Code)];

    public bool CanSee(string queueCode) => VisibleQueues.Contains(queueCode, StringComparer.Ordinal);
}

/// <summary>Queue ids on the wire: contracts type a queue as a UUID, the Work core names queues by code, so the id is derived from the code.</summary>
internal static class QueueIds
{
    public static QueueId For(string code) => new(new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("coreins.wrk.queue:" + code))[..16]));

    public static string? CodeOf(QueueId id) => WorkCatalog.Queues.Select(q => q.Code).FirstOrDefault(code => For(code) == id);
}
