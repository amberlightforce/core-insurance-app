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

    /// <summary>Source type of a policy refund (calling module BIL itself; evidence: the refund's own approval, D-SL3-14).</summary>
    public const string RefundPayment = "BIL_REFUND";

    /// <summary>Payee account purpose used for policy refunds (REQ-BIL-186, REQ-BIL-343).</summary>
    public const string RefundPurpose = "REFUND";
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

/// <summary>
/// The PLT approval BIL verifies for a referred claim payment (D-SL2-10 d, REQ-PLT-117): when a CLM_CLAIM_PAYMENT request
/// names a PLT approval request as evidence (<c>PLT/ApprovalRequest/{id}</c>), BIL calls
/// <c>plt.Approval.verifyForExecution</c> with type <see cref="ClaimPaymentType"/>, subject <c>CLM/ClaimPayment/{sourceId}</c> and
/// the hash it recomputes with <see cref="DisbursementContent.Hash"/>; anything but an Approved request covering exactly that
/// content is refused (BIL-ERR-APPROVAL-MISMATCH). A payment approved within the maker's authority names its approved set
/// (<c>CLM/TransactionSet/{id}</c>) and carries no PLT request.
/// </summary>
public static class DisbursementApproval
{
    /// <summary>The approval type CLM requests for a set that carries a referred claim payment.</summary>
    public const string ClaimPaymentType = "CLM.CLAIM_PAYMENT";

    /// <summary>The subject type of that approval.</summary>
    public const string ClaimPaymentSubjectType = "ClaimPayment";

    /// <summary>The approval type BIL requests for a refund above the auto-approval limit or to a changed payee (D-SL3-14).</summary>
    public const string RefundType = "BIL.REFUND";

    /// <summary>The subject type of that approval.</summary>
    public const string RefundSubjectType = "Refund";

    /// <summary>Evidence of a refund approved by rule (within the auto-approval limit, unchanged payee): <c>BIL/RefundAuto/{refundId}</c>.</summary>
    public const string RefundAutoEvidencePrefix = "BIL/RefundAuto/";

    private const string EvidencePrefix = "PLT/ApprovalRequest/";

    /// <summary>The approval subject of a refund (its source id is the refund id in "D" format).</summary>
    public static ObjectRef RefundSubject(string sourceId) => new(ModuleCode.BIL, RefundSubjectType, sourceId);

    /// <summary>The approval subject of a claim payment (its source id is the CLM claim payment id).</summary>
    public static ObjectRef ClaimPaymentSubject(string sourceId) => new(ModuleCode.CLM, ClaimPaymentSubjectType, sourceId);

    /// <summary>The PLT approval request named by an evidence reference, if it names one.</summary>
    public static bool TryParseApprovalRequest(string? evidenceRef, out Guid requestId)
    {
        requestId = Guid.Empty;
        return evidenceRef is not null
               && evidenceRef.StartsWith(EvidencePrefix, StringComparison.Ordinal)
               && Guid.TryParseExact(evidenceRef[EvidencePrefix.Length..], "D", out requestId)
               && requestId != Guid.Empty;
    }
}
