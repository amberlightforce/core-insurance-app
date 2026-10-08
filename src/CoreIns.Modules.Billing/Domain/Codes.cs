namespace CoreIns.Modules.Billing.Domain;

/// <summary>Upper-snake wire codes of the module's enums (stored in the database and published in events).</summary>
internal static class Codes
{
    public static string Of<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        string.Concat(value.ToString().Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + c : c.ToString())).ToUpperInvariant();

    public static TEnum Parse<TEnum>(string code)
        where TEnum : struct, Enum =>
        Enum.GetValues<TEnum>().First(value => string.Equals(Of(value), code, StringComparison.Ordinal));

    /// <summary>Check-constraint SQL listing every code of <typeparamref name="TEnum"/>.</summary>
    public static string CheckSql<TEnum>(string column)
        where TEnum : struct, Enum =>
        $"{column} IN ({string.Join(", ", Enum.GetValues<TEnum>().Select(v => $"'{Of(v)}'"))})";

    /// <summary>The contract enum with the same member name.</summary>
    public static TApi Api<TApi>(Enum value)
        where TApi : struct, Enum => Enum.Parse<TApi>(value.ToString());
}

/// <summary>Billing account states (PRD-06 §7.1.1: Active ↔ Suspended; Active → Closing → Closed; Closed → Active).</summary>
internal enum BillingAccountStatus
{
    Active,
    Suspended,
    Closing,
    Closed,
}

/// <summary>
/// Processing state of a consumed POL charge delta (REQ-BIL-002, REQ-BIL-364). The received columns of a charge are
/// frozen (a trigger refuses to change them); only this state and its links move.
/// </summary>
internal enum ChargeStatus
{
    /// <summary>Stored as received; waiting for its term (PolicyBound) to be attached.</summary>
    Received,

    /// <summary>Validated and its written entry posted (REQ-BIL-066); waiting for set completeness to schedule.</summary>
    Written,

    /// <summary>Accrued only (billing treatment ACCRUED_NOT_BILLED, REQ-BIL-068): never invoiced.</summary>
    Accrued,

    /// <summary>Scheduled into an invoice item (REQ-BIL-068, REQ-BIL-364).</summary>
    Scheduled,

    /// <summary>Failed validation; no entry posted (REQ-BIL-065, REQ-BIL-287).</summary>
    Quarantined,
}

/// <summary>Invoice item states (PRD-06 §7.3.2).</summary>
internal enum InvoiceItemState
{
    Planned,
    Billed,
    Open,
    Settled,
    Cancelled,
    WrittenOff,
}

/// <summary>Status of the fiscal document requested for an invoice's charges (REQ-BIL-096, REQ-BIL-098).</summary>
internal enum FiscalStatus
{
    NotRequested,
    Pending,
    Registered,
    Rejected,

    /// <summary>The request to CMP failed (e.g. no channel bound); billing goes on (REQ-BIL-099).</summary>
    RequestFailed,
}

/// <summary>Kinds of intake exceptions (activities BIL-DELTA-EXCEPTION, BIL-ERR-PLAN; WRK is not wired in the slice).</summary>
internal static class ExceptionKinds
{
    public const string DeltaException = "BIL-DELTA-EXCEPTION";
    public const string SetBlocked = "BIL-DELTA-SET-BLOCKED";
    public const string PlanUnsupported = "BIL-PLAN-UNSUPPORTED";
    public const string FiscalRequestFailed = "BIL-FISCAL-REQUEST-FAILED";
    public const string FiscalRejected = "CMP-FISCAL-REJECTED";
}

/// <summary>Receipt channels and methods served by SL-BIL.</summary>
internal static class ReceiptCodes
{
    public const string ChannelStaff = "STAFF";
    public const string BankTransfer = "BANK_TRANSFER";
    public const string Cashier = "CASHIER";

    /// <summary>Suspense reasons (REQ-BIL-135).</summary>
    public const string AmountMismatch = "AMOUNT_MISMATCH";
    public const string NoOpenInvoice = "NO_OPEN_INVOICE";
    public const string AmbiguousMatch = "AMBIGUOUS_MATCH";
    public const string InvoiceNotOpen = "INVOICE_NOT_OPEN";
    public const string ConcurrentAllocation = "CONCURRENT_ALLOCATION";

    /// <summary>Deterministic matching rules (REQ-BIL-127) and the manual rule.</summary>
    public const string RuleReferencedInvoice = "REFERENCED_INVOICE";
    public const string RuleUniqueOpenAmount = "UNIQUE_OPEN_AMOUNT";
    public const string RuleManual = "MANUAL";
}

/// <summary>Status of a payee account (PRD-06 §7.1 PaymentInstrument: Active, Superseded, Revoked).</summary>
internal enum PayeeAccountStatus
{
    Active,
    Superseded,
    Revoked,
}

/// <summary>Verification status of a payee account (REQ-BIL-345).</summary>
internal enum PayeeVerification
{
    Unverified,
    VopMatched,
    VopCloseMatch,
    VopNoMatch,
    VopNotAvailable,
    Confirmed,
}

/// <summary>Outcome of one verification of payee (REQ-BIL-203).</summary>
internal enum VopOutcome
{
    Match,
    CloseMatch,
    NoMatch,
    NotAvailable,
}

/// <summary>Screening outcomes stored on a disbursement (PTY <c>pty.Screening.screen</c>, REQ-BIL-200).</summary>
internal static class ScreeningCodes
{
    public const string Clear = "CLEAR";
}

/// <summary>Payment plans served by the slice (D-SLC-10c: the happy path uses ANNUAL; BIL defines no other plan).</summary>
internal static class PaymentPlans
{
    /// <summary>One instalment for the whole term, billed when the charges arrive and due on the billing date.</summary>
    public const string Annual = "ANNUAL";

    /// <summary>Direct bill (the slice has no agency bill).</summary>
    public const string DirectBill = "DIRECT_BILL";
}
