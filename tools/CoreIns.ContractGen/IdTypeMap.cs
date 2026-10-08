namespace CoreIns.ContractGen;

/// <summary>
/// Maps contract ids to SharedKernel strongly typed ids. The schemas type every internal id as the common <c>Uuid</c>
/// (one pattern for all entities), so the mapping is by property name, optionally scoped to the owning module of the
/// document (<c>*</c> = any module, <c>common</c> = contracts/events/common.schema.json). An id with no entry stays
/// <see cref="System.Guid"/> and its doc says so. Business numbers and hashes map by name the same way.
/// </summary>
internal static class IdTypeMap
{
    private const string Ids = "global::CoreIns.SharedKernel.Identifiers.";

    /// <summary>(scope, property name) → SharedKernel id type (simple name). Scope-specific entries win over <c>*</c>.</summary>
    public static IReadOnlyDictionary<(string Scope, string Name), string> Uuid { get; } = Build(
        ("*", "quoteId", "QuoteId"), ("*", "jobRef", "JobId"), ("*", "policyRef", "PolicyId"), ("*", "policyTermRef", "PolicyTermId"),
        ("*", "jobId", "JobId"), ("*", "polJobId", "JobId"), ("*", "consequenceJobId", "JobId"),
        ("*", "policyId", "PolicyId"), ("*", "matchedPolicyId", "PolicyId"), ("*", "newPolicyId", "PolicyId"),
        ("*", "rewritePolicyId", "PolicyId"), ("*", "sourcePolicyId", "PolicyId"),
        ("*", "termId", "PolicyTermId"), ("*", "policyTermId", "PolicyTermId"), ("*", "newTermId", "PolicyTermId"),
        ("*", "oldTermId", "PolicyTermId"), ("*", "expiringTermId", "PolicyTermId"),
        ("*", "transactionId", "PolicyTransactionId"), ("*", "newTransactionId", "PolicyTransactionId"),
        ("*", "originalTransactionId", "PolicyTransactionId"), ("*", "preemptingTransactionId", "PolicyTransactionId"),
        ("*", "reversalTransactionId", "PolicyTransactionId"),
        ("*", "chargeId", "ChargeId"), ("*", "sourceChargeId", "ChargeId"),
        ("*", "segmentId", "SegmentId"), ("*", "affectedSegmentId", "SegmentId"),
        ("*", "partyId", "PartyId"), ("*", "policyholderPartyId", "PartyId"), ("*", "claimantPartyId", "PartyId"), ("*", "payerPartyId", "PartyId"),
        ("*", "payeePartyId", "PartyId"), ("*", "reinsurerPartyId", "PartyId"), ("*", "holderPartyId", "PartyId"),
        ("*", "counterpartyPartyId", "PartyId"), ("*", "participantPartyId", "PartyId"), ("*", "recipientPartyId", "PartyId"),
        ("*", "requesterPartyId", "PartyId"), ("*", "survivorPartyId", "PartyId"), ("*", "vendorPartyId", "PartyId"),
        ("*", "fromPartyId", "PartyId"), ("*", "toPartyId", "PartyId"), ("*", "mergedPartyId", "PartyId"),
        ("*", "restoredPartyId", "PartyId"), ("*", "subjectPartyId", "PartyId"),
        ("*", "accountId", "AccountId"), ("*", "newAccountId", "AccountId"), ("*", "oldAccountId", "AccountId"),
        ("PTY", "sourceAccountId", "AccountId"), ("PTY", "targetAccountId", "AccountId"), ("POL", "targetAccountId", "AccountId"),
        ("*", "intermediaryId", "IntermediaryId"),
        ("*", "billingAccountId", "BillingAccountId"),
        ("*", "invoiceId", "InvoiceId"),
        ("*", "paymentId", "PaymentId"), ("*", "receiptId", "PaymentId"), ("*", "newPaymentId", "PaymentId"), ("*", "originalPaymentId", "PaymentId"),
        ("*", "refundId", "RefundId"),
        ("*", "disbursementId", "DisbursementId"),
        ("BIL", "planId", "PaymentPlanId"),
        ("*", "claimId", "ClaimId"),
        ("*", "exposureId", "ExposureId"),
        ("*", "fsCaseId", "FsCaseId"),
        ("CLM", "recoveryId", "RecoveryId"),
        ("*", "riContractId", "RiContractId"), ("RI", "contractId", "RiContractId"), ("RI", "affectedContractId", "RiContractId"),
        ("*", "programmeId", "RiProgrammeId"),
        ("RI", "layerId", "RiLayerId"),
        ("RI", "cessionId", "CessionId"),
        ("*", "journalId", "JournalId"),
        ("FIN", "periodId", "FinancialPeriodId"),
        ("*", "templateId", "TemplateId"),
        ("*", "clauseId", "ClauseId"),
        ("*", "documentId", "DocumentId"), ("*", "coverNoteDocumentId", "DocumentId"), ("*", "noticeDocumentId", "DocumentId"),
        ("*", "supersededByDocumentId", "DocumentId"),
        ("*", "deliveryId", "DeliveryId"), ("*", "fallbackDeliveryId", "DeliveryId"),
        ("*", "complaintId", "ComplaintId"),
        ("*", "dsarId", "DsarId"),
        ("*", "fiscalDocumentId", "FiscalDocumentId"), ("*", "originalFiscalDocumentId", "FiscalDocumentId"),
        ("*", "activityId", "ActivityId"),
        ("*", "queueId", "QueueId"), ("*", "assigneeQueueId", "QueueId"),
        ("common", "groupId", "WorkGroupId"), ("WRK", "groupId", "WorkGroupId"),
        ("*", "userId", "UserId"), ("*", "approverUserId", "UserId"), ("*", "assigneeUserId", "UserId"),
        ("*", "authorUserId", "UserId"), ("*", "checkerUserId", "UserId"), ("*", "decisionMakerUserId", "UserId"),
        ("*", "secondApproverUserId", "UserId"),
        ("*", "approvalRequestId", "ApprovalRequestId"),
        ("*", "authorityCheckId", "AuthorityCheckId"),
        ("*", "issueId", "UwIssueId"),
        ("*", "referralId", "ReferralId"),
        ("UW", "holdId", "PolicyHoldId"),
        ("*", "causationId", "EventId"));

