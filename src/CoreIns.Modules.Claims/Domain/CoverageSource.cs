using CoreIns.SharedKernel;

namespace CoreIns.Modules.Claims.Domain;

/// <summary>
/// What CLM needs from the POL snapshot valid at the loss date (REQ-CLM-002, REQ-POL-007). Read through
/// <see cref="Services.PolicySnapshotAdapter"/> from the generated <c>IPolicySnapshotService</c>; CLM keeps only the
/// reference and a display copy, never re-reads policy data for decisions (PRD-07 §7.0).
/// </summary>
/// <param name="SnapshotRef">Opaque immutable reference (encodes policy, segment, validAt, knownAt).</param>
/// <param name="ValidAt">The resolved valid instant (the loss instant).</param>
/// <param name="KnownAt">The resolved record instant (now at FNOL).</param>
/// <param name="InForce">True when a term was in force at the loss instant.</param>
/// <param name="Status">POL term state at the loss instant (IN_FORCE, CANCELLED, EXPIRED, …), when POL gives one.</param>
/// <param name="NotInForceReason">NO_TERM_AT_INSTANT or TERM_NOT_IN_FORCE when not in force.</param>
/// <param name="PolicyId">Policy id.</param>
/// <param name="PolicyNumber">Policy number.</param>
/// <param name="ProductCode">Product code.</param>
/// <param name="ProductVersion">Product version of the term, when known.</param>
/// <param name="InsuredPartyId">PTY party of the insured.</param>
/// <param name="SegmentId">Segment of the snapshot, when in force.</param>
/// <param name="CoverageCodes">Selected coverage codes of the segment (empty when not in force).</param>
internal sealed record PolicySnapshotFacts(
    string SnapshotRef,
    Instant ValidAt,
    Instant KnownAt,
    bool InForce,
    string? Status,
    string? NotInForceReason,
    Guid PolicyId,
    string PolicyNumber,
    string ProductCode,
    string? ProductVersion,
    Guid InsuredPartyId,
    Guid? SegmentId,
    IReadOnlyList<string> CoverageCodes);

/// <summary>Outcome of a snapshot read.</summary>
internal enum SnapshotReadOutcome
{
    /// <summary>POL answered with a usable snapshot (in force or not).</summary>
    Found,

    /// <summary>POL does not know the policy, or its answer has no snapshot reference: the policy is unverified (REQ-CLM-050).</summary>
    Unverified,

    /// <summary>POL is not reachable or not registered in this deployment.</summary>
    Unavailable,
}

/// <summary>Result of <see cref="ICoverageSource.ReadAsync"/>.</summary>
internal sealed record SnapshotRead(SnapshotReadOutcome Outcome, PolicySnapshotFacts? Facts, string? Detail)
{
    public static SnapshotRead Found(PolicySnapshotFacts facts) => new(SnapshotReadOutcome.Found, facts, null);

    public static SnapshotRead Unverified(string detail) => new(SnapshotReadOutcome.Unverified, null, detail);

    public static SnapshotRead Unavailable(string detail) => new(SnapshotReadOutcome.Unavailable, null, detail);
}

/// <summary>The POL snapshot seam of CLM (one implementation: the generated POL contract).</summary>
internal interface ICoverageSource
{
    /// <summary>The snapshot of <paramref name="policyId"/> valid at <paramref name="lossAt"/> and known at <paramref name="knownAt"/>.</summary>
    Task<SnapshotRead> ReadAsync(Guid policyId, Instant lossAt, Instant knownAt, CancellationToken cancellationToken);
}

/// <summary>Coverage indications from a snapshot (REQ-CLM-048, REQ-CLM-049).</summary>
internal static class CoverageRules
{
    /// <summary>
    /// COVERED when the policy was in force and the code is a selected coverage of the segment; NOT_COVERED when in force
    /// but the code is absent; IN_QUESTION when the policy was not in force at the loss date (REQ-CLM-049: the claim is
    /// created with coverage pending and flagged coverage in question).
    /// </summary>
    public static CoverageIndicationCode Indicate(PolicySnapshotFacts snapshot, string coverageCode) =>
        !snapshot.InForce ? CoverageIndicationCode.InQuestion
        : snapshot.CoverageCodes.Contains(coverageCode, StringComparer.Ordinal) ? CoverageIndicationCode.Covered
        : CoverageIndicationCode.NotCovered;
}
