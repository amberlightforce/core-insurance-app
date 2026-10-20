using System.ComponentModel.DataAnnotations;
using CoreIns.Rules;
using CoreIns.SharedKernel;

namespace CoreIns.Modules.Reinsurance.Cession;

/// <summary>Cession base: NET = premium lines only (final for P1-P2); GROSS = every charge line (capability, unit-tested only, D-SL6-10).</summary>
internal enum CessionMode
{
    Net,
    Gross,
}

/// <summary>Kind of a configured treaty. Anything not listed is unknown to RI and goes to the exception log, never guessed.</summary>
internal static class TreatyKinds
{
    /// <summary>Proportional: target cession = share x written premium.</summary>
    public const string QuotaShare = "QUOTA_SHARE";

    /// <summary>Excess of loss: no premium cession effect (the premium side is not ceded per transaction).</summary>
    public const string XolNoCession = "XOL_NO_CESSION";
}

/// <summary>One premium-cession treaty known to RI. ILLUSTRATIVE configuration (D-SL6-10); TODO(hardening): move to the treaty registry.</summary>
internal sealed class CessionTreatyOptions
{
    [Required]
    public string TreatyId { get; set; } = string.Empty;

    [Required]
    public string Kind { get; set; } = TreatyKinds.QuotaShare;

    public int ContractYear { get; set; }

    /// <summary>Ceded share in percent, 0 to 100.</summary>
    [Range(0, 100)]
    public decimal SharePct { get; set; }

    public List<string> CoverageCodes { get; set; } = [];

    /// <summary>Inclusive start of the cover period a written premium line must start in.</summary>
    public DateOnly ValidFrom { get; set; }

    /// <summary>Exclusive end of the cover period.</summary>
    public DateOnly ValidTo { get; set; }
}

/// <summary>Configuration section <c>Reinsurance:Cession</c>.</summary>
internal sealed class CessionOptions
{
    public const string Section = "Reinsurance:Cession";

    public CessionMode Mode { get; set; } = CessionMode.Net;

    public List<CessionTreatyOptions> Treaties { get; set; } = [];
}

/// <summary>The pure arithmetic of the cession (no I/O): target, difference to booked.</summary>
internal static class CessionEngine
{
    /// <summary>Target cession = share x written premium, rounded to cents in the platform money mode.</summary>
    // TODO(hardening): resolve the legal entity's rule via mkt.Rounding.apply instead of the platform default.
    public static decimal Target(decimal sharePct, decimal writtenPremium) =>
        DecimalRounding.RoundTo(sharePct / 100m * writtenPremium, 2, DecimalRounding.DefaultMoneyMode);

    /// <summary>The difference to book: target minus what is already booked for the term and treaty.</summary>
    public static decimal Difference(decimal target, decimal booked) => target - booked;

    /// <summary>Whether a charge category is part of the cession base in the given mode.</summary>
    public static bool InBase(CessionMode mode, string chargeCategory) =>
        mode == CessionMode.Gross || string.Equals(chargeCategory, "PREMIUM", StringComparison.Ordinal);
}

/// <summary><c>ri.premium_delta</c>: every premium-bearing ChargeDeltaEmitted applied once (by event id); the written premium of a term is the sum.</summary>
internal sealed class PremiumDeltaRow
{
    public Guid EventId { get; set; }

    public Guid LegalEntityId { get; set; }

    public Guid PolicyId { get; set; }

    public Guid TermId { get; set; }

    public string CoverageCode { get; set; } = string.Empty;

    public string ChargeCategory { get; set; } = string.Empty;

    public decimal Amount { get; set; }

    public BusinessDate ValidFrom { get; set; }

    public Guid? SetId { get; set; }

    public int SetSize { get; set; }

    public Guid? CorrectionOf { get; set; }

    public Guid? OosSetId { get; set; }

    public Instant RecordedAt { get; set; }
}

/// <summary><c>ri.cession</c>: sealed delta row per (term, treaty, set). Booked cession of a term = sum of rows.</summary>
internal sealed class CessionRow
{
    public Guid CessionId { get; set; }

    public Guid LegalEntityId { get; set; }

    public string TreatyId { get; set; } = string.Empty;

    public int ContractYear { get; set; }

    public Guid TermId { get; set; }

    /// <summary>The set id (or the event id of an ordinary single-event delta): one row per key.</summary>
    public Guid SetKey { get; set; }

    public decimal WrittenPremium { get; set; }

    public decimal Target { get; set; }

    public decimal BookedBefore { get; set; }

    public decimal Delta { get; set; }

    public string Mode { get; set; } = "NET";

    public Instant RecordedAt { get; set; }
}

/// <summary><c>ri.cession_exception</c>: what the cession could not apply (unknown treaty, outside every treaty period). Append-only.</summary>
internal sealed class CessionExceptionRow
{
    public Guid ExceptionId { get; set; }

    public Guid LegalEntityId { get; set; }

    public Guid TermId { get; set; }

    public Guid SourceEventId { get; set; }

    public string Code { get; set; } = string.Empty;

    public string Detail { get; set; } = string.Empty;

    public Instant RecordedAt { get; set; }
}

/// <summary><c>ri.tracker</c>: the opening aggregate of a treaty year (migration import). Idempotent on (legal entity, treaty, year).</summary>
internal sealed class TrackerRow
{
    public Guid TrackerId { get; set; }

    public Guid LegalEntityId { get; set; }

    public string TreatyId { get; set; } = string.Empty;

    public int ContractYear { get; set; }

    public decimal OpeningAggregate { get; set; }

    public Instant RecordedAt { get; set; }
}
