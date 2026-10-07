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
        Register(ErrorDefinition.Generic(PlatformErrors.Validation, 400, "Τα στοιχεία του αιτήματος δεν είναι έγκυρα", "The request is not valid"));
        Register(ErrorDefinition.Generic(PlatformErrors.IdempotencyKeyRequired, 400, "Λείπει η κεφαλίδα Idempotency-Key", "The Idempotency-Key header is required"));
        Register(ErrorDefinition.Generic(PlatformErrors.IdempotencyKeyInvalid, 400, "Η κεφαλίδα Idempotency-Key πρέπει να είναι UUID", "The Idempotency-Key header must be a UUID"));
        Register(ErrorDefinition.Generic(
            PlatformErrors.IdempotencyMismatch, 409,
            "Το Idempotency-Key χρησιμοποιήθηκε ήδη με διαφορετικό αίτημα", "The Idempotency-Key was already used with a different request"));
        Register(ErrorDefinition.Generic(
            PlatformErrors.IdempotencyInProgress, 409,
            "Το αρχικό αίτημα με αυτό το Idempotency-Key βρίσκεται ακόμη σε εξέλιξη", "The original request with this Idempotency-Key is still in progress",
            retryable: true));
        Register(ErrorDefinition.Generic(PlatformErrors.InvalidStateTransition, 409, "Η ενέργεια δεν επιτρέπεται στην τρέχουσα κατάσταση", "The action is not allowed in the current state"));
        Register(ErrorDefinition.Generic(PlatformErrors.AuthorityDenied, 403, "Δεν έχετε την απαιτούμενη εξουσιοδότηση", "You do not have the required authority"));
        Register(ErrorDefinition.Generic(PlatformErrors.AuthorityReferral, 403, "Απαιτείται έγκριση από ανώτερη εξουσιοδότηση", "Approval by a higher authority is required"));
        Register(ErrorDefinition.Generic(PlatformErrors.NotFound, 404, "Δεν βρέθηκε", "Not found"));
        Register(ErrorDefinition.Generic(PlatformErrors.ConcurrencyConflict, 409, "Η εγγραφή άλλαξε στο μεταξύ· δοκιμάστε ξανά", "The record changed meanwhile; try again", retryable: true));
        Register(ErrorDefinition.Generic(PlatformErrors.Internal, 500, "Παρουσιάστηκε απρόσμενο σφάλμα", "An unexpected error occurred", retryable: true));
        Register(ErrorDefinition.For(ModuleCode.PLT, PlatformErrors.UnknownAuthorityType, 400, "Άγνωστος τύπος εξουσιοδότησης", "Unknown authority type"));
        Register(ErrorDefinition.For(ModuleCode.PLT, PlatformErrors.AuthorityTypeExists, 409, "Ο τύπος εξουσιοδότησης υπάρχει ήδη", "The authority type already exists"));
        Register(ErrorDefinition.For(
            ModuleCode.PLT, PlatformErrors.HumanDecisionRequired, 403,
            "Απαιτείται απόφαση από εξουσιοδοτημένο πρόσωπο", "A decision by an authorised person is required"));
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

    /// <summary>The definition of a code: exact, then generic by name, then <see cref="Unknown"/>.</summary>
    public ErrorDefinition Find(ErrorCode code) =>
        _exact.TryGetValue(code.Value, out var exact) ? exact
        : _generic.TryGetValue(code.Name, out var generic) ? generic
        : Unknown;
}
