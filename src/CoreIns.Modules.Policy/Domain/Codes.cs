using CoreIns.Modules.Policy.Contracts;
using CoreIns.Modules.Policy.Contracts.Api;

namespace CoreIns.Modules.Policy.Domain;

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

    /// <summary>The contract enum of a job state (same member names).</summary>
    public static JobStateCode Api(JobState state) => Enum.Parse<JobStateCode>(state.ToString());

    /// <summary>The contract enum of a term state (same member names).</summary>
    public static TermStateCode Api(PolicyTermState state) => Enum.Parse<TermStateCode>(state.ToString());
}

/// <summary>Job types of PRD-05 §7.3.2 (slice 3 adds the servicing jobs).</summary>
internal enum JobType
{
    /// <summary>New business.</summary>
    Submission,

    /// <summary>Mid-term change of a term (endorsement).</summary>
    PolicyChange,

    /// <summary>Cancellation of a term.</summary>
    Cancellation,

    /// <summary>Renewal: the next term of an expiring one.</summary>
    Renewal,
}

/// <summary>
/// Job sub-states (contract §3.2.4: QuickQuote and Converting under Draft, Offered and Accepted under Quoted). A sub-state
/// is a refinement of the state, stored beside it, never a state of its own.
/// </summary>
internal enum JobSubState
{
    /// <summary>Draft: a quick quote being prepared.</summary>
    QuickQuote,

    /// <summary>Draft: a quick quote being converted to a full one.</summary>
    Converting,

    /// <summary>Quoted: the renewal offer has been issued to the customer (<c>Quoted.Offered</c>).</summary>
    Offered,

    /// <summary>Quoted: the renewal offer was accepted explicitly (<c>Quoted.Accepted</c>).</summary>
    Accepted,
}

/// <summary>Whether a cancellation is a standard one (pro rata) or a flat one (from inception, REQ-POL-214).</summary>
internal enum CancellationKind
{
    /// <summary>Cancelled from its effective time on.</summary>
    Standard,

    /// <summary>Cancelled from inception: the written amount is credited exactly.</summary>
    Flat,
}

/// <summary>
/// The MKT transaction kinds (PRD-17 <c>TaxCalculator.treatment</c>) a charge line is tagged with. A mid-term change is
/// <c>EndorsementDebit</c> or <c>EndorsementCredit</c> per line by the sign of the delta; <c>ENDORSEMENT</c> alone is invalid.
/// </summary>
internal enum TaxTransactionKind
{
    /// <summary>First issuance and renewals.</summary>
    NewBusiness,

    /// <summary>A mid-term change that adds charge.</summary>
    EndorsementDebit,

    /// <summary>A mid-term change that removes charge.</summary>
    EndorsementCredit,

    /// <summary>Cancellation (needs a cancellation source).</summary>
    Cancellation,

    /// <summary>Distance-withdrawal void.</summary>
    DistanceWithdrawalVoid,

    /// <summary>Void ab initio (needs a cancellation source).</summary>
    Void,

    /// <summary>Return premium.</summary>
    ReturnPremium,

    /// <summary>Reinstatement.</summary>
    Reinstatement,

    /// <summary>Fee.</summary>
    Fee,

    /// <summary>Refund.</summary>
    Refund,
}

/// <summary>
/// The cancellation sources of the shared MKT code list (REQ-POL-205), as MKT publishes them. Stored as given; a source the
/// code list does not carry is refused by the command, not by the database.
/// </summary>
internal static class CancellationSources
{
    public const string Policyholder = "Policyholder";

    public const string Insurer = "Insurer";

    public const string NonPayment = "NonPayment";

    public const string DistanceWithdrawal = "DistanceWithdrawal";

    public const string LongTermWithdrawal = "LongTermWithdrawal";

    public const string Objection = "Objection";

    public const string Statutory = "Statutory";

    /// <summary>All sources of the code list.</summary>
    public static IReadOnlyList<string> All { get; } = [Policyholder, Insurer, NonPayment, DistanceWithdrawal, LongTermWithdrawal, Objection, Statutory];
}

/// <summary>
/// Names (without the <c>POL-ERR-</c> prefix) of the error codes the temporal and servicing commands raise. The wire codes
/// are the generated <c>PolicyErrorCodes</c>; these are the names handed to <c>DomainError.Of(ModuleCode.POL, …)</c>.
/// </summary>
internal static class PolicyErrorNames
{
    /// <summary>409: the policy or job changed meanwhile (a racing writer, a lock wait that ran out).</summary>
    public const string Stale = "STALE";

    /// <summary>422: the effective time is earlier than the latest bound non-reversed transaction of the term (D-SL3-02).</summary>
    public const string OutOfSequence = "OUT-OF-SEQUENCE";

    /// <summary>409: the base transaction of a change is no longer the term's head.</summary>
    public const string Preempted = "PREEMPTED";

    /// <summary>409: the expiring term's head moved since the renewal was created.</summary>
    public const string RebaseRequired = "REBASE-REQUIRED";

    /// <summary>422: the term is cancelled; changes after the cancellation are refused.</summary>
    public const string AfterCancellation = "AFTER-CANCELLATION";

    /// <summary>422: the effective date is outside the permitted range.</summary>
    public const string EffdateLimit = "EFFDATE-LIMIT";

    /// <summary>422: the snapshot reference or time is malformed or forged.</summary>
    public const string Validation = "VALIDATION";
}

/// <summary>Charge categories returned by RAT (PRD-03: taxes and levies are separate charge types of category tax or levy).</summary>
internal static class ChargeCategories
{
    public const string Premium = "PREMIUM";

    public const string Tax = "TAX";

    public const string Levy = "LEVY";
}

/// <summary>Delta kinds of a charge delta (PRD-05 §7.1). NET is the P1 mode (REQ-POL-121).</summary>
internal static class DeltaKinds
{
    public const string Net = "NET";
}
