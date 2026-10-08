namespace CoreIns.Modules.Market.Contracts.Spi;

/// <summary>
/// SPI 23 <c>FriendlySettlementClearing</c> (contract §3.5.8; PRD-17 §9.4.23 extended by §9.4.43, CCR-CLM-04 / R-42;
/// spi.md §23): Friendly Settlement (inter-insurer clearing) eligibility, submission, clearing notifications and
/// settlement statements. Binding axis RISK_LOCATION. Mode A (worker-hosted adapter), idempotent on the caller's key,
/// 30 s, queued on outage. Core default: <see cref="FsEligibilityResult.NotApplicable"/> (Cyprus). Caller: CLM
/// (REQ-CLM-155…REQ-CLM-160, REQ-CLM-264). The capability switch is <see cref="CapabilityKey"/>: when it is off the
/// SPI is not called and CLM answers <c>NotApplicable</c>; in Production the SPI is unbound and Friendly Settlement
/// fails closed with <c>CLM-ERR-FS-DISABLED</c> (D-SL4-03).
/// </summary>
/// <remarks>
/// <para>
/// Slice 4 (SL4-CONTRACTS, D-SL4-02, D-SL4-03): the Greece pack binds a stub, never in Production, with a documented
/// JSON test statement format, a member list, a provisional eligibility limit (<c>legalStatus UNVERIFIED</c>) and the
/// clearing value basis <see cref="ClearingValueBasis.Actual"/>. No value is presented as the market's.
/// </para>
/// <para>
/// CLM never moves cash through this SPI: <see cref="SettlementStatementAsync"/> returns lines and the net per
/// counterparty only; the net cash goes through BIL <c>FS_CLEARING</c> (D1).
/// </para>
/// <para>
/// <see cref="SubmitDisputeAsync"/> and <see cref="RecordReplyAsync"/> are typed here so the contract does not change
/// later; they are <b>not implemented in slice 4</b> (disputes and the counterparty reply clock are out, D-SL4-01):
/// an implementation throws <see cref="SpiException"/> with category <see cref="SpiErrorCategory.NotApplicable"/> and
/// code <c>FS_DISPUTE_NOT_IMPLEMENTED</c>, and CLM does not call them.
/// </para>
/// </remarks>
public interface IFriendlySettlementClearing
{
    /// <summary>Capability key that switches Friendly Settlement on per jurisdiction (REQ-CLM-156, REQ-MKT-004).</summary>
    public const string CapabilityKey = "cap.clm.friendly_settlement";

