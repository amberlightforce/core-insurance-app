namespace CoreIns.Modules.Finance.Domain;

/// <summary>Posting relevance of a consumed event type in the finance event catalogue (REQ-FIN-030).</summary>
internal static class Relevance
{
    public const string Posting = "POSTING";
    public const string Context = "CONTEXT";
    public const string Ignored = "IGNORED";

    public static readonly string[] All = [Posting, Context, Ignored];
}

/// <summary>BusinessEvent states (PRD-09 §7.3.1). The slice uses Received, Waiting, Posted, NoPosting and Suspended.</summary>
internal static class BusinessEventStatus
{
    public const string Received = "RECEIVED";
    public const string Waiting = "WAITING";
    public const string Posted = "POSTED";
    public const string NoPosting = "NO_POSTING";
    public const string Suspended = "SUSPENDED";

    public static readonly string[] All = [Received, Waiting, Posted, NoPosting, Suspended];
}

/// <summary>
/// Why an event became an intake exception (Suspended, REQ-FIN-080). FIN never posts these to a suspense account
/// (REQ-FIN-084): the event waits in the intake exception queue for a fix and a re-run.
/// </summary>
internal static class ExceptionReasons
{
    public const string UnknownEvent = "UNKNOWN_EVENT";
    public const string InvalidEnvelope = "INVALID_ENVELOPE";
    public const string NoBookProfile = "NO_BOOK_PROFILE";
    public const string NoRuleSet = "NO_RULE_SET";
    public const string NoRule = "NO_RULE";
    public const string AmbiguousRule = "AMBIGUOUS_RULE";
    public const string NoChargeType = "NO_CHARGE_TYPE";
    public const string NoAccountDerivation = "NO_ACCOUNT_DERIVATION";
    public const string AccountInactive = "ACCOUNT_INACTIVE";
    public const string RateMissing = "RATE_MISSING";
    public const string Precision = "PRECISION";
    public const string Unbalanced = "UNBALANCED";
    public const string Empty = "EMPTY";
    public const string PostingError = "POSTING_ERROR";

    /// <summary>
    /// A servicing entry whose tax or levy payable movement disagrees with <c>TaxCalculator.treatment</c>, whose treatment
    /// is missing or cannot be obtained, or that lacks the dimensions the check needs (REQ-FIN-182, -183, D-SL3-05).
    /// </summary>
    public const string TaxRuleViolation = "TAX_RULE_VIOLATION";

    /// <summary>A PaymentVoided whose payment journal does not exist (yet): nothing to reverse (REQ-FIN-159, SL4-FIN-V4).</summary>
    public const string NoOriginalJournal = "NO_ORIGINAL_JOURNAL";

    /// <summary>A PaymentVoided for a payment journal that was already reversed, or a partial void.</summary>
    public const string VoidRefused = "VOID_REFUSED";

    /// <summary>A journal dated in a locked finance period that is not a correction (G5, REQ-POL-108): never posted into a closed period.</summary>
    public const string PeriodLocked = "PERIOD_LOCKED";
}

/// <summary>Journal-line side.</summary>
internal static class Sides
{
    public const string Debit = "DEBIT";
    public const string Credit = "CREDIT";

    public static string Opposite(string side) => side == Debit ? Credit : Debit;
}

/// <summary>Journal source types (REQ-FIN-067; the slice posts from events and reversals).</summary>
internal static class SourceTypes
{
    public const string Event = "EVENT";
    public const string Reversal = "REVERSAL";
}

/// <summary>Account derivation sources of a rule (REQ-FIN-050).</summary>
internal static class DeriveFrom
{
    public const string GlKey = "GL_KEY";
}

/// <summary>Rule-set version states used by the slice (PRD-09 §7.3.2: Active, Superseded; authoring states in W5-FIN-01).</summary>
internal static class RuleSetStatus
{
    public const string Active = "ACTIVE";
    public const string Superseded = "SUPERSEDED";
}

/// <summary>Dimension keys of BIL sub-ledger lines (BillingEntryPosted, agreed with SL-BIL; the contract leaves them open).</summary>
internal static class LineDimensionKeys
{
    public const string PolicyId = "policyId";
    public const string PolicyTermId = "policyTermId";
    public const string TransactionId = "transactionId";
    public const string ChargeId = "chargeId";
    public const string ChargeType = "chargeType";
    public const string ChargeCategory = "chargeCategory";
    public const string CoverageCode = "coverageCode";
    public const string ProductCode = "productCode";
    public const string BillingAccountId = "billingAccountId";
    public const string InvoiceId = "invoiceId";
    public const string ReceiptId = "receiptId";

