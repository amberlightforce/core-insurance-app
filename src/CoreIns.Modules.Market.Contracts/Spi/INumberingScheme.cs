namespace CoreIns.Modules.Market.Contracts.Spi;

/// <summary>
/// SPI 9 <c>NumberingScheme</c> (contract §3.5.8; PRD-17 §9.4.9; spi.md §9): identifier formats and series. Binding
/// axis LEGAL_ENTITY_HOME. Mode S, pure (formatting, REQ-MKT-117), 20 ms, fail closed. Core default: prefix + padded
/// sequence. Caller: PLT numbering service (REQ-PLT-014). Fiscal series are issued only by CMP through
/// <c>FiscalDocumentChannel.series</c> (D3). Types only in F-1e.
/// </summary>
public interface INumberingScheme
{
    /// <summary><c>format(identifierType, sequence, context) → string</c>.</summary>
    ValueTask<string> FormatAsync(
        string identifierType, long sequence, NumberingContext context, CancellationToken cancellationToken = default);

    /// <summary><c>validate(identifierType, value)</c>.</summary>
    ValueTask<NumberValidationResult> ValidateAsync(
        string identifierType, string value, CancellationToken cancellationToken = default);

    /// <summary><c>series(identifierType, context) → series id</c>.</summary>
    ValueTask<string> SeriesAsync(
        string identifierType, NumberingContext context, CancellationToken cancellationToken = default);
}

/// <summary>Context of a numbering call.</summary>
/// <param name="LegalEntityId">Legal entity.</param>
/// <param name="ValidAt">Valid-time business date (series may roll by year; D-API-08 name).</param>
/// <param name="Qualifiers">Additional pack-defined qualifiers (for example branch, product line).</param>
public sealed record NumberingContext(Guid LegalEntityId, DateOnly ValidAt, IReadOnlyDictionary<string, string>? Qualifiers = null);

/// <summary>Result of <c>validate</c>.</summary>
public sealed record NumberValidationResult(bool Valid, IReadOnlyList<SpiError> Errors);
