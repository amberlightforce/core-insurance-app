namespace CoreIns.Modules.Market.Contracts.Spi;

/// <summary>
/// SPI 5 <c>FiscalDocumentChannel</c> (contract §3.5.8; PRD-17 §9.4.5; spi.md §5): fiscal documents, series and
/// registration ids. Binding axis LEGAL_ENTITY_HOME / BRANCH_LOCATION. Mode A (worker-hosted adapter), idempotent on
/// the caller's key, 30 s per attempt, queued on outage. Core default <see cref="FiscalSubmissionStatus.NotRequired"/>.
/// Errors: VALIDATION (schema), UNAVAILABLE (queue), CONTRACT_VIOLATION. Caller: CMP (REQ-CMP-001); BIL and FIN consume.
/// Types only in F-1e; the document payload is refined by the CMP feature package.
/// </summary>
public interface IFiscalDocumentChannel
{
    /// <summary><c>build(fiscalSource) → document</c>.</summary>
    Task<FiscalDocument> BuildAsync(FiscalSource fiscalSource, CancellationToken cancellationToken = default);

    /// <summary><c>submit(document, idempotencyKey) → {status, registrationId, uid, rejections[]}</c>.</summary>
    Task<FiscalSubmissionResult> SubmitAsync(
        FiscalDocument document, string idempotencyKey, CancellationToken cancellationToken = default);

    /// <summary><c>cancel(registrationId, reason)</c>.</summary>
    Task<FiscalSubmissionResult> CancelAsync(
        string registrationId, string reason, CancellationToken cancellationToken = default);

    /// <summary><c>series(documentType) → series rules</c>. CMP alone issues fiscal series and numbers (D3).</summary>
    Task<FiscalSeriesRules> SeriesAsync(string documentType, CancellationToken cancellationToken = default);
}

/// <summary>The business source of a fiscal document (receipt, credit note, commission document).</summary>
public sealed record FiscalSource
{
    public required Guid LegalEntityId { get; init; }

    /// <summary>Source kind code (for example a BIL receipt or a FIN commission statement).</summary>
    public required string SourceType { get; init; }

    /// <summary>Business key of the source in its owning module.</summary>
    public required string SourceId { get; init; }

    /// <summary>Pack-defined fiscal document type.</summary>
    public required string DocumentType { get; init; }

    public required DateOnly IssueDate { get; init; }

    /// <summary>Counterparty reference (party id in PTY), when the document type needs one.</summary>
    public string? CounterpartyRef { get; init; }

    public required IReadOnlyList<FiscalSourceLine> Lines { get; init; }
}

/// <summary>A line of a fiscal source.</summary>
public sealed record FiscalSourceLine(string ChargeType, SpiMoney NetAmount, IReadOnlyList<TaxLine> TaxLines);

/// <summary>A built fiscal document ready to submit (pack-specific body kept opaque to the core).</summary>
/// <param name="DocumentType">Pack-defined document type.</param>
/// <param name="Series">Series id from <c>series</c>.</param>
/// <param name="Number">Number within the series.</param>
/// <param name="Body">Serialised pack-specific body.</param>
/// <param name="ContentType">Media type of <paramref name="Body"/>.</param>
public sealed record FiscalDocument(
    string DocumentType, string Series, string Number, ReadOnlyMemory<byte> Body, string ContentType);

/// <summary>Submission outcome (PRD-17 §9.4.5).</summary>
public enum FiscalSubmissionStatus
{
    Registered,
    Rejected,
    NotRequired,
    Queued,
}

/// <summary>Result of <c>submit</c> / <c>cancel</c>.</summary>
public sealed record FiscalSubmissionResult(
    FiscalSubmissionStatus Status, string? RegistrationId, string? Uid, IReadOnlyList<SpiError> Rejections);

/// <summary>Series rules of a document type.</summary>
/// <param name="DocumentType">Document type.</param>
/// <param name="SeriesId">Series id.</param>
/// <param name="Pattern">Pack-defined numbering pattern.</param>
public sealed record FiscalSeriesRules(string DocumentType, string SeriesId, string Pattern);