    /// <summary>BIL disbursement entries (SL2-BIL-DISB, D-SL2-08) and the CLM legs FIN builds (<see cref="ClaimLegs"/>).</summary>
    public const string ClaimId = "claimId";
    public const string DisbursementId = "disbursementId";
    public const string SourceType = "sourceType";
    public const string SourceId = "sourceId";

    /// <summary>Keys FIN sets on the legs of a CLM fact (no BIL counterpart).</summary>
    public const string ExposureId = "exposureId";
    public const string ReserveLineId = "reserveLineId";
    public const string CostType = "costType";
    public const string CostCategory = "costCategory";
    public const string ClaimPaymentId = "claimPaymentId";

    /// <summary>Servicing dimensions of BIL entries (SL3-CONTRACTS, D-SL3-05): the POL transaction kind, the cancellation source and the MKT treatment rule.</summary>
    public const string TransactionKind = "transactionKind";
    public const string CancellationSource = "cancellationSource";
    public const string TreatmentRuleId = "treatmentRuleId";

    /// <summary>SL4 dimensions (BIL receivable and FS_CLEARING entries, SL4-CONTRACTS; FIN sets the same keys on its CLM and RI legs).</summary>
    public const string RecoveryId = "recoveryId";
    public const string StatementRef = "statementRef";
    public const string CounterpartyPartyId = "counterpartyPartyId";
    public const string RecoverableBasis = "recoverableBasis";
    public const string RiContractId = "riContractId";
    public const string RiContractYear = "riContractYear";
    public const string RiHeldGroup = "riHeldGroup";
}

/// <summary>Disbursement source types FIN reads on BIL disbursement entries (PRD-06 source register, D-SL2-10b).</summary>
internal static class DisbursementSources
{
    /// <summary>A CLM claim payment: the entry's <c>sourceId</c> is the claim payment id.</summary>
    public const string ClaimPayment = "CLM_CLAIM_PAYMENT";

    /// <summary>A policy refund (SL3, D-SL3-14): the entry's <c>sourceId</c> is the refund id; FIN nets the refund payable per refund on it.</summary>
    public const string BilRefund = "BIL_REFUND";

    /// <summary>A Friendly Settlement net (SL4, D-SL4-02): BIL FS_CLEARING entries; the clearing leg is LA-24.</summary>
    public const string FsClearing = "FS_CLEARING";
}

/// <summary>
/// Servicing entry types of BIL (SL3-CONTRACTS, D-SL3-01): credits and refunds. They always carry the line dimension
/// <c>transactionKind</c>, so their tax and levy movements can be checked against the MKT treatment (REQ-FIN-182).
/// </summary>
internal static class ServicingEntryTypes
{
    public const string CreditWritten = "CREDIT_WRITTEN";
    public const string CreditBilled = "CREDIT_BILLED";
    public const string RefundApproved = "REFUND_APPROVED";

    public static readonly string[] All = [CreditWritten, CreditBilled, RefundApproved];

    /// <summary>Entry types that carry the written or billed premium of a transaction; with a transaction kind (endorsement debit) they are checked too.</summary>
    public static readonly string[] Premium = ["WRITTEN", "BILLED"];
}

/// <summary>
/// BIL sub-ledger accounts whose movements are tax or levy payables (PRD-06 section 3.5): IPT payable LA-06, IPT written
/// not yet due LA-27, levy payable LA-07 and other tax payable (stamp duty) LA-26. A debit on one of them reduces the
/// authority liability (REQ-FIN-182).
/// </summary>
internal static class TaxPayableAccounts
{
    public static readonly string[] SubLedger = ["LA-06", "LA-27", "LA-07", "LA-26"];

    /// <summary>Charge categories of tax, levy and stamp lines (the MKT <c>TaxCategory</c>).</summary>
    public const string Tax = "TAX";
    public const string Levy = "LEVY";
    public const string Stamp = "STAMP";
}

/// <summary>
/// CLM events carry no sub-ledger accounts, so FIN expands each claim fact into fixed debit/credit legs whose "source
/// account" is one of these leg roles (D-SL2-08). The posting rules map each role to a book account, so the accounts
/// stay data (REQ-FIN-048); a role without a rule is an intake exception (NO_RULE), never a default account.
/// </summary>
internal static class ClaimLegs
{
    /// <summary>Incurred claims expense side of a case-reserve change.</summary>
    public const string Incurred = "CLM-INCURRED";

    /// <summary>LIC case reserve: credited by a reserve increase, debited by an eroding payment.</summary>
    public const string CaseReserve = "CLM-CASE-RESERVE";

