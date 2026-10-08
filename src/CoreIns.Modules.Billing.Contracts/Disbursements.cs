using System.Globalization;
using System.Text.Json.Nodes;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;

namespace CoreIns.Modules.Billing.Contracts;

/// <summary>
/// Codes of the disbursement source register (REQ-BIL-354) and methods (REQ-BIL-209) that SL2-BIL-DISB serves. Other
/// sources are refused with <c>BIL-ERR-SOURCE</c>, other methods with <c>BIL-ERR-METHOD-NOT-ALLOWED</c>.
/// </summary>
public static class DisbursementCodes
{
    /// <summary>Source type of a CLM claim payment (calling module CLM; evidence: the approved transaction set).</summary>
    public const string ClaimPayment = "CLM_CLAIM_PAYMENT";

    /// <summary>SEPA credit transfer (REQ-BIL-209), the default method of CLM_CLAIM_PAYMENT.</summary>
    public const string SepaCreditTransfer = "SEPA_CT";

    /// <summary>Payee account purpose used for claim payments (REQ-BIL-343).</summary>
    public const string ClaimPaymentPurpose = "CLAIM_PAYMENT";
}

/// <summary>
/// The content a source approves before it asks BIL to pay (REQ-BIL-198): BIL recomputes this hash from the request and
/// refuses the disbursement with <c>BIL-ERR-APPROVAL-MISMATCH</c> when it differs from <c>approvalContentHash</c>, so
/// an amount or payee changed after approval is never paid. The hash is SHA-256 over the RFC 8785 canonical JSON of
/// <c>{"amount":{"amount":"&lt;fixed minor units&gt;","currency":"EUR"},"payeeAccountId":"…","payeePartyId":"…","sourceId":"…","sourceType":"…"}</c>
/// (ids in lower-case "D" format, the amount as a string with exactly the currency's minor units).
/// </summary>
public static class DisbursementContent
{
    /// <summary>The approval content hash of a disbursement.</summary>
    public static Sha256Hash Hash(string sourceType, string sourceId, PartyId payeePartyId, Guid payeeAccountId, Money amount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceType);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        var node = new JsonObject
        {
            ["sourceType"] = sourceType,
            ["sourceId"] = sourceId,
            ["payeePartyId"] = payeePartyId.Value.ToString("D"),
            ["payeeAccountId"] = payeeAccountId.ToString("D"),
            ["amount"] = new JsonObject
            {
                ["amount"] = amount.Amount.ToString("F" + amount.Currency.MinorUnits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
                ["currency"] = amount.Currency.Code,
            },
        };
        return CanonicalJson.Hash(node);
    }
}
