using System.Text.Json;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Migration.Domain;

/// <summary>Batch lifecycle (D-SL6-05, MIG-005).</summary>
internal static class BatchStatus
{
    public const string Draft = "DRAFT";
    public const string PendingApproval = "PENDING_APPROVAL";
    public const string Rejected = "REJECTED";
    public const string Running = "RUNNING";
    public const string Completed = "COMPLETED";
    public const string CompletedWithErrors = "COMPLETED_WITH_ERRORS";
    public const string Paused = "PAUSED";
    public const string Reversed = "REVERSED";
    public const string ReversalBlocked = "REVERSAL_BLOCKED";
    public const string NotReversible = "NOT_REVERSIBLE";
}

/// <summary>Per-item outcome.</summary>
internal static class ItemOutcome
{
    public const string Pending = "PENDING";
    public const string Imported = "IMPORTED";
    public const string SkippedExisting = "SKIPPED_EXISTING";
    public const string Failed = "FAILED";
    public const string Reversed = "REVERSED";
    public const string ReversalBlocked = "REVERSAL_BLOCKED";
}

/// <summary>Coexistence master states per policy and date.</summary>
internal static class RouteState
{
    public const string None = "NONE";
    public const string Transitioning = "TRANSITIONING";
    public const string Core = "CORE";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { None, Transitioning, Core };
}

/// <summary>The approval type and authority of a non-dry-run batch (MIG-005).</summary>
internal static class BatchApproval
{
    public const string Type = "MIG.BATCH_RUN";
    public const string ReferralRole = "Staff.MigrationManager";
    public const string ScopeDimension = "scope";
    public const string Scope = "BATCH";

    public static ObjectRef Subject(Guid batchId) => new(ModuleCode.MIG, "Batch", batchId.ToString("D"));
}

/// <summary>MIG error names raised by the framework (the generated MIG-ERR codes are reused where they exist).</summary>
internal static class MigrationErrors
{
    public const string Validation = "VALIDATION";
    public const string BatchState = "BATCH-STATE";
    public const string RoutingOverlap = "ROUTING-OVERLAP";
    public const string ReversalBlocked = "REVERSAL-BLOCKED";
    public const string NotReversible = "NOT-REVERSIBLE";

    public static DomainError NotFound(string what) => DomainError.Of(ModuleCode.MIG, "NOT-FOUND", $"{what} does not exist in your legal entity.");

    public static DomainError State(string detail) => DomainError.Of(ModuleCode.MIG, BatchState, detail);

    public static ErrorDefinition[] Definitions { get; } =
    [
        ErrorDefinition.For(ModuleCode.MIG, "NOT-FOUND", 404, "Δεν βρέθηκε", "Not found")
            .Describe("Η εγγραφή δεν υπάρχει στη νομική σας οντότητα.", "The record does not exist in your legal entity."),
        ErrorDefinition.For(ModuleCode.MIG, Validation, 422, "Μη έγκυρο αίτημα μετάπτωσης", "The migration request is not valid")
            .Describe("Ελέγξτε τα πεδία του αιτήματος.", "Check the request fields."),
        ErrorDefinition.For(ModuleCode.MIG, BatchState, 409, "Η δέσμη δεν επιτρέπει αυτή την ενέργεια", "The batch state does not allow this action")
            .Describe("Η κατάσταση της δέσμης δεν επιτρέπει τη λειτουργία.", "The batch is in a state that does not allow the operation."),
        ErrorDefinition.For(ModuleCode.MIG, "DRYRUN-REQUIRED", 422, "Απαιτείται δοκιμαστική εκτέλεση", "A dry run is required first")
            .Describe("Η δέσμη πρέπει να εκτελεστεί δοκιμαστικά με το ίδιο περιεχόμενο πριν την πραγματική εκτέλεση.", "Run the batch dry with the same content before the real run."),
        ErrorDefinition.For(ModuleCode.MIG, "APPROVAL-REQUIRED", 422, "Απαιτείται έγκριση", "Approval required")
            .Describe("Η πραγματική εκτέλεση χρειάζεται έγκριση από άλλον χρήστη.", "A real run needs approval by a different user."),
        ErrorDefinition.For(ModuleCode.MIG, "DEPENDENCY", 422, "Δεν υπάρχει εισαγωγέας για τη μονάδα", "No importer for the module")
            .Describe("Η μονάδα δεν έχει καταχωρήσει εισαγωγέα μετάπτωσης.", "The module has not registered a migration importer."),
        ErrorDefinition.For(ModuleCode.MIG, "XREF-CONFLICT", 409, "Σύγκρουση διασταυρούμενης αναφοράς", "Cross-reference conflict")
            .Describe("Το κλειδί του παλαιού συστήματος είναι ήδη συνδεδεμένο με άλλο αντικείμενο.", "The legacy key is already linked to a different core object."),
        ErrorDefinition.For(ModuleCode.MIG, "AMBIGUOUS-KEY", 422, "Διφορούμενο κλειδί", "Ambiguous key")
            .Describe("Το κλειδί αντιστοιχεί σε περισσότερα από ένα αντικείμενα· δώστε πηγή.", "The key matches more than one object; give the source."),
        ErrorDefinition.For(ModuleCode.MIG, RoutingOverlap, 422, "Επικαλυπτόμενες περίοδοι δρομολόγησης", "Overlapping routing periods")
            .Describe("Υπάρχει μόνο ένας κύριος ανά ασφαλιστήριο και ημερομηνία.", "There is exactly one master per policy and date."),
        ErrorDefinition.For(ModuleCode.MIG, ReversalBlocked, 422, "Η αναίρεση μπλοκαρίστηκε", "Reversal blocked")
            .Describe("Το αντικείμενο έχει δραστηριότητα μετά τη μετάπτωση.", "The object has post-migration activity."),
        ErrorDefinition.For(ModuleCode.MIG, NotReversible, 422, "Η δέσμη δεν αναιρείται", "The batch is not reversible")
            .Describe("Μονάδα της δέσμης δεν διαθέτει αναιρέτη.", "A module of the batch has no reverser."),
    ];
}

