using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.SharedKernel.Dsar;

/// <summary>Outcome for one record class in a data-subject request (D-SL6-07). A participant never skips silently.</summary>
public enum DsarOutcomeKind
{
    /// <summary>Data was physically deleted.</summary>
    Erased,

    /// <summary>Identifying fields were irreversibly anonymised; the record remains.</summary>
    Anonymised,

    /// <summary>Data is retained under a legal basis and blocked from other processing.</summary>
    Restricted,

    /// <summary>The module holds no data for this subject in this record class.</summary>
    NotHeld,

    /// <summary>The module's data exists in the product but its participant is not built yet; blocks auto-completion.</summary>
    NotBuilt,
}

/// <summary>Retention markers.</summary>
public static class DsarRetention
{
    /// <summary>Retention duration not yet defined (F-404); data is retained rather than destroyed.</summary>
    public const string UnsetF404 = "UNSET_F404";
}

/// <summary>Who the request is about: the party and the other identifiers the modules may know it by. No PII in logs.</summary>
/// <param name="PartyId">The party at the centre of the request.</param>
/// <param name="KnownIdentifiers">Other identifiers by kind (for example "policyNumber"); values are never logged.</param>
public sealed record SubjectRef(PartyId PartyId, IReadOnlyDictionary<string, IReadOnlyList<string>> KnownIdentifiers)
{
    /// <summary>A subject known only by party id.</summary>
    public static SubjectRef ForParty(PartyId partyId) =>
        new(partyId, new Dictionary<string, IReadOnlyList<string>>());

    /// <summary>Redacted rendering safe for logs.</summary>
    public override string ToString() => $"Subject({PartyId.Value})";
}

/// <summary>Request-level context handed to every participant.</summary>
/// <param name="CaseId">The DSAR case.</param>
/// <param name="RequestedBy">Actor that triggered the step.</param>
/// <param name="ApprovedBy">DPO approving erase/restrict, not the case creator (D-SL6-07); null for export.</param>
public sealed record DsarContext(Guid CaseId, string RequestedBy, string? ApprovedBy);

/// <summary>Result for one record class. <see cref="Detail"/> is a neutral description, never PII.</summary>
public sealed record DsarRecordOutcome(
    string RecordClass,
    DsarOutcomeKind Kind,
    int RecordCount = 0,
    string? LegalBasis = null,
    string? RetentionEnd = null,
    string? Detail = null)
{
    /// <summary>Restricted outcome; retention end is <see cref="DsarRetention.UnsetF404"/> when not given.</summary>
    public static DsarRecordOutcome Restricted(string recordClass, string legalBasis, int count = 0, string? retentionEnd = null) =>
        new(recordClass, DsarOutcomeKind.Restricted, count, legalBasis, retentionEnd ?? DsarRetention.UnsetF404);

    /// <summary>Module holds nothing for the subject in this class.</summary>
    public static DsarRecordOutcome NotHeld(string recordClass) => new(recordClass, DsarOutcomeKind.NotHeld);

    /// <summary>Participant not built yet.</summary>
    public static DsarRecordOutcome NotBuilt(string recordClass) => new(recordClass, DsarOutcomeKind.NotBuilt);
}

/// <summary>One module's answer to a DSAR step.</summary>
/// <param name="Module">Module code.</param>
/// <param name="Outcomes">One entry per relevant record class; never silently empty.</param>
/// <param name="ExportPayload">Portable export (JSON text) for export steps, otherwise null.</param>
public sealed record DsarModuleResult(string Module, IReadOnlyList<DsarRecordOutcome> Outcomes, string? ExportPayload = null)
{
    /// <summary>True when any outcome is <see cref="DsarOutcomeKind.NotBuilt"/> (blocks auto-completion).</summary>
    public bool HasNotBuilt => Outcomes.Any(o => o.Kind == DsarOutcomeKind.NotBuilt);
}

/// <summary>
/// Contract by which a module takes part in a data-subject request. Implementations live only in module projects
/// (architecture test) and are registered in DI.
/// </summary>
public interface IDsarParticipant
{
    /// <summary>Module code, unique across participants (for example "BIL").</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Name fixed by the SL6 brief; 'Module' is not reserved in C#.")]
    string Module { get; }

    /// <summary>Collect the subject's data held by this module.</summary>
    Task<DsarModuleResult> ExportAsync(SubjectRef subject, DsarContext context, CancellationToken ct = default);

    /// <summary>Block further processing of the subject's data, keeping it under a legal basis.</summary>
    Task<DsarModuleResult> RestrictAsync(
        SubjectRef subject, string reason, string legalBasis, DateOnly? retentionEnd, CancellationToken ct = default);

    /// <summary>Erase or anonymise the subject's data, restricting regulated record classes instead (<see cref="DsarRetentionPolicy"/>).</summary>
    Task<DsarModuleResult> EraseAsync(SubjectRef subject, DsarContext context, CancellationToken ct = default);
}
