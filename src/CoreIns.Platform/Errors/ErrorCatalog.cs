using System.Collections.Concurrent;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Platform.Errors;

/// <summary>
/// How an error code appears to API callers: HTTP status, localized title (el/en) and whether a retry may succeed.
/// A definition is either for one code (<c>POL-ERR-QUOTE-EXPIRED</c>) or generic for a name used by every module
/// (<c>*-ERR-IDEMPOTENCY-MISMATCH</c>).
/// </summary>
/// <param name="Name">The part after <c>-ERR-</c>.</param>
/// <param name="Module">The owning module, or null for a generic definition.</param>
/// <param name="Status">HTTP status.</param>
/// <param name="Title">Localized title.</param>
/// <param name="Retryable">True when the same request may succeed later.</param>
public sealed record ErrorDefinition(string Name, ModuleCode? Module, int Status, LocalizedText Title, bool Retryable = false)
{
    /// <summary>What the error means and what the caller can do (shown on the <c>/problems/&lt;CODE&gt;</c> page).</summary>
    public LocalizedText? Description { get; init; }

    /// <summary>A copy with a description.</summary>
    public ErrorDefinition Describe(string descriptionEl, string descriptionEn) => this with { Description = new LocalizedText(descriptionEl, descriptionEn) };

    /// <summary>A definition for every module's <c>&lt;MOD&gt;-ERR-&lt;name&gt;</c>.</summary>
    public static ErrorDefinition Generic(string name, int status, string titleEl, string titleEn, bool retryable = false) =>
        new(name, null, status, new LocalizedText(titleEl, titleEn), retryable);

    /// <summary>A definition for one module's code.</summary>
    public static ErrorDefinition For(ModuleCode module, string name, int status, string titleEl, string titleEn, bool retryable = false) =>
        new(name, module, status, new LocalizedText(titleEl, titleEn), retryable);
}

/// <summary>Error names used by the platform primitives (generic across modules unless stated).</summary>
public static class PlatformErrors
{
    /// <summary>Request validation failed (400).</summary>
    public const string Validation = "VALIDATION";

    /// <summary>A state-changing request has no Idempotency-Key (400).</summary>
    public const string IdempotencyKeyRequired = "IDEMPOTENCY-KEY-REQUIRED";

    /// <summary>The Idempotency-Key is not a UUID (400).</summary>
    public const string IdempotencyKeyInvalid = "IDEMPOTENCY-KEY-INVALID";

    /// <summary>The key was used with a different request (409, contract §3.5.3).</summary>
    public const string IdempotencyMismatch = "IDEMPOTENCY-MISMATCH";

    /// <summary>The first request with this key is still running (409, retryable).</summary>
    public const string IdempotencyInProgress = "IDEMPOTENCY-IN-PROGRESS";

    /// <summary>An undeclared state transition (409).</summary>
    public const string InvalidStateTransition = "INVALID-STATE-TRANSITION";

    /// <summary>Authority check denied (403).</summary>
    public const string AuthorityDenied = "AUTHORITY-DENIED";

    /// <summary>Authority check referred to a higher authority (403; the referral target is in the metadata).</summary>
    public const string AuthorityReferral = "AUTHORITY-REFERRAL-REQUIRED";

    /// <summary>Not found (404).</summary>
    public const string NotFound = "NOT-FOUND";

    /// <summary>Optimistic concurrency conflict (409, retryable).</summary>
    public const string ConcurrencyConflict = "CONCURRENCY-CONFLICT";

    /// <summary>Unexpected server error (500).</summary>
    public const string Internal = "INTERNAL";

    /// <summary>PLT: an authority type is not registered (REQ-PLT-003).</summary>
    public const string UnknownAuthorityType = "UNKNOWN-TYPE";

    /// <summary>PLT: an authority type is registered twice.</summary>
    public const string AuthorityTypeExists = "TYPE-EXISTS";

    /// <summary>PLT: an AI actor needs the delegating person's decision (REQ-PLT-111).</summary>
    public const string HumanDecisionRequired = "HUMAN-DECISION-REQUIRED";
}

/// <summary>Registry of <see cref="ErrorDefinition"/>s. Modules add theirs at start-up; the platform's are built in.</summary>
public sealed class ErrorCatalog
{
    private readonly ConcurrentDictionary<string, ErrorDefinition> _exact = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ErrorDefinition> _generic = new(StringComparer.Ordinal);

