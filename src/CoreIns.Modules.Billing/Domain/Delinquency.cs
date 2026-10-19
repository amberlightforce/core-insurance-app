namespace CoreIns.Modules.Billing.Domain;

/// <summary>States of a delinquency case (PRD-06 collections, SL6-BIL-DELINQ).</summary>
internal enum DelinquencyState
{
    /// <summary>The DT-NONPAY-NOTICE is requested from DOC; no delivery proof yet, no clock (D-SL6-03).</summary>
    NoticeRequested,

    /// <summary>The notice was delivered with proof and the BIL_NONPAY_NOTICE clock runs.</summary>
    ClockRunning,

    /// <summary>The registered letter came back: no notification date, no clock (D-SL6-03); a work request was recorded.</summary>
    NoticeReturned,

    /// <summary>A vulnerable-customer hold paused the clock (D-SL6-04).</summary>
    OnHold,

    /// <summary>A promise to pay paused the clock.</summary>
    Arranged,

    /// <summary>The clock elapsed with arrears above tolerance: <c>CancellationForNonPaymentRequested</c> was emitted.</summary>
    CancellationRequested,

    /// <summary>Closed: paid, below tolerance, or arrangement kept.</summary>
    Resolved,
}

/// <summary>What opened a delinquency case.</summary>
internal enum DelinquencyTrigger
{
    /// <summary>A SEPA return (AM04, MS03) left arrears and re-presentation failed.</summary>
    PaymentReturn,

    /// <summary>An invoice stayed unpaid past <c>bil.delinq.grace</c>.</summary>
    Overdue,
}

/// <summary>Constants of the non-payment process.</summary>
internal static class Nonpay
{
    public const string NoticeTemplate = "DT-NONPAY-NOTICE";
    public const string ClockCode = "BIL_NONPAY_NOTICE";
    public const string ObjectType = "DelinquencyCase";
    public const string NoticeReturnedActivity = "WRK-BIL-NONPAY-NOTICE-RETURNED";
    public const string HoldActivity = "WRK-BIL-NONPAY-PSR-HOLD";
    public const string PlanVersion = "1";

    /// <summary>Case states that are still open (a term has at most one).</summary>
    public static readonly string[] OpenStates =
    [
        Codes.Of(DelinquencyState.NoticeRequested), Codes.Of(DelinquencyState.ClockRunning), Codes.Of(DelinquencyState.NoticeReturned),
        Codes.Of(DelinquencyState.OnHold), Codes.Of(DelinquencyState.Arranged),
    ];
}
