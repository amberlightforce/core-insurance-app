using CoreIns.SharedKernel;

namespace CoreIns.Modules.Reinsurance.Persistence;

/// <summary><c>ri.claim_view</c>: RI's own view of a claim, built from CLM events only (REQ-RI-113). Party ids only, no personal data.</summary>
internal sealed class ClaimViewRow
{
    public Guid ClaimId { get; set; }

    public Guid LegalEntityId { get; set; }

    public Guid? PolicyId { get; set; }

    public string? ProductCode { get; set; }

    public BusinessDate? LossDate { get; set; }

    public bool Closed { get; set; }

    /// <summary>Sequence of the last claim-level state event applied (close / reopen); lower or equal is a no-op (D-SL4-19).</summary>
    public long ClosedSequence { get; set; }

    /// <summary>Highest aggregate sequence applied to this claim.</summary>
    public long LastSequence { get; set; }

    public Instant UpdatedAt { get; set; }
}

/// <summary><c>ri.claim_exposure</c>: an exposure of the claim and the contract it falls under (null = out of scope or not yet resolved).</summary>
internal sealed class ClaimExposureRow
{
    public Guid ClaimId { get; set; }

    public Guid ExposureId { get; set; }

    public string? Kind { get; set; }

    public string? CoverageCode { get; set; }

    public string? SiiLob { get; set; }

    public Guid? ContractId { get; set; }
}

/// <summary><c>ri.claim_amount</c>: amounts per claim x exposure x cost type, EUR (REQ-RI-113).</summary>
internal sealed class ClaimAmountRow
{
    public Guid ClaimId { get; set; }

    public Guid ExposureId { get; set; }

    public string CostType { get; set; } = string.Empty;

    public decimal Paid { get; set; }

    public decimal OpenReserve { get; set; }

    public decimal RecoveryOpen { get; set; }

    public decimal Realised { get; set; }

    /// <summary>Sequence of the last reserve event applied to the open amount of this line (older ones are ignored).</summary>
    public long ReserveSequence { get; set; }

    /// <summary>Sequence of the last recovery-reserve event applied to this line.</summary>
    public long RecoverySequence { get; set; }
}

/// <summary><c>ri.recovery_batch</c>: one recalculation of a contract year; sealed (append-only). Holds the trace (REQ-RI-132).</summary>
internal sealed class RecoveryBatchRow
{
    public Guid BatchId { get; set; }

    public Guid LegalEntityId { get; set; }

    public Guid ContractId { get; set; }

    public int ContractYear { get; set; }

    public Guid ClaimId { get; set; }

    public Guid SourceEventId { get; set; }

    public Instant RecordedAt { get; set; }

    public string CalcHash { get; set; } = string.Empty;

    public string EngineVersion { get; set; } = string.Empty;

    public string ContractVersion { get; set; } = string.Empty;

    public string Trace { get; set; } = "[]";
}

/// <summary><c>ri.recovery</c>: a sealed delta row, occurrence (claim) x layer x participant (REQ-RI-003, -131). The booked total is the sum of rows.</summary>
internal sealed class RecoveryRow
{
    public Guid RecoveryId { get; set; }

    public Guid BatchId { get; set; }

    public Guid LegalEntityId { get; set; }

    public Guid ContractId { get; set; }

    public int ContractYear { get; set; }

    public Guid ClaimId { get; set; }

    public Guid LayerId { get; set; }

    public int LayerNo { get; set; }

    public Guid ParticipantPartyId { get; set; }

    public decimal IncurredDelta { get; set; }

    public decimal PaidDelta { get; set; }

    public decimal OutstandingDelta { get; set; }

    public string State { get; set; } = "CALCULATED";

    public string SiiLob { get; set; } = "UNMAPPED";

    public Instant RecordedAt { get; set; }
}

/// <summary><c>ri.intake_exception</c>: append-only log of what RI could not apply (overlapping contracts, an engine refusal, bad input).</summary>
internal sealed class IntakeExceptionRow
{
    public Guid ExceptionId { get; set; }

    public Guid LegalEntityId { get; set; }

    public Guid ClaimId { get; set; }

    public Guid? ContractId { get; set; }

    public string Code { get; set; } = string.Empty;

    public string Detail { get; set; } = string.Empty;

    public Guid? SourceEventId { get; set; }

    public Instant RecordedAt { get; set; }
}

/// <summary><c>ri.claim_event</c>: the CLM events already applied to the claim view, so a redelivery or replay of an event id changes nothing (REQ-RI-113).</summary>
internal sealed class ClaimEventRow
{
    public Guid EventId { get; set; }

    public Guid ClaimId { get; set; }

    public long AggregateSequence { get; set; }

    public Instant AppliedAt { get; set; }
}
