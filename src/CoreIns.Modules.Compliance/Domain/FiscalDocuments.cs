namespace CoreIns.Modules.Compliance.Domain;

/// <summary>Fiscal document status (REQ-CMP-030: Pending on creation, then Registered or Rejected by the channel).</summary>
internal enum FiscalDocumentStatus
{
    /// <summary>Created; not yet registered (queued on a channel outage, REQ-CMP-056).</summary>
    Pending,

    /// <summary>Registered by the channel (MARK received, <c>FiscalDocRegistered</c>).</summary>
    Registered,

    /// <summary>Rejected by the channel (<c>FiscalDocRejected</c>).</summary>
    Rejected,
}

/// <summary>Fiscal document roles (REQ-CMP-031).</summary>
internal enum FiscalDocumentRole
{
    Issue,
    Credit,
    Cancellation,
}

/// <summary>Constants of the fiscal-document stub path built by SL-BIL (W5-CMP-01 subset).</summary>
internal static class FiscalDocuments
{
    /// <summary>
    /// PLACEHOLDER document type used for every fiscal document until the myDATA document types for insurance are settled
    /// (DECISIONS §G OQ-012, PRD-06 OI-BIL-06). It is deliberately not an AADE code and must never be transmitted to
    /// the real myDATA service.
    /// </summary>
    public const string PlaceholderDocumentType = "UNMAPPED-OQ-012";

    /// <summary>
    /// Suffix of CMP's own credit-note series. The myDATA credit-note document type is open (OQ-012): the candidate codes
    /// (PRD-11 BR-CMP-001, OI-CMP-03) stay out of the code and credits carry <see cref="PlaceholderDocumentType"/>.
    /// </summary>
    public const string CreditSeriesSuffix = "-CR";

    /// <summary>Fiscal source types REQ-CMP-030 lists (open code until CMP settles one list).</summary>
    public static IReadOnlySet<string> SourceTypes { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "TRANSACTION", "INVOICE", "CREDIT", "FEE", "COMMISSION", "CLAIM_PAYMENT" };

    /// <summary>
    /// Media-type marker of a synthetic document built by a stub channel (the slice's myDATA stub): its identifiers are
    /// never fiscal evidence and the UI shows them as such.
    /// </summary>
    public const string StubContentTypeMarker = "fiscal-stub";

    /// <summary>True when the channel that built the document is a stub (no external call, synthetic MARK/UID).</summary>
    public static bool IsStub(CoreIns.Modules.Market.Contracts.Spi.FiscalDocument document) =>
        document.ContentType.Contains(StubContentTypeMarker, StringComparison.Ordinal);

    /// <summary>Upper-snake code of an enum value (stored and published).</summary>
    public static string Code<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        string.Concat(value.ToString().Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + c : c.ToString())).ToUpperInvariant();

    /// <summary>Check-constraint SQL listing every code of <typeparamref name="TEnum"/>.</summary>
    public static string CheckSql<TEnum>(string column)
        where TEnum : struct, Enum =>
        $"{column} IN ({string.Join(", ", Enum.GetValues<TEnum>().Select(v => $"'{Code(v)}'"))})";
}
