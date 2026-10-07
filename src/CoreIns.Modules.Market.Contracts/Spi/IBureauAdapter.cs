namespace CoreIns.Modules.Market.Contracts.Spi;

/// <summary>
/// SPI 7 <c>BureauAdapter</c> (contract §3.5.8; PRD-17 §9.4.7; spi.md §7): motor bureau / insured-vehicle register
/// reporting. Binding axis RISK_LOCATION. Mode A, idempotent on the caller's key (one submission per policy event,
/// REQ-MKT-093), 30 s, queued on outage. Core default <see cref="BureauReportStatus.NotRequired"/>. Caller: CMP
/// (REQ-CMP-002). Types only in F-1e.
/// </summary>
public interface IBureauAdapter
{
    /// <summary><c>report(policyEvent, idempotencyKey) → {status, bureauRef}</c>.</summary>
    Task<BureauReportResult> ReportAsync(
        BureauPolicyEvent policyEvent, string idempotencyKey, CancellationToken cancellationToken = default);

    /// <summary><c>reconcile(period) → differences[]</c>.</summary>
    Task<IReadOnlyList<BureauDifference>> ReconcileAsync(
        DateOnly periodStart, DateOnly periodEnd, CancellationToken cancellationToken = default);
}

/// <summary>A policy event to report (business keys only, no P2/P3 values beyond what the bureau requires).</summary>
public sealed record BureauPolicyEvent
{
    public required Guid LegalEntityId { get; init; }

    /// <summary>Id of the originating event (the idempotency anchor).</summary>
    public required Guid EventId { get; init; }

    public required string EventType { get; init; }

    public required string PolicyNumber { get; init; }

    /// <summary>Normalised plate (<c>IdValidator</c> scheme <c>VEHICLE_PLATE</c>).</summary>
    public required string VehiclePlate { get; init; }

    public required DateOnly CoverStart { get; init; }

    public DateOnly? CoverEnd { get; init; }
}

/// <summary>Report outcome.</summary>
public enum BureauReportStatus
{
    Reported,
    Rejected,
    Queued,
    NotRequired,
}

/// <summary>Result of <c>report</c>.</summary>
public sealed record BureauReportResult(BureauReportStatus Status, string? BureauRef, IReadOnlyList<SpiError> Rejections);

/// <summary>A difference between the insurer's records and the bureau's.</summary>
/// <param name="PolicyNumber">Policy business key.</param>
/// <param name="VehiclePlate">Normalised plate.</param>
/// <param name="Kind">Difference code (for example missing at bureau, missing locally, dates differ).</param>
public sealed record BureauDifference(string PolicyNumber, string VehiclePlate, string Kind);
