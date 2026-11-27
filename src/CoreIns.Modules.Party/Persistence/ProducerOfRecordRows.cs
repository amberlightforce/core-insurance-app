using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.Modules.Party.Persistence;

/// <summary>
/// <c>pty.producer_of_record</c> (BO-PTY-09, REQ-PTY-218): one segment of the producer of record of a policy, valid from
/// <see cref="ValidFrom"/> until <see cref="ValidTo"/> (null = open ended). Append-only: a change or a cancellation closes the superseded
/// rows (<see cref="RecordedTo"/>) and inserts their successors; no other column of an existing row ever changes. The policy and term ids
/// are POL's, held without a foreign key (no cross-schema SQL).
/// </summary>
internal sealed class ProducerOfRecordRow
{
    public Guid PorId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public PolicyId PolicyId { get; set; }

    /// <summary>The term in whose context the change was made.</summary>
    public PolicyTermId PolicyTermId { get; set; }

    public IntermediaryId IntermediaryId { get; set; }

    public string ProducerCode { get; set; } = string.Empty;

    public BusinessDate ValidFrom { get; set; }

    public BusinessDate? ValidTo { get; set; }

    /// <summary>Reason code (BROKER_OF_RECORD_LETTER, INTERMEDIARY_LEFT, ...).</summary>
    public string Reason { get; set; } = string.Empty;

    public string? Note { get; set; }

    public string CommissionConsequence { get; set; } = string.Empty;

    public decimal? SplitShare { get; set; }

    public string? LetterReference { get; set; }

    public Guid? TransferId { get; set; }

    public Instant RecordedFrom { get; set; }

    public Instant? RecordedTo { get; set; }

    public string CreatedBy { get; set; } = string.Empty;
}
