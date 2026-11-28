using CoreIns.Modules.Compliance.Contracts.Api;
using CoreIns.Modules.Compliance.Domain;

namespace CoreIns.Modules.Compliance.Reconciliation;

/// <summary>
/// <c>cmp.FiscalRule.certify</c> (REQ-CMP-260): the MARK certification rule MKT runs over a country pack's fiscal values
/// before the pack may go live. Pure and fail closed: every key must pass, and values that are not legally Verified
/// (the default) fail, so an unverified pack is never certified in any environment.
/// </summary>
internal static class FiscalRuleCertifier
{
    public const string DocumentType = "cmp.fiscal.document_type";
    public const string SeriesPattern = "cmp.fiscal.series_pattern";
    public const string MarkBeforeIssue = "cmp.fiscal.mark_before_issue";
    public const string MarkBeforeDelivery = "bil.fiscal.mark_before_delivery";
    public const string LegalStatusVerified = "cmp.fiscal.legal_status_verified";

    public static IReadOnlyList<string> Keys { get; } = [DocumentType, SeriesPattern, MarkBeforeIssue, MarkBeforeDelivery, LegalStatusVerified];

    public static FiscalRuleCertifyResponse Certify(FiscalRuleCertifyRequest.PackCandidateDetail candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var failed = new List<string>();
        if (string.IsNullOrWhiteSpace(candidate.DocumentType) || candidate.DocumentType == FiscalDocuments.PlaceholderDocumentType)
        {
            failed.Add(DocumentType);
        }

        if (string.IsNullOrWhiteSpace(candidate.SeriesPattern))
        {
            failed.Add(SeriesPattern);
        }

        if (candidate.MarkBeforeIssue != true)
        {
            failed.Add(MarkBeforeIssue);
        }

        if (candidate.MarkBeforeDelivery != true)
        {
            failed.Add(MarkBeforeDelivery);
        }

        if (candidate.LegalStatus != FiscalRuleCertifyRequest.PackCandidateDetail.LegalStatusValue.Verified)
        {
            failed.Add(LegalStatusVerified);
        }

        return new FiscalRuleCertifyResponse { Pass = failed.Count == 0, FailedKeys = failed, CheckedKeys = Keys };
    }
}