/// <summary>Request item of a batch.</summary>
internal sealed record BatchItemInput(string Module, string ObjectType, string LegacyId, JsonElement Payload);

/// <summary>Create-batch body.</summary>
internal sealed record CreateBatchRequest(string Name, IReadOnlyList<BatchItemInput> Items);

/// <summary>Batch view.</summary>
internal sealed record BatchView(
    Guid BatchId,
    string Name,
    string Status,
    string? BaselineHash,
    string ContentHash,
    bool DryRunDone,
    string? StartedBy,
    Guid? ApprovalRequestId,
    int ItemCount,
    IReadOnlyDictionary<string, int> Counts);

/// <summary>One item outcome.</summary>
internal sealed record OutcomeView(int Seq, string Module, string ObjectType, string LegacyId, string Outcome, Guid? CoreId, string? ErrorCode, string? Message);

/// <summary>Response of the batch operations.</summary>
internal sealed record BatchResponse(BatchView Batch, IReadOnlyList<OutcomeView> Outcomes);

/// <summary>Page of outcomes.</summary>
internal sealed record OutcomesPage(IReadOnlyList<OutcomeView> Items, string? NextCursor, int Limit);

/// <summary>Start body.</summary>
internal sealed record StartBatchRequest(Guid BatchId, bool DryRun);

/// <summary>Batch id body.</summary>
internal sealed record BatchIdRequest(Guid BatchId);

/// <summary>Reverse body.</summary>
internal sealed record ReverseBatchRequest(Guid BatchId, bool DryRun);

/// <summary>Approval decision body.</summary>
internal sealed record ApproveBatchRequest(Guid BatchId, string Decision, string? Comment);

/// <summary>Xref bodies.</summary>
internal sealed record XrefRegisterBody(string Source, string ObjectType, string LegacyKey, Guid CoreId, Guid? BatchId);

internal sealed record XrefResolveBody(string ObjectType, string? Source, string? LegacyKey, Guid? CoreId);

internal sealed record XrefView(Guid XrefId, string Source, string ObjectType, string LegacyKey, Guid CoreId, Guid? BatchId, string Origin, string Status);

internal sealed record XrefPage(IReadOnlyList<XrefView> Items, string? NextCursor, int Limit);

/// <summary>Routing bodies.</summary>
internal sealed record RoutingResolveBody(string ObjectType, string Key, string? Date);

internal sealed record RoutingOverrideBody(string ObjectType, string Key, string MasterState, string From, string? To, string Reason);

internal sealed record RoutingView(string MasterState, Guid? RouteId, string? ValidFrom, string? ValidTo, string? Reason);
