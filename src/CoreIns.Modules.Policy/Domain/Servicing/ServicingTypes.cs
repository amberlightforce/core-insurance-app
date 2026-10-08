using CoreIns.SharedKernel;

namespace CoreIns.Modules.Policy.Domain.Servicing;

/// <summary>Day-count convention of a term (D-SL3-04). Anything else is refused, never guessed (PITFALLS 10).</summary>
internal enum DayCountConvention
{
    /// <summary>Annual rate × segment days ÷ term days (365 or 366). MOTOR-GR 1.1, provisional.</summary>
    TermRatio,

    /// <summary>Annual rate × segment days ÷ 365; the full-term amount is capped at the annual rate. MOTOR-GR 1.0.</summary>
    Act365F,
}

/// <summary>Parses the convention codes carried by artefacts and contracts; unknown codes fail closed.</summary>
internal static class DayCountConventions
{
    public static bool TryParse(string? code, out DayCountConvention convention)
    {
        switch (code)
        {
            case "TERM_RATIO":
                convention = DayCountConvention.TermRatio;
                return true;
            case "ACT_365F":
                convention = DayCountConvention.Act365F;
                return true;
            default:
                convention = default;
                return false;
        }
    }
}

/// <summary>The MKT transaction kind carried on charge deltas (local copy until SL3-CONTRACTS types it; reconcile).</summary>
internal enum TransactionKind
{
    NewBusiness,
    EndorsementDebit,
    EndorsementCredit,
    Cancellation,
}

/// <summary>How a cancellation refunds: pro rata from the effective date, or the full written amount (flat cancel at term start).</summary>
internal enum RefundMethod
{
    ProRata,
    FullRefund,
}

/// <summary>Typed refusals of the engine (fail closed).</summary>
internal enum ServicingRefusal
{
    /// <summary>The effective time is earlier than the latest boundary created by a bound transaction (D-SL3-02).</summary>
    OutOfSequence,

    /// <summary>The effective time is not inside the term.</summary>
    EffectiveOutsideTerm,

    /// <summary>The cover already ended (cancelled); only a new term is possible.</summary>
    AfterCancellation,

    /// <summary>FullRefund is only possible for a flat cancel: effective on the term's first Athens date.</summary>
    FullRefundNotFlat,

    /// <summary>Unknown refund method.</summary>
    UnknownRefundMethod,

    /// <summary>The input state or rates are inconsistent (gaps, currency, duplicate keys, negative rates).</summary>
    InvalidInput,
}

/// <summary>The term the segments live in. Valid period half-open [From, To); days are whole Athens dates.</summary>
internal sealed record ServicingTerm(Instant From, Instant To, Currency Currency, DayCountConvention Convention, TimeZoneInfo Zone)
{
    /// <summary>Number of Athens calendar days in the term (365 or 366 for an annual term).</summary>
    public int Days => DayCount.Days(From, To, Zone);
}

/// <summary>Identity of a charge line stream: delta granularity is element × coverage × charge type (never netted across).</summary>
internal readonly record struct ChargeKey(string ElementLocator, string CoverageCode, string ChargeType) : IComparable<ChargeKey>
{
    public int CompareTo(ChargeKey other)
    {
        var c = string.CompareOrdinal(ElementLocator, other.ElementLocator);
        if (c != 0)
        {
            return c;
        }

        c = string.CompareOrdinal(CoverageCode, other.CoverageCode);
        return c != 0 ? c : string.CompareOrdinal(ChargeType, other.ChargeType);
    }
}

/// <summary>An annual rate for one element × coverage × charge type.</summary>
/// <param name="Flat">A flat charge: never prorated; the annual rate is the amount.</param>
/// <param name="RefundableOnCancel">A flat charge that the charge type says is credited on cancellation.</param>
internal sealed record ChargeRate(
    string ElementLocator,
    string CoverageCode,
    string ChargeType,
    string ChargeCategory,
    decimal AnnualRate,
    bool Flat = false,
    bool RefundableOnCancel = false)
{
    public ChargeKey Key => new(ElementLocator, CoverageCode, ChargeType);
}