    /// <summary>Claim payment clearing: credited by PaymentIssued, debited by BIL's DISBURSEMENT_RELEASED (REQ-FIN-037).</summary>
    public const string PaymentClearing = "CLM-PAYMENT-CLEARING";

    /// <summary>Debit side of a non-eroding payment (no rule in the slice: PRD-09 names no account for it).</summary>
    public const string PaidNonEroding = "CLM-PAID-NON-ERODING";

    /// <summary>Debit side of a recovery-reserve change (no rule in the slice: recoveries are out, D-SL2-01).</summary>
    public const string RecoveryReserve = "CLM-RECOVERY-RESERVE";

    /// <summary>Entry type of a reserve change: the reserve kind (RESERVE, RECOVERY_RESERVE).</summary>
    public const string Reserve = "RESERVE";

    /// <summary>Entry type of a recovery-reserve change.</summary>
    public const string RecoveryReserveKind = "RECOVERY_RESERVE";

    /// <summary>Entry type of an issued claim payment.</summary>
    public const string Payment = "PAYMENT";

    /// <summary>Entry type of a Friendly Settlement payable (method CLEARING): no rule in the slice, it never touches GL-2510 (REQ-FIN-299, D-SL2-12a).</summary>
    public const string PaymentFsClearing = "PAYMENT_FS_CLEARING";

    /// <summary>Claim payment method of a Friendly Settlement payable (PRD-07 REQ-CLM-264).</summary>
    public const string FsClearingMethod = "CLEARING";

    /// <summary>Leg role: Friendly Settlement clearing GL-2515 (payables credited, FS recoveries debited; BIL FS_CLEARING entries settle it).</summary>
    public const string FsClearing = "CLM-FS-CLEARING";

    /// <summary>Leg role: claim recovery clearing (salvage, subrogation): debited when recorded, credited by BIL's receipt (REQ-FIN-160).</summary>
    public const string RecoveryClearing = "CLM-RECOVERY-CLEARING";

    /// <summary>Leg role: recovery income of the recovery type (the entry type picks the account).</summary>
    public const string RecoveryIncome = "CLM-RECOVERY-INCOME";
}

/// <summary>Recovery entry types FIN posts (SL4-FIN-V4): one per CLM recovery type, so the rules can choose the income account per type.</summary>
internal static class RecoveryTypes
{
    public const string Subrogation = "SUBROGATION";
    public const string Salvage = "SALVAGE";
    public const string FriendlySettlement = "FRIENDLY_SETTLEMENT";

    public const string EntrySubrogation = "RECOVERY_SUBROGATION";
    public const string EntrySalvage = "RECOVERY_SALVAGE";
    public const string EntryFriendlySettlement = "RECOVERY_FRIENDLY_SETTLEMENT";

    /// <summary>The entry type of a recovery type, or null when the type is unknown (the fact is then an intake exception).</summary>
    public static string? EntryTypeOf(string recoveryType) => recoveryType switch
    {
        Subrogation => EntrySubrogation,
        Salvage => EntrySalvage,
        FriendlySettlement => EntryFriendlySettlement,
        _ => null,
    };
}

/// <summary>BIL entry types and sub-ledger accounts of the slice-4 postings (SL4-CONTRACTS, D-SL4-02, -06, -13) and the RI legs.</summary>
internal static class Sl4Entries
{
    public const string ReceivableRegistered = "RECEIVABLE_REGISTERED";
    public const string ReceivableCollected = "RECEIVABLE_COLLECTED";
    public const string FsClearingReleased = "FS_CLEARING_RELEASED";
    public const string FsClearingCleared = "FS_CLEARING_CLEARED";
    public const string DisbursementReversed = "DISBURSEMENT_REVERSED";

    /// <summary>Cash at bank (BIL LA-10) and the FS clearing sub-ledger account (LA-24).</summary>
    public const string CashAccount = "LA-10";
    public const string FsClearingAccount = "LA-24";

    /// <summary>
    /// Memo entries: the receivable is registered in the BIL sub-ledger but FIN books nothing, because the CLM RecoveryRecorded
    /// fact already debited the recovery clearing and the cash comes from the RECEIVABLE_COLLECTED entry (REQ-FIN-037).
    /// </summary>
    public static readonly string[] Memo = [ReceivableRegistered];

    /// <summary>Entry type and leg roles of a RI recoverable (REQ-FIN-164).</summary>
    public const string RiRecovery = "RECOVERY";
    public const string RiHeld = "RI-HELD-ASSET";
    public const string RiRecovered = "RI-RECOVERED";
    public const string Paid = "PAID";
    public const string Outstanding = "OUTSTANDING";
    public const string Unassigned = "UNASSIGNED";
    public const string RecoveryClearingRole = "RECOVERY-CLEARING";
}
