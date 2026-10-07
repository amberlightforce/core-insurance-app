using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CoreIns.Modules.Market.Contracts.Spi;

namespace CoreIns.CountryPacks.GR.Fiscal;

/// <summary>
/// STUB of the Greek <c>FiscalDocumentChannel</c> (myDATA, PRD-11 REQ-CMP-001, spi.md §5) for the thin slice (SL-BIL).
/// It makes <b>no external call</b>: it answers the way myDATA does (a registration id / MARK and a UID) with
/// <b>synthetic</b> identifiers that are visibly marked: the MARK and the UID start with <see cref="StubPrefix"/>, the
/// series is <see cref="StubSeries"/>, and the built document's media type carries the <c>fiscal-stub</c> marker so
/// CMP records it as a stub. Nothing it returns is fiscal evidence.
/// <list type="bullet">
/// <item>The document body is a minimal JSON echo of the source; it is not the myDATA XSD (version open, OQ-012).</item>
/// <item>The document type passes through unchanged: the caller supplies a placeholder because the insurance document
/// types are open (OQ-012, OI-BIL-06). No AADE code (document type, E3 classification, VAT category) is produced.</item>
/// <item>Identifiers are deterministic in the caller's idempotency key, so a resubmission returns the same MARK
/// (REQ-CMP-031).</item>
/// </list>
/// The Host binds it only outside Production; the real adapter (W5-CMP-01) replaces it.
/// </summary>
public sealed class MyDataStubFiscalChannel : IFiscalDocumentChannel
{
    /// <summary>Prefix of every synthetic identifier.</summary>
    public const string StubPrefix = "STUB-";

    /// <summary>Series the stub reports for every document type.</summary>
    public const string StubSeries = "STUB-A";

    /// <summary>Media type of a stub-built document (contains the <c>fiscal-stub</c> marker).</summary>
    public const string StubContentType = "application/vnd.coreins.fiscal-stub+json";

    /// <inheritdoc />
    public Task<FiscalDocument> BuildAsync(FiscalSource fiscalSource, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fiscalSource);
        if (fiscalSource.Lines.Count == 0)
        {
            throw new SpiException(new SpiError(SpiErrorCategory.Validation, "NO_LINES"), "A fiscal document needs at least one line.");
        }

        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            stub = true,
            sourceType = fiscalSource.SourceType,
            sourceId = fiscalSource.SourceId,
            documentType = fiscalSource.DocumentType,
            issueDate = fiscalSource.IssueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            lines = fiscalSource.Lines.Select(l => new { key = l.ChargeType, amount = l.NetAmount.Amount.ToString(CultureInfo.InvariantCulture), currency = l.NetAmount.Currency }),
        });
        return Task.FromResult(new FiscalDocument(fiscalSource.DocumentType, StubSeries, string.Empty, body, StubContentType));
    }

    /// <inheritdoc />
    public Task<FiscalSubmissionResult> SubmitAsync(FiscalDocument document, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey));

        // myDATA returns a numeric MARK and a 40-hex UID; the stub mimics the shapes behind the STUB- prefix.
        var mark = StubPrefix + (BitConverter.ToUInt64(hash, 0) % 1_000_000_000_000_000UL).ToString("D15", CultureInfo.InvariantCulture);
        var uid = StubPrefix + Convert.ToHexString(hash, 8, 20);
        return Task.FromResult(new FiscalSubmissionResult(FiscalSubmissionStatus.Registered, mark, uid, []));
    }

    /// <inheritdoc />
    public Task<FiscalSubmissionResult> CancelAsync(string registrationId, string reason, CancellationToken cancellationToken = default) =>
        Task.FromResult(new FiscalSubmissionResult(FiscalSubmissionStatus.Registered, registrationId, null, []));

    /// <inheritdoc />
    public Task<FiscalSeriesRules> SeriesAsync(string documentType, CancellationToken cancellationToken = default) =>
        Task.FromResult(new FiscalSeriesRules(documentType, StubSeries, "{number}"));
}
