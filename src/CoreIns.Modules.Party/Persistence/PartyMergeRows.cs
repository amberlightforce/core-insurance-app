using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.Modules.Party.Persistence;

/// <summary>
/// <c>pty.party_merge</c> (BO-PTY-05, REQ-PTY-007): one merge of <see cref="MergedPartyId"/> into <see cref="SurvivorPartyId"/> and its
/// maker-checker state. A merge moves no data: the absorbed party only gets the status MERGED (with this row as its pointer), and
/// <see cref="PriorStatus"/> is what unmerge restores, so both parties come back exactly as they were. The row is updated in place only along
/// PENDING_APPROVAL to EXECUTED or REJECTED, then EXECUTED to UNMERGED.
/// </summary>
internal sealed class PartyMergeRow
{
    public Guid MergeId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public PartyId SurvivorPartyId { get; set; }

    public PartyId MergedPartyId { get; set; }

    public string Status { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    /// <summary>Status of the absorbed party when the merge was requested; set back on unmerge.</summary>
    public string PriorStatus { get; set; } = string.Empty;

    /// <summary>Hash of the merge content (ids and prior status) the checker approves (PLT payload hash).</summary>
    public string ContentHash { get; set; } = string.Empty;

    public Guid? ApprovalRequestId { get; set; }

    public string RequestedBy { get; set; } = string.Empty;

    public Instant RequestedAt { get; set; }

    public string? DecidedBy { get; set; }

    public Instant? DecidedAt { get; set; }

    public string? DecisionComment { get; set; }

    public Instant? ExecutedAt { get; set; }

    public Instant? UnmergeWindowEnd { get; set; }

    public Instant? UnmergedAt { get; set; }

    public string? UnmergedBy { get; set; }

    public string? UnmergeReason { get; set; }
}
