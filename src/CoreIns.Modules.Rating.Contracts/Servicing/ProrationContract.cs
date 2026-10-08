using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.Modules.Rating.Contracts.Servicing;

// SL3-RAT-PRORATE. Hand-written in-process contract of rat.Proration.prorate (REQ-RAT-004, -155..-166) and of the servicing
// tax lines (REQ-RAT-009 subset, REQ-POL-124). The generated ProrationProrateRequest/Response are still untyped JSON
// (SL3-CONTRACTS types them in parallel). Whichever of the two merges second reconciles the names and shapes
// (PITFALLS 21, 22); until then POL's IProration adapter calls this interface.

/// <summary>Day-count conventions RAT can apply (REQ-RAT-155, -156). ACT/ACT and 30E/360 are known to the PRD but not built: they are refused like an undeclared convention.</summary>
public static class DayCountConventions
{
    /// <summary>Annual rate x segment days / term days (365 or 366).</summary>
    public const string TermRatio = "TERM_RATIO";

    /// <summary>Annual rate x segment days / 365, whatever the year length.</summary>
    public const string Act365Fixed = "ACT/365F";

    /// <summary>Canonical spelling of a convention (accepts <c>ACT_365F</c> and any case), or null when empty.</summary>
    public static string? Normalise(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var value = text.Trim().ToUpperInvariant().Replace('_', '/');
        return value == "TERM/RATIO" ? TermRatio : value;
    }
}

/// <summary>How a charge behaves under proration (REQ-RAT-160): flat and fully-earned charges are never prorated.</summary>
public enum ProrationHandling
{
    Proratable,
    Flat,
    FullyEarned,
}

/// <summary>One annual-rate line to prorate: an element x charge type inside a segment.</summary>
/// <param name="LineId">Caller's reference, echoed back unchanged.</param>
/// <param name="ElementId">Element (vehicle) the line belongs to.</param>
/// <param name="ChargeType">Charge type (for example PREM-MTPL).</param>
/// <param name="AnnualRate">Annual rate in the request currency. A negative rate gives the exact negative of the same positive rate.</param>
/// <param name="Handling">Proratable by default.</param>
public sealed record ProrationLineInput(string LineId, string ElementId, string ChargeType, decimal AnnualRate, ProrationHandling Handling = ProrationHandling.Proratable);

/// <summary>A segment: a half-open period <c>[From, To)</c> of whole Europe/Athens dates inside the term (D-SL3-04). <c>From == To</c> is a zero-day segment (amount 0.00).</summary>
public sealed record ProrationSegmentInput(string SegmentId, BusinessDate From, BusinessDate To, IReadOnlyList<ProrationLineInput> Lines);

/// <summary>Request of <c>rat.Proration.prorate</c> (in-process).</summary>
/// <param name="Currency">Currency of every rate and amount.</param>
/// <param name="TermFrom">Term start (inclusive).</param>
/// <param name="TermTo">Term end (exclusive); must be after <paramref name="TermFrom"/>.</param>
/// <param name="Convention">The convention to apply.</param>
/// <param name="DeclaredConventions">The conventions the product artefact declares (its <c>dayCount</c>). The convention must be one of them (REQ-RAT-165).</param>
/// <param name="Segments">Segments with their lines.</param>
/// <param name="ConfigurationHash">Configuration the caller rated under; echoed in the result so the amounts can be audited.</param>
/// <param name="RoundingDate">Date the MKT rounding rule is resolved at; the term start when null.</param>
public sealed record ProrationRequest(
    Currency Currency,
    BusinessDate TermFrom,
    BusinessDate TermTo,
    string Convention,
    IReadOnlyList<string> DeclaredConventions,
    IReadOnlyList<ProrationSegmentInput> Segments,
    ConfigurationHash? ConfigurationHash = null,
    BusinessDate? RoundingDate = null);

/// <summary>One prorated line.</summary>
/// <param name="Days">Whole days of the segment (0 for a zero-day segment).</param>
/// <param name="DaysDenominator">Term days (TERM_RATIO) or 365 (ACT/365F); 1 for flat and fully-earned lines.</param>
/// <param name="Fraction">Days / denominator as a decimal at 16 places (informational; the amount is computed from the exact ratio, not from this value).</param>
/// <param name="Unrounded">Annual rate x days / denominator, truncated toward zero at 16 places, before rounding.</param>
/// <param name="Amount">Rounded amount (MKT PREMIUM rule), signed like the rate.</param>
/// <param name="Residual">Unrounded - Amount, signed like the rate; POL keeps it so cumulative amounts stay exact.</param>
public sealed record ProratedLine(
    string SegmentId,
    string LineId,
    string ElementId,
    string ChargeType,
    ProrationHandling Handling,
    int Days,
    int DaysDenominator,
    decimal Fraction,
    decimal Unrounded,
    Money Amount,
    Money Residual,
    string? RoundingRuleKey,
    Guid? RoundingRuleId)
{
    /// <summary>The exact reversal (REQ-RAT-163): amount, residual and unrounded value negated; nothing re-rounded.</summary>
    public ProratedLine Reverse() => this with { Unrounded = -Unrounded, Amount = Amount.Negate(), Residual = Residual.Negate() };
}

/// <summary>Result of <c>rat.Proration.prorate</c>.</summary>
/// <param name="Convention">Canonical convention applied.</param>
/// <param name="TermDays">Whole days of the term.</param>
/// <param name="ConfigurationHash">Echo of the request's configuration hash.</param>
/// <param name="Lines">One per segment line, in request order.</param>
/// <param name="RoundingLegalStatus">Weakest legal status of the rounding rules used; null when none was reported.</param>
/// <param name="Provisional">True when any rounding rule used is not Settled.</param>
public sealed record ProrationResult(
    string Convention,
    int TermDays,
    ConfigurationHash? ConfigurationHash,
    IReadOnlyList<ProratedLine> Lines,
    string? RoundingLegalStatus,
    bool Provisional)
{
    /// <summary>The exact reversal of the whole result: every line negated.</summary>
    public ProrationResult Reverse() => this with { Lines = [.. Lines.Select(l => l.Reverse())] };
}

/// <summary><c>rat.Proration.prorate</c> as an in-process service (REQ-RAT-004). Pure apart from the MKT rounding call. Errors: <c>RAT-ERR-CONVENTION</c>, <c>RAT-ERR-PERIOD</c>, <c>RAT-ERR-INPUT</c>.</summary>
public interface IRatingProrationEngine
{
    /// <summary>Turns annual rates into amounts for the given segments (half-open, whole Athens dates).</summary>
    Task<ProrationResult> ProrateAsync(ProrationRequest request, CancellationToken cancellationToken = default);
}