    /// <summary>The fallback for unknown codes.</summary>
    public static ErrorDefinition Unknown { get; } = ErrorDefinition.Generic("UNKNOWN", 400, "Το αίτημα δεν ολοκληρώθηκε", "The request could not be completed");

    /// <summary>A catalog with the platform definitions only.</summary>
    public ErrorCatalog()
        : this([])
    {
    }

    /// <summary>A catalog with the platform definitions plus the modules' (registered with <c>AddErrorDefinitions</c>).</summary>
    public ErrorCatalog(IEnumerable<ErrorDefinition> moduleDefinitions)
    {
        ArgumentNullException.ThrowIfNull(moduleDefinitions);
        Register(ErrorDefinition.Generic(PlatformErrors.Validation, 400, "Τα στοιχεία του αιτήματος δεν είναι έγκυρα", "The request is not valid")
            .Describe(
                "Ένα ή περισσότερα πεδία δεν πέρασαν τον έλεγχο· το errors[] αναφέρει το πεδίο και τον λόγο. Διορθώστε τα και στείλτε ξανά.",
                "One or more fields failed validation; errors[] names each field and the reason. Correct them and send again."));
        Register(ErrorDefinition.Generic(PlatformErrors.IdempotencyKeyRequired, 400, "Λείπει η κεφαλίδα Idempotency-Key", "The Idempotency-Key header is required")
            .Describe(
                "Κάθε αίτημα που αλλάζει δεδομένα χρειάζεται μια κεφαλίδα Idempotency-Key με νέο UUID, ώστε μια επανάληψη να μην εκτελεστεί δύο φορές.",
                "Every state-changing request needs an Idempotency-Key header with a fresh UUID, so that a retry never executes twice."));
        Register(ErrorDefinition.Generic(PlatformErrors.IdempotencyKeyInvalid, 400, "Η κεφαλίδα Idempotency-Key πρέπει να είναι UUID", "The Idempotency-Key header must be a UUID")
            .Describe(
                "Η τιμή της κεφαλίδας Idempotency-Key πρέπει να είναι ένα μόνο UUID στη μορφή 8-4-4-4-12.",
                "The Idempotency-Key header value must be a single UUID in 8-4-4-4-12 form."));
        Register(ErrorDefinition.Generic(
            PlatformErrors.IdempotencyMismatch, 409,
            "Το Idempotency-Key χρησιμοποιήθηκε ήδη με διαφορετικό αίτημα", "The Idempotency-Key was already used with a different request")
            .Describe(
                "Το κλειδί έχει ήδη χρησιμοποιηθεί για άλλο αίτημα· τα αρχικά αποτελέσματα δεν άλλαξαν. Χρησιμοποιήστε νέο κλειδί για νέο αίτημα.",
                "The key was already used for a different request; the original result is unchanged. Use a new key for a new request."));
        Register(ErrorDefinition.Generic(
            PlatformErrors.IdempotencyInProgress, 409,
            "Το αρχικό αίτημα με αυτό το Idempotency-Key βρίσκεται ακόμη σε εξέλιξη", "The original request with this Idempotency-Key is still in progress",
            retryable: true)
            .Describe(
                "Το πρώτο αίτημα με αυτό το κλειδί δεν έχει ολοκληρωθεί. Δοκιμάστε ξανά σε λίγο με το ίδιο κλειδί για να λάβετε το αποτέλεσμά του.",
                "The first request with this key has not finished. Retry shortly with the same key to receive its result."));
        Register(ErrorDefinition.Generic(PlatformErrors.InvalidStateTransition, 409, "Η ενέργεια δεν επιτρέπεται στην τρέχουσα κατάσταση", "The action is not allowed in the current state")
            .Describe(
                "Ο κύκλος ζωής της εγγραφής δεν επιτρέπει αυτή την ενέργεια από την τρέχουσα κατάστασή της.",
                "The record's lifecycle does not allow this action from its current state."));
        Register(ErrorDefinition.Generic(PlatformErrors.AuthorityDenied, 403, "Δεν έχετε την απαιτούμενη εξουσιοδότηση", "You do not have the required authority")
            .Describe(
                "Δεν διαθέτετε εξουσιοδότηση αυτού του τύπου για την ενέργεια. Ο κωδικός λόγου εξηγεί γιατί.",
                "You hold no authority of this type for the action. The reason code explains why."));
        Register(ErrorDefinition.Generic(PlatformErrors.AuthorityReferral, 403, "Απαιτείται έγκριση από ανώτερη εξουσιοδότηση", "Approval by a higher authority is required")
            .Describe(
                "Η ενέργεια υπερβαίνει το όριό σας. Τα referralTargets δείχνουν ποιος μπορεί να την εγκρίνει.",
                "The action exceeds your limit. referralTargets shows who can approve it."));
        Register(ErrorDefinition.Generic(PlatformErrors.NotFound, 404, "Δεν βρέθηκε", "Not found")
            .Describe("Η εγγραφή δεν υπάρχει ή δεν έχετε πρόσβαση σε αυτήν.", "The record does not exist or is not visible to you."));
        Register(ErrorDefinition.Generic(PlatformErrors.ConcurrencyConflict, 409, "Η εγγραφή άλλαξε στο μεταξύ· δοκιμάστε ξανά", "The record changed meanwhile; try again", retryable: true)
            .Describe(
                "Κάποιος άλλος άλλαξε την εγγραφή στο μεταξύ. Φορτώστε την ξανά και επαναλάβετε.",
                "Someone else changed the record meanwhile. Reload it and try again."));
        Register(ErrorDefinition.Generic(PlatformErrors.Internal, 500, "Παρουσιάστηκε απρόσμενο σφάλμα", "An unexpected error occurred", retryable: true)
            .Describe(
                "Το σύστημα δεν ολοκλήρωσε το αίτημα και δεν έγινε καμία αλλαγή. Δοκιμάστε ξανά· αναφέρετε το traceId αν επιμένει.",
                "The system could not complete the request and nothing changed. Try again; quote the traceId if it persists."));
        Register(ErrorDefinition.For(ModuleCode.PLT, PlatformErrors.UnknownAuthorityType, 400, "Άγνωστος τύπος εξουσιοδότησης", "Unknown authority type")
            .Describe("Ο τύπος εξουσιοδότησης δεν έχει καταχωρηθεί από καμία ενότητα.", "No module has registered this authority type."));
        Register(ErrorDefinition.For(ModuleCode.PLT, PlatformErrors.AuthorityTypeExists, 409, "Ο τύπος εξουσιοδότησης υπάρχει ήδη", "The authority type already exists")
            .Describe("Μια άλλη ενότητα έχει ήδη καταχωρήσει τύπο εξουσιοδότησης με αυτόν τον κωδικό.", "Another module already registered an authority type with this code."));
        Register(ErrorDefinition.For(
            ModuleCode.PLT, PlatformErrors.HumanDecisionRequired, 403,
            "Απαιτείται απόφαση από εξουσιοδοτημένο πρόσωπο", "A decision by an authorised person is required")
            .Describe(
                "Μια ενέργεια με νομικό ή οικονομικό αποτέλεσμα δεν αποφασίζεται από τεχνητή νοημοσύνη· πρέπει να την επιβεβαιώσει εξουσιοδοτημένο πρόσωπο.",
                "An action with legal or financial effect is never decided by AI; an authorised person must confirm it."));
        foreach (var definition in moduleDefinitions)
        {
            Register(definition);
        }
    }

    /// <summary>Adds or replaces a definition.</summary>
    public void Register(ErrorDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Module is { } module)
        {
            _exact[ErrorCode.For(module, definition.Name).Value] = definition;
        }
        else
        {
            _generic[definition.Name] = definition;
        }
    }

    /// <summary>True when the code has its own or a generic definition (not the <see cref="Unknown"/> fallback).</summary>
    public bool IsKnown(ErrorCode code) => _exact.ContainsKey(code.Value) || _generic.ContainsKey(code.Name);

    /// <summary>The definition of a code: exact, then generic by name, then <see cref="Unknown"/>.</summary>
    public ErrorDefinition Find(ErrorCode code) =>
        _exact.TryGetValue(code.Value, out var exact) ? exact
        : _generic.TryGetValue(code.Name, out var generic) ? generic
        : Unknown;
}
