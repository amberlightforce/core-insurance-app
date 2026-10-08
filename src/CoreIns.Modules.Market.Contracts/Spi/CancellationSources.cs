namespace CoreIns.Modules.Market.Contracts.Spi;

/// <summary>
/// The cancellation-source code list (REQ-POL-205, shared code list R-84; PRD-17 §7.5). The list is held by MKT as the
/// code list <c>mkt.cancellation_source</c> and resolved through the configuration resolver; these constants are the
/// codes the core knows. The list is open: a pack may add a source, so consumers must treat an unknown value as
/// "no rule" (fail closed), never map it to a default.
/// </summary>
public static class CancellationSources
{
    /// <summary>The code list key in MKT configuration.</summary>
    public const string CodeList = "mkt.cancellation_source";

    /// <summary>Policyholder-requested cancellation.</summary>
    public const string Policyholder = "Policyholder";

    /// <summary>Insurer-initiated cancellation.</summary>
    public const string Insurer = "Insurer";

    /// <summary>Cancellation for non-payment.</summary>
    public const string NonPayment = "NonPayment";

    /// <summary>Distance-contract withdrawal (implied for the DISTANCE_WITHDRAWAL_VOID transaction kind).</summary>
    public const string DistanceWithdrawal = "DistanceWithdrawal";

    /// <summary>Withdrawal from a long-term contract.</summary>
    public const string LongTermWithdrawal = "LongTermWithdrawal";

    /// <summary>Objection to the contract.</summary>
    public const string Objection = "Objection";

    /// <summary>Statutory cancellation.</summary>
    public const string Statutory = "Statutory";

    /// <summary>All codes the core knows.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        Policyholder, Insurer, NonPayment, DistanceWithdrawal, LongTermWithdrawal, Objection, Statutory,
    ];
}
