using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.Modules.Party.Persistence;

/// <summary>
/// <c>pty.consent</c> (BO-PTY-06, REQ-PTY-005, REQ-PTY-150): one consent record. Append-only: a withdrawal or an objection is a new row, never
/// an update; the current state of a purpose and channel is its newest row (<see cref="RecordedAt"/>, then <see cref="ConsentId"/>).
/// </summary>
internal sealed class ConsentRow
{
    public Guid ConsentId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public PartyId PartyId { get; set; }

    public string Purpose { get; set; } = string.Empty;

    public string Channel { get; set; } = string.Empty;

    /// <summary>GIVEN, WITHDRAWN or OBJECTION.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>GRANTED, WITHDRAWN or OBJECTED.</summary>
    public string State { get; set; } = string.Empty;

    public string Source { get; set; } = string.Empty;

    public string? WordingVersion { get; set; }

    public string ProofRef { get; set; } = string.Empty;

    public Instant RecordedAt { get; set; }

    public string RecordedBy { get; set; } = string.Empty;
}

/// <summary>
/// <c>pty.communication_preference</c> (BO-PTY-06): the channel, language and medium a party wants for a purpose (or <c>DEFAULT</c>).
/// Bitemporal: <see cref="PreferenceId"/> is the logical preference; a change closes the current rows in record time and inserts successors.
/// </summary>
internal sealed class CommunicationPreferenceRow : BitemporalRow
{
    public Guid RowId { get; set; }

    public Guid PreferenceId { get; set; }

    public string Purpose { get; set; } = string.Empty;

    public string Channel { get; set; } = string.Empty;

    public string Language { get; set; } = string.Empty;

    public string Medium { get; set; } = string.Empty;
}
