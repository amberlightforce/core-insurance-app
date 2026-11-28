using System.ComponentModel.DataAnnotations;
using CoreIns.SharedKernel;

namespace CoreIns.Modules.Billing;

/// <summary>
/// Configuration section <c>Billing</c>. Stamp settings, not regulatory values: the legal entity's time zone (business
/// and accounting dates) and the account currency used when a term arrives before any of its charges (PolicyBound
/// carries no currency).
/// </summary>
internal sealed class BillingOptions
{
    /// <summary>Configuration section.</summary>
    public const string Section = "Billing";

    /// <summary>IANA time zone of the stamp's legal entity (e.g. Europe/Athens).</summary>
    [Required]
    public string TimeZone { get; set; } = string.Empty;

    /// <summary>ISO 4217 currency of a new account when the term's charges have not arrived yet.</summary>
    [Required]
    [RegularExpression("^[A-Z]{3}$")]
    public string DefaultCurrency { get; set; } = string.Empty;

    /// <summary>
    /// Cooling-off days of a new or changed payee account, <c>bil.payee.cooling_off.&lt;purpose&gt;</c> (PRD-06 §10.2
    /// default 30, one value for every purpose until MKT serves the key, BR-BIL-062).
    /// </summary>
    [Range(0, 365)]
    public int PayeeCoolingOffDays { get; set; } = 30;

    /// <summary>
    /// ILLUSTRATIVE (no approved value; PRD-06 default <c>bil.refund.minimum</c>): the smallest refund BIL proposes, in the account
    /// currency. A smaller credit stays on the account (REQ-BIL-182).
    /// </summary>
    [Range(typeof(decimal), "0.01", "100000")]
    public decimal RefundMinimum { get; set; } = 5.00m;

    /// <summary>
    /// ILLUSTRATIVE (D-SL3-08, D-SL3-14): refunds up to this total to an unchanged payee are approved by rule; anything above, a
    /// changed payee and every resubmission after a rejection needs a second person under <c>BIL.REFUND</c> authority.
    /// </summary>
    [Range(typeof(decimal), "0", "100000000")]
    public decimal RefundAutoApproveLimit { get; set; } = 500.00m;

    /// <summary>The role whose inbox receives a refund approval (WRK activity <c>BIL-REFUND-APPROVAL</c>, REQ-BIL-188).</summary>
    [Required]
    public string RefundApproverRole { get; set; } = "Staff.BillingManager";

    /// <summary>
    /// Policyholder type sent with <c>TaxCalculator.treatment</c> (REQ-BIL-079). A technical default: the consumed events
    /// carry no policyholder type, and the Greece treatment rows do not depend on it.
    /// </summary>
    public Market.Contracts.Spi.PolicyholderType TreatmentPolicyholderType { get; set; } = Market.Contracts.Spi.PolicyholderType.Consumer;

    /// <summary>Business basis code sent with <c>TaxCalculator.treatment</c>; technical default, see <see cref="TreatmentPolicyholderType"/>.</summary>
    [Required]
    public string TreatmentBusinessBasis { get; set; } = "ESTABLISHMENT";

    /// <summary>
    /// ILLUSTRATIVE (D-SL6-04; <c>bil.delinq.grace</c>): days an invoice may stay unpaid after its due date before a delinquency case
    /// is opened.
    /// </summary>
    [Range(0, 365)]
    public int DelinquencyGraceDays { get; set; } = 5;

    /// <summary>
    /// ILLUSTRATIVE (D-SL6-04, INV-11; <c>bil.delinq.tolerance</c>): arrears up to this amount do not lead to a cancellation request
    /// when the waiting period elapses. The PRD names no value.
    /// </summary>
    [Range(typeof(decimal), "0", "100000")]
    public decimal DelinquencyTolerance { get; set; } = 5.00m;

    /// <summary>
    /// ILLUSTRATIVE (BO-BIL-13; fallback of MKT key <c>bil.writeoff.tolerance</c>): a residual up to this amount, being the whole remaining
    /// balance of an invoice, is written off at once without a second person. The PRDs name no value; 0 switches the automatic write-off off.
    /// </summary>
    [Range(typeof(decimal), "0", "100000")]
    public decimal WriteOffTolerance { get; set; } = 1.00m;

    /// <summary>Whether the worker role runs the scheduled overdue sweep (NX-HD-01). Switch off to pause case opening.</summary>
    public bool DelinquencySweepEnabled { get; set; } = true;

    /// <summary>Seconds between overdue sweeps (default hourly; the grace period is counted in days).</summary>
    [Range(1, 86400)]
    public int DelinquencySweepIntervalSeconds { get; set; } = 3600;

    /// <summary>Whether the worker role runs the scheduled SEPA direct-debit collection run (BO-BIL-06b). Off by default: a legal entity switches it on once its creditor identifier is set.</summary>
    public bool DirectDebitEnabled { get; set; }

    /// <summary>Seconds between collection runs (default hourly).</summary>
    [Range(1, 86400)]
    public int DirectDebitIntervalSeconds { get; set; } = 3600;

    /// <summary>
    /// ILLUSTRATIVE (no approved value): days before an instalment's due date at which it goes into a pain.008 file (SEPA Core needs at least
    /// one business day for RCUR and FRST since 2016; two keeps a weekend margin).
    /// </summary>
    [Range(0, 30)]
    public int DirectDebitLeadDays { get; set; } = 2;

    /// <summary>SEPA creditor name (pain.008 <c>Cdtr/Nm</c>); the run fails closed while empty.</summary>
    public string DirectDebitCreditorName { get; set; } = string.Empty;

    /// <summary>SEPA creditor identifier (<c>CdtrSchmeId</c>) of the legal entity; the run fails closed while empty.</summary>
    public string DirectDebitCreditorId { get; set; } = string.Empty;

    /// <summary>The creditor account the collections are credited to (own account, not P2); the run fails closed while empty.</summary>
    public string DirectDebitCreditorIban { get; set; } = string.Empty;

    /// <summary>Core money-source register rows (REQ-BIL-354); an absent row fails closed.</summary>
    public Dictionary<string, BillingSourceRow> Sources { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The configured zone.</summary>
    public TimeZoneInfo Zone => TimeZoneInfo.FindSystemTimeZoneById(TimeZone);

    /// <summary>The configured default currency.</summary>
    public Currency Currency => Currency.FromCode(DefaultCurrency);
}

internal sealed class BillingSourceRow
{
    public string CallingModule { get; set; } = string.Empty;
    public string Direction { get; set; } = string.Empty;
    public string[] AllowedMethods { get; set; } = [];
    public string ApprovalEvidenceType { get; set; } = string.Empty;
    public string[] LedgerRuleRows { get; set; } = [];
    public string FinancePostingRuleReference { get; set; } = string.Empty;
}