    /// <summary>Property name → SharedKernel business-number type (common <c>BusinessNumber</c>).</summary>
    public static IReadOnlyDictionary<string, string> BusinessNumber { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["accountNumber"] = "AccountNumber",
        ["activityNumber"] = "ActivityNumber",
        ["claimNumber"] = "ClaimNumber",
        ["disbursementNumber"] = "DisbursementNumber",
        ["exposureNumber"] = "ExposureNumber",
        ["invoiceNumber"] = "InvoiceNumber",
        ["jobNumber"] = "JobNumber",
        ["journalNumber"] = "JournalNumber",
        ["partyNumber"] = "PartyNumber",
        ["policyNumber"] = "PolicyNumber",
        ["newPolicyNumber"] = "PolicyNumber",
        ["receiptNumber"] = "ReceiptNumber",
        ["documentNumber"] = "DocumentNumber",
    };

    /// <summary>Property name → SharedKernel hash type (common <c>Sha256</c>); others are <c>Sha256Hash</c>.</summary>
    public static IReadOnlyDictionary<string, string> Hash { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["configurationHash"] = "ConfigurationHash",
        ["resolutionHash"] = "ResolutionHash",
    };

    /// <summary>Unmapped Uuid property names seen during generation (for the report).</summary>
    public static SortedSet<string> Unmapped { get; } = new(StringComparer.Ordinal);

    public static TypeRef ForUuid(string scope, string? property)
    {
        if (property is not null
            && (Uuid.TryGetValue((scope, property), out var type) || Uuid.TryGetValue(("*", property), out type)))
        {
            return new TypeRef(Ids + type, true);
        }

        Unmapped.Add(property ?? "(unnamed)");
        return TypeRef.Guid with { Note = $"Untyped id: no SharedKernel id type is mapped for '{property ?? "(unnamed)"}' (tools/CoreIns.ContractGen/IdTypeMap.cs)." };
    }

    public static TypeRef ForBusinessNumber(string? property) =>
        property is not null && BusinessNumber.TryGetValue(property, out var type)
            ? new TypeRef(Ids + type, true)
            : TypeRef.String with { Note = "Business number issued by PLT numbering (format is configuration)." };

    public static TypeRef ForHash(string? property) =>
        new(Ids + (property is not null && Hash.TryGetValue(property, out var type) ? type : "Sha256Hash"), true);

    private static Dictionary<(string, string), string> Build(params (string Scope, string Name, string Type)[] entries)
    {
        var map = new Dictionary<(string, string), string>();
        foreach (var (scope, name, type) in entries)
        {
            map.Add((scope, name), type);
        }

        return map;
    }
}
