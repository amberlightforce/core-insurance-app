namespace CoreIns.Modules.Reinsurance.Domain.Recovery;

/// <summary>One excess-of-loss layer (REQ-RI-123, -125). A null <see cref="Limit"/> is an unlimited layer, a null <see cref="Aal"/> an unlimited annual aggregate.</summary>
internal sealed record XolLayer(string LayerId, decimal Attachment, decimal? Limit, decimal Aad = 0m, decimal? Aal = null);

/// <summary>A participant's signed line as a fraction of the placed cover (0.60 = 60%). Exactly one participant is the lead and takes the rounding residual (D-SL4-05).</summary>
internal sealed record Participation(string ParticipantId, decimal SignedLine, bool IsLead);

/// <summary>UNL clause flags (BR-RI-028/029). ExpenseUnallocated is never an input. Default: realised recoveries only (REQ-RI-117).</summary>
internal sealed record UnlClause(bool IncludeAlae, bool IncludeStatutoryInterest, bool AnticipatedRecoveriesInure = false);

/// <summary>Contract terms for one contract year. <see cref="Round"/> is the MKT rounding rule (PITFALLS 11); it is mandatory.</summary>
internal sealed record ContractTerms(
    IReadOnlyList<XolLayer> Layers,
    UnlClause Clause,
    IReadOnlyList<Participation> Participations,
    decimal PlacedPct,
    Func<decimal, decimal> Round);

/// <summary>Per-claim totals of the in-scope cost types (D-SL4-05). Open parts must be zero on a closed claim (REQ-RI-122).</summary>
internal sealed record ClaimAmounts(
    decimal IndemnityPaid,
    decimal IndemnityOpen,
    decimal AlaePaid,
    decimal AlaeOpen,
    decimal InterestPaid,
    decimal InterestOpen,
    decimal RealisedRecoveries,
    decimal OpenRecoveryReserve,
    bool Closed);

/// <summary>An occurrence (= one claim, per risk; D-SL4-05) of a contract year.</summary>
internal sealed record RecoveryOccurrence(string OccurrenceId, DateOnly OccurrenceDate, ClaimAmounts Claim);

/// <summary>Cumulative recoverable (booked or target) for one occurrence x layer x participant. Outstanding = Incurred - Paid.</summary>
internal sealed record RecoverableRow(string OccurrenceId, string LayerId, string ParticipantId, decimal Incurred, decimal Paid, decimal Outstanding);

internal sealed record RecoveryInput(ContractTerms Terms, IReadOnlyList<RecoveryOccurrence> Occurrences, IReadOnlyList<RecoverableRow> Booked);

/// <summary>Typed refusal for an impossible input (PITFALLS 10, 42): the engine never clamps or guesses.</summary>
internal sealed record RecoveryError(string Code, string Message);

/// <summary>UNL build-up on one basis, for the trace (REQ-RI-132).</summary>
internal sealed record UnlTrace(
    decimal Indemnity,
    decimal Alae,
    decimal Interest,
    decimal RealisedRecoveries,
    decimal AnticipatedRecoveryReserveApplied,
    decimal UnlBeforeFloor,
    decimal Unl);

/// <summary>Aggregate position on one basis: cumulative layer loss and aggregate recovery R(C) before and after the occurrence.</summary>
internal sealed record AggregatePosition(decimal CumulativeBefore, decimal CumulativeAfter, decimal RecoveryBefore, decimal RecoveryAfter);

/// <summary>Calculation trace per occurrence and layer (REQ-RI-132).</summary>
internal sealed record LayerTrace(
    string OccurrenceId,
    DateOnly OccurrenceDate,
    string LayerId,
    ClaimAmounts Inputs,
    UnlTrace UnlIncurred,
    UnlTrace UnlPaid,
    decimal Attachment,
    decimal? Limit,
    decimal Aad,
    decimal? Aal,
    decimal LayerLossIncurred,
    decimal LayerLossPaid,
    AggregatePosition AggregateIncurred,
    AggregatePosition AggregatePaid,
    decimal RecoverableIncurred,
    decimal RecoverablePaid,
    decimal RecoverableOutstanding,
    decimal PlacedPct,
    decimal PlacedIncurred,
    decimal PlacedPaid,
    string EngineVersion);

internal sealed record RecoveryResult(
    IReadOnlyList<RecoverableRow> Targets,
    IReadOnlyList<RecoverableRow> Deltas,
    IReadOnlyList<LayerTrace> Traces);

internal sealed record RecoveryOutcome(RecoveryResult? Result, IReadOnlyList<RecoveryError> Errors)
{
    public bool IsSuccess => Result is not null && Errors.Count == 0;
}