    /// <summary><c>evaluateEligibility(claimFacts) → result</c> (REQ-CLM-155, REQ-MKT-313). Mode S in effect: no side effects.</summary>
    ValueTask<FsEligibilityDecision> EvaluateEligibilityAsync(
        FsClaimFacts claimFacts, CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>submit(agreedClaim) → clearing reference</c> (REQ-CLM-159, submit part). Idempotent on the case id: a second
    /// call with the same <see cref="FsSubmission.FsCaseId"/> returns the same reference.
    /// </summary>
    ValueTask<FsSubmissionResult> SubmitAsync(
        FsSubmission submission, CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>receiveNotification(message) → typed notification</c> (REQ-CLM-158): parses and validates an inbound clearing
    /// message from the at-fault leg. The transport is outside the SPI: in slice 4 the message enters through the
    /// Development-only <c>POST /dev/fs-clearing/notifications</c> endpoint (D-SL4-18).
    /// </summary>
    ValueTask<FsNotification> ReceiveNotificationAsync(
        FsNotificationMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>settlementStatement(period, counterparty?) → statement</c> (REQ-CLM-160): the lines and the net per
    /// counterparty of a period. CLM matches the lines to its cases; this call moves no cash.
    /// </summary>
    ValueTask<FsSettlementStatement> SettlementStatementAsync(
        FsStatementPeriod period, string? counterpartyCode = null, CancellationToken cancellationToken = default);

    /// <summary><c>submitDispute(receivableRef, reason)</c>. Typed only; not implemented in slice 4 (see remarks).</summary>
    ValueTask<FsDisputeResult> SubmitDisputeAsync(
        FsDisputeSubmission dispute, CancellationToken cancellationToken = default);

    /// <summary><c>recordReply(disputeRef, decision)</c>. Typed only; not implemented in slice 4 (see remarks).</summary>
    ValueTask<FsDisputeResult> RecordReplyAsync(
        FsReplyRecord reply, CancellationToken cancellationToken = default);
}

/// <summary>Result of <c>evaluateEligibility</c>.</summary>
public enum FsEligibilityResult
{
    /// <summary>The claim can be settled through the clearing house.</summary>
    Eligible,

    /// <summary>The claim cannot be settled through the clearing house; <see cref="FsEligibilityDecision.Reasons"/> say why.</summary>
    NotEligible,

    /// <summary>The pack has no Friendly Settlement (Cyprus stub); CLM treats it as capability off.</summary>
    NotApplicable,
}

/// <summary>Basis of the clearing value (PRD-17 §9.4.23): the actual repair/settlement amount or the market average.</summary>
public enum ClearingValueBasis
{
    /// <summary>The actual amount paid; the only basis of the slice-4 stub (averages are unknown, D-SL4-03).</summary>
    Actual,

    /// <summary>The market's average cost per claim, when the agreement defines averages.</summary>
    Average,
}

/// <summary>Direction of a statement line or net, seen from the legal entity.</summary>
public enum FsDirection
{
    /// <summary>The counterparty owes the legal entity (own-insurer legs).</summary>
    Receivable,

    /// <summary>The legal entity owes the counterparty (at-fault legs).</summary>
    Payable,
}

/// <summary>Role of the legal entity in a case.</summary>
public enum FsRole
{
    OwnInsurer,
    AtFaultInsurer,
}

/// <summary>
/// The claim facts an eligibility check needs (REQ-CLM-155). Built by CLM from the claim and its liability facts
/// (D-SL4-17); contains no personal data, no plate and no driver identity (PITFALLS 18).
/// </summary>
public sealed record FsClaimFacts
{
    public required Guid LegalEntityId { get; init; }

    public required Guid ClaimId { get; init; }

    /// <summary>Date of the accident in the Europe/Athens zone.</summary>
    public required DateOnly AccidentDate { get; init; }

    /// <summary>Product line, for example MOTOR.</summary>
    public required string ProductLine { get; init; }

    /// <summary>Company code of the counterparty insurer from the pack member list; null when unknown.</summary>
    public string? CounterpartyInsurerCode { get; init; }

    public required bool AccidentInCountry { get; init; }

    public required int VehicleCount { get; init; }

    /// <summary>Insured driver's fault in percent (0 to 100).</summary>
    public required decimal InsuredFaultPercent { get; init; }

    /// <summary>Damage types of the claim, for example MATERIAL_DAMAGE, BODILY_INJURY.</summary>
    public required IReadOnlyList<string> DamageTypes { get; init; }

    /// <summary>The amount to be cleared (the settlement or repair cost for the own insured).</summary>
    public required SpiMoney Amount { get; init; }

    public required bool JointReport { get; init; }
}

/// <summary>Result of <c>evaluateEligibility</c> (REQ-CLM-155, REQ-MKT-313).</summary>
/// <param name="Result">Eligible, not eligible or not applicable.</param>
/// <param name="Reasons">Reason codes (for example COUNTERPARTY_NOT_MEMBER, AMOUNT_ABOVE_LIMIT); empty when eligible. Always set.</param>
/// <param name="RuleId">Rule that decided; always set.</param>
/// <param name="RuleVersion">Version of that rule; always set.</param>
/// <param name="LegalStatus">Legal status of the values used; always set. A value that is not <see cref="LegalStatus.Settled"/> is refused in Production (PITFALLS 36).</param>
/// <param name="Provisional">True while <paramref name="LegalStatus"/> is not Settled; always set.</param>
/// <param name="ClearingValue">Clearing value of the claim; set when eligible.</param>
/// <param name="ClearingValueBasis">Basis of <paramref name="ClearingValue"/>; set when eligible.</param>
public sealed record FsEligibilityDecision(
    FsEligibilityResult Result,
    IReadOnlyList<string> Reasons,
    string RuleId,
    string RuleVersion,
    LegalStatus LegalStatus,
    bool Provisional,
    SpiMoney? ClearingValue,
    ClearingValueBasis? ClearingValueBasis);

/// <summary>The agreed claim submitted to the clearing house (<c>submit</c>).</summary>
public sealed record FsSubmission
{
    public required Guid LegalEntityId { get; init; }

    /// <summary>CLM case id: the idempotency key of the submission.</summary>
    public required Guid FsCaseId { get; init; }

    public required Guid ClaimId { get; init; }

    public required string CounterpartyInsurerCode { get; init; }

    public required DateOnly AccidentDate { get; init; }

    /// <summary>The clearing value from the stored eligibility result.</summary>
    public required SpiMoney ClearingValue { get; init; }

    public required ClearingValueBasis ClearingValueBasis { get; init; }
}

/// <summary>Result of <c>submit</c>.</summary>
/// <param name="ClearingReference">Reference of the clearing house for the case; always set.</param>
/// <param name="SubmittedAt">When the submission was accepted.</param>
public sealed record FsSubmissionResult(string ClearingReference, DateTimeOffset SubmittedAt);

/// <summary>An inbound clearing message (the at-fault notification). The body format is pack-specific.</summary>
/// <param name="Format">Format code of the message, for example the stub's JSON test format.</param>
/// <param name="Body">The raw message.</param>
public sealed record FsNotificationMessage(string Format, ReadOnlyMemory<byte> Body);

/// <summary>
/// A typed clearing notification (<c>receiveNotification</c>, REQ-CLM-158): the other insurer states that its insured
/// was hit by our insured, so a claim exists on our policy and the legal entity owes the clearing value.
/// </summary>
public sealed record FsNotification
{
    /// <summary>Clearing reference assigned by the clearing house; the idempotency key of CLM's handling.</summary>
    public required string ClearingReference { get; init; }

    public required string CounterpartyInsurerCode { get; init; }

    /// <summary>The legal entity's policy number the notification names.</summary>
    public required string PolicyNumber { get; init; }

    public required DateOnly AccidentDate { get; init; }

    public required SpiMoney ClearingValue { get; init; }

    public required ClearingValueBasis ClearingValueBasis { get; init; }

    public required FsRole Role { get; init; }

    /// <summary>Damage types the notification states, for example MATERIAL_DAMAGE.</summary>
    public required IReadOnlyList<string> DamageTypes { get; init; }
}

/// <summary>A statement period: a calendar month, half-open <c>[From, To)</c> in Europe/Athens dates.</summary>
public readonly record struct FsStatementPeriod(DateOnly From, DateOnly To);

/// <summary>A line of a settlement statement.</summary>
/// <param name="ClearingReference">Clearing reference of the case the line belongs to.</param>
/// <param name="CounterpartyCode">Counterparty insurer company code.</param>
/// <param name="Direction">Receivable or payable for the legal entity.</param>
/// <param name="Amount">Positive amount of the line.</param>
/// <param name="LineRef">Reference of the line within the statement.</param>
public sealed record FsStatementLine(
    string ClearingReference, string CounterpartyCode, FsDirection Direction, SpiMoney Amount, string LineRef);

/// <summary>The net of one counterparty on a statement: Σ receivable lines − Σ payable lines.</summary>
/// <param name="CounterpartyCode">Counterparty insurer company code.</param>
/// <param name="Direction">Payable when the legal entity owes the net, receivable when it is owed.</param>
/// <param name="Amount">Absolute net amount.</param>
public sealed record FsCounterpartyNet(string CounterpartyCode, FsDirection Direction, SpiMoney Amount);

/// <summary>Result of <c>settlementStatement</c> (REQ-CLM-160).</summary>
/// <param name="StatementId">Statement id assigned by the clearing house (pack format); stable for a period.</param>
/// <param name="Period">Statement period.</param>
/// <param name="Lines">Statement lines.</param>
/// <param name="Nets">Net per counterparty.</param>
public sealed record FsSettlementStatement(
    string StatementId, FsStatementPeriod Period, IReadOnlyList<FsStatementLine> Lines, IReadOnlyList<FsCounterpartyNet> Nets);

/// <summary>Input of <c>submitDispute</c>. Typed only; not implemented in slice 4.</summary>
/// <param name="ReceivableRef">Clearing reference or statement line reference disputed.</param>
/// <param name="ReasonCode">Dispute reason code.</param>
public sealed record FsDisputeSubmission(string ReceivableRef, string ReasonCode);

/// <summary>Input of <c>recordReply</c>. Typed only; not implemented in slice 4.</summary>
/// <param name="DisputeRef">Dispute reference returned by <c>submitDispute</c>.</param>
/// <param name="Decision">Counterparty decision code (for example ACCEPTED, REJECTED).</param>
public sealed record FsReplyRecord(string DisputeRef, string Decision);

/// <summary>Result of a dispute operation. Typed only; not implemented in slice 4.</summary>
/// <param name="DisputeRef">Dispute reference.</param>
/// <param name="Status">Dispute status code.</param>
public sealed record FsDisputeResult(string DisputeRef, string Status);
