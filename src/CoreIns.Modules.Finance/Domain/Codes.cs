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
}
