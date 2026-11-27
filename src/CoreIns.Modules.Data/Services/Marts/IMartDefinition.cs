using CoreIns.Modules.Data.Persistence;

namespace CoreIns.Modules.Data.Services.Marts;

/// <summary>The scope of one mart computation: entity, as-of instants (valid and record time) and optional period and product filters.</summary>
internal sealed record MartScope(string LegalEntity, DateTime Valid, DateTime Known, string? Product, DateTime? From, DateTime? To);

/// <summary>One computed figure. The key is unique within a run and doubles as the comparison key between runs.</summary>
internal sealed record MartFigure(string ProductCode, string PeriodMonth, string Dimension, string Metric, decimal Value, string Unit, long SourceCount)
{
    /// <summary>Stable key: metric|product|month|dimension.</summary>
    public string Key => $"{Metric}|{ProductCode}|{PeriodMonth}|{Dimension}";
}

/// <summary>
/// A report mart (gold layer, schema <c>rpt_mart</c>): a pure function of the conformed facts at (validAt, knownAt), so the same inputs always
/// give the same figures and hash. KPI definitions are those of <see cref="AnalyticsReader"/> (v1.0): reused, not redefined.
/// </summary>
internal interface IMartDefinition
{
    /// <summary>Mart code, e.g. PORTFOLIO_PREMIUM.</summary>
    string Code { get; }

    /// <summary>
    /// Legal status of the mart. The three management marts are NotRegulatory; a regulatory layout (SII QRT, national) stays Unverified
    /// until its taxonomy is confirmed (BO-DAT-04).
    /// </summary>
    string LegalStatus { get; }

    /// <summary>Computes the figures in the scope.</summary>
    Task<IReadOnlyList<MartFigure>> ComputeAsync(MartScope scope, CancellationToken cancellationToken);
}