/// <summary>A charge segment: the annual rate valid over [From, To) and the premium written for it (4 dp).</summary>
internal sealed record ServicingSegment(ChargeRate Rate, Instant From, Instant To, decimal Amount)
{
    public ChargeKey Key => Rate.Key;
}

/// <summary>The head of a term as the engine sees it.</summary>
/// <param name="Term">The term.</param>
/// <param name="Segments">All current segments of all keys.</param>
/// <param name="CoverEndedAt">Set once the cover was ended (cancellation); segments then stop there.</param>
/// <param name="LatestBoundEffective">Latest segment boundary created by a bound transaction (in-sequence guard).</param>
internal sealed record ServicingState(
    ServicingTerm Term,
    IReadOnlyList<ServicingSegment> Segments,
    Instant? CoverEndedAt,
    Instant? LatestBoundEffective);

internal abstract record ServicingIntent;

/// <summary>New annual rates for the remainder of the term from <paramref name="EffectiveAt"/>. Keys missing from <paramref name="NewRates"/> are rated 0.</summary>
internal sealed record ChangeIntent(Instant EffectiveAt, IReadOnlyList<ChargeRate> NewRates) : ServicingIntent;

/// <summary>Ends the cover at <paramref name="EffectiveAt"/>; flat cancel = FullRefund on the term's first Athens date.</summary>
internal sealed record EndCoverIntent(Instant EffectiveAt, RefundMethod RefundMethod) : ServicingIntent;

/// <summary>Opens a (renewal) term with full-term segments.</summary>
internal sealed record NewTermIntent(ServicingTerm Term, IReadOnlyList<ChargeRate> Rates) : ServicingIntent;

/// <summary>Correlation and set fields stamped on every delta.</summary>
internal sealed record DeltaCorrelation(string CorrelationId, string SetId);

/// <summary>
/// One NET charge delta: element × coverage × charge type × valid period (REQ-POL-119). Amount has 4 decimal places;
/// the fraction is the proration applied (days ÷ basis).
/// </summary>
internal sealed record ServicingDelta(
    ChargeKey Key,
    string ChargeCategory,
    Instant ValidFrom,
    Instant ValidTo,
    DateOnly DateFrom,
    DateOnly DateTo,
    decimal Amount,
    int Days,
    int FractionNumerator,
    int FractionDenominator,
    TransactionKind TransactionKind,
    string CorrelationId,
    string SetId,
    int SetSequence);

/// <summary>Result of an engine call: either the new state and deltas, or a typed refusal.</summary>
internal sealed record ServicingResult
{
    public ServicingState? State { get; init; }

    public IReadOnlyList<ServicingDelta> Deltas { get; init; } = [];

    public ServicingRefusal? Refusal { get; init; }

    public string? Message { get; init; }

    public bool IsAccepted => Refusal is null;

    public static ServicingResult Accepted(ServicingState state, IReadOnlyList<ServicingDelta> deltas) => new() { State = state, Deltas = deltas };

    public static ServicingResult Refused(ServicingRefusal refusal, string message) => new() { Refusal = refusal, Message = message };
}

/// <summary>Rounding of a premium amount per MKT's rule for premium at element × charge-type level; supplied by the caller.</summary>
internal delegate decimal PremiumRounding(decimal amount);

/// <summary>Port: tax lines on premium deltas are computed by RAT (via POL-CHANGE / POL-CANCEL), never here.</summary>
internal interface ITaxLinesForDeltas
{
    /// <summary>Tax and levy deltas for the premium deltas of one transaction, keyed to the premium delta they derive from.</summary>
    IReadOnlyList<TaxLineDelta> TaxLines(IReadOnlyList<ServicingDelta> premiumDeltas, TransactionKind kind);
}

/// <summary>A tax or levy delta derived from a premium delta.</summary>
internal sealed record TaxLineDelta(ChargeKey SourcePremiumKey, string ChargeType, decimal Amount, string? TreatmentRuleId, string? TreatmentRuleVersion, string? LegalStatus, bool Provisional);
