namespace CoreIns.Modules.Market.Contracts.Spi;

/// <summary>
/// SPI 8 <c>StatutoryClockSet</c> (contract §3.5.8; PRD-17 §9.4.8; spi.md §8): statutory clock values. Binding axis
/// CONTRACT_LAW_JURISDICTION or per clock. Mode S, pure, 20 ms; EU default or fail closed. Core default: EU-layer
/// defaults only. Caller: CMP (REQ-CMP-003). Types only in F-1e: clock values are pack data with a legal status
/// (D-REG-01); unstated values are empty keys that fail closed.
/// </summary>
public interface IStatutoryClockSet
{
    /// <summary><c>get(clockCode, jurisdiction, date)</c>. RULE_MISSING when no value is valid on the date.</summary>
    ValueTask<StatutoryClockValue> GetAsync(
        string clockCode, string jurisdiction, DateOnly validAt, CancellationToken cancellationToken = default);

    /// <summary><c>list(jurisdiction, date)</c>.</summary>
    ValueTask<IReadOnlyList<StatutoryClockValue>> ListAsync(
        string jurisdiction, DateOnly validAt, CancellationToken cancellationToken = default);
}

/// <summary>Clock duration unit (PRD-17 §7.1 StatutoryClockValue <c>unit</c>).</summary>
public enum ClockUnit
{
    CalendarDays,
    BusinessDays,
    Weeks,
    Months,
    Years,
    Hours,
}

/// <summary>A statutory clock value (PRD-17 §7.1 StatutoryClockValue; all attributes P0).</summary>
public sealed record StatutoryClockValue
{
    public required Guid ClockValueId { get; init; }

    /// <summary>Code in the CMP clock register (REQ-CMP-003).</summary>
    public required string ClockCode { get; init; }

    /// <summary>ISO 3166-1 jurisdiction.</summary>
    public required string Jurisdiction { get; init; }

    /// <summary>ISO 8601 duration (for example <c>P1M</c>); constrained by the EU minimum.</summary>
    public required string Duration { get; init; }

    public required ClockUnit Unit { get; init; }

    /// <summary>Calendar id in the PLT calendar store.</summary>
    public required string CalendarId { get; init; }

    /// <summary>Start, stop, pause, warning and extension rules (JSON documents, PRD-17 §7.1).</summary>
    public string? StartRule { get; init; }

    public string? StopRule { get; init; }

    public string? PauseRules { get; init; }

    public string? WarningThresholds { get; init; }

    public string? ExtensionRules { get; init; }

    public required bool AppliesToRunning { get; init; }

    public required string LegalSource { get; init; }

    /// <summary>Date verified; null = UNVERIFIED.</summary>
    public DateOnly? VerifiedOn { get; init; }

    public required LegalStatus LegalStatus { get; init; }

    public required DateOnly ValidFrom { get; init; }

    public DateOnly? ValidTo { get; init; }
}
