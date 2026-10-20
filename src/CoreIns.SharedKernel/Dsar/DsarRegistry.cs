namespace CoreIns.SharedKernel.Dsar;

/// <summary>All participants known to the host, built from the DI enumeration. Duplicate module codes fail fast.</summary>
public sealed class DsarParticipantRegistry
{
    private readonly Dictionary<string, IDsarParticipant> _byModule;

    /// <summary>Build from the registered participants.</summary>
    public DsarParticipantRegistry(IEnumerable<IDsarParticipant> participants)
    {
        _byModule = new Dictionary<string, IDsarParticipant>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in participants)
        {
            if (string.IsNullOrWhiteSpace(p.Module))
            {
                throw new InvalidOperationException("DSAR participant without a module code.");
            }

            if (!_byModule.TryAdd(p.Module, p))
            {
                throw new InvalidOperationException($"Duplicate DSAR participant for module {p.Module}.");
            }
        }
    }

    /// <summary>Participants ordered by module code.</summary>
    public IReadOnlyList<IDsarParticipant> All => _byModule.Values.OrderBy(p => p.Module, StringComparer.Ordinal).ToList();

    /// <summary>Resolve one module's participant or null.</summary>
    public IDsarParticipant? Find(string module) => _byModule.GetValueOrDefault(module);

    /// <summary>Modules in <paramref name="expectedModules"/> without a participant (to be reported NOT_BUILT, never skipped).</summary>
    public IReadOnlyList<string> Missing(IEnumerable<string> expectedModules) =>
        expectedModules.Where(m => !_byModule.ContainsKey(m)).ToList();
}

/// <summary>Whether a record class may be erased or is restrict-only (D-SL6-07, F-404).</summary>
public sealed class DsarRetentionPolicy
{
    /// <summary>Record class: ledger / journal entries.</summary>
    public const string Ledger = "LEDGER";

    /// <summary>Record class: invoices.</summary>
    public const string Invoice = "INVOICE";

    /// <summary>Record class: claim files.</summary>
    public const string ClaimFile = "CLAIM_FILE";

    /// <summary>Record class: policy documents.</summary>
    public const string PolicyDocument = "POLICY_DOCUMENT";

    /// <summary>Record class: fiscal documents.</summary>
    public const string FiscalDocument = "FISCAL_DOCUMENT";

    /// <summary>Record class: bureau facts.</summary>
    public const string BureauFact = "BUREAU_FACT";

    /// <summary>Default restrict-only classes. Retention durations are unset (F-404), so these are retained, not destroyed.</summary>
    public static readonly IReadOnlyList<string> DefaultRestrictOnly =
        [Ledger, Invoice, ClaimFile, PolicyDocument, FiscalDocument, BureauFact];

    private readonly HashSet<string> _restrictOnly;

    /// <summary>Policy with the default restrict-only classes.</summary>
    public DsarRetentionPolicy()
        : this(DefaultRestrictOnly)
    {
    }

    /// <summary>Policy from a configured table of restrict-only record classes.</summary>
    public DsarRetentionPolicy(IEnumerable<string> restrictOnlyClasses) =>
        _restrictOnly = new HashSet<string>(restrictOnlyClasses, StringComparer.OrdinalIgnoreCase);

    /// <summary>Outcome detail for a record class an erase request could not erase because a retention hold applies.</summary>
    public static string NotErasedDetail(string? legalBasis) =>
        "NOT_ERASED: retained under retention hold" + (string.IsNullOrWhiteSpace(legalBasis) ? string.Empty : " (" + legalBasis + ")");

    /// <summary>True when the class must be restricted and never erased.</summary>
    public bool IsRestrictOnly(string recordClass) => _restrictOnly.Contains(recordClass);

    /// <summary>Retention end for a restrict-only class (always <see cref="DsarRetention.UnsetF404"/> until F-404 is answered).</summary>
    public string RetentionEnd(string recordClass) =>
        IsRestrictOnly(recordClass) ? DsarRetention.UnsetF404 : string.Empty;

    /// <summary>Fail-closed erase decision: restrict-only classes yield a Restricted outcome; null means erase/anonymise is allowed.</summary>
    public DsarRecordOutcome? RestrictInsteadOfErase(string recordClass, string legalBasis, int count = 0) =>
        IsRestrictOnly(recordClass) ? DsarRecordOutcome.Restricted(recordClass, legalBasis, count) : null;
}
