using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.Modules.Reinsurance.Persistence;

/// <summary>
/// <c>ri.deposit_schedule</c>: one revision of the minimum and deposit premium schedule of a contract layer and year (BO-RI-10,
/// REQ-RI-153). Append-only: a reschedule is the next revision, the highest revision is current. The instalments are a JSON array
/// (seq, due date, amount) summing exactly to the deposit premium.
/// </summary>
internal sealed class DepositScheduleRow
{
    public Guid ScheduleId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public RiContractId ContractId { get; set; }

    public int LayerNo { get; set; }

    public int ContractYear { get; set; }

    public int Revision { get; set; }

    public string Currency { get; set; } = string.Empty;

    public decimal MinimumPremium { get; set; }

    public decimal DepositPremium { get; set; }

    public string Instalments { get; set; } = "[]";

    public string Reason { get; set; } = string.Empty;

    public Instant CreatedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;
}

/// <summary>
/// <c>ri.adjustment</c>: an adjustment premium calculation of a contract layer and year (BO-RI-10). Append-only; its single decision
/// is a row of <see cref="AccountingDecisionRow"/>. The amount is signed: positive is an additional premium, negative a return.
/// </summary>
internal sealed class AdjustmentRow
{
    public Guid AdjustmentId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public RiContractId ContractId { get; set; }

    public int LayerNo { get; set; }

    public int ContractYear { get; set; }

    public Guid ScheduleId { get; set; }

    public string Currency { get; set; } = string.Empty;

    public decimal SubjectPremium { get; set; }

    public decimal RatePct { get; set; }

    public decimal CalculatedPremium { get; set; }

    public decimal MinimumPremium { get; set; }

    public decimal DepositPremium { get; set; }

    public decimal AdjustmentAmount { get; set; }

    public Guid ApprovalRequestId { get; set; }

    public string? Reason { get; set; }

    public Instant CreatedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;
}

/// <summary>
/// <c>ri.commission</c>: a sliding-scale commission calculation of a contract year (BO-RI-10, REQ-RI-160). Append-only, so the table
/// is the commission history; its single decision is a row of <see cref="AccountingDecisionRow"/>.
/// </summary>
internal sealed class CommissionRow
{
    public Guid CommissionId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public RiContractId ContractId { get; set; }

    public int ContractYear { get; set; }

    public string Currency { get; set; } = string.Empty;

    public decimal CededPremium { get; set; }

    public decimal IncurredLoss { get; set; }

    public decimal LossRatioPct { get; set; }

    public decimal ProvisionalRatePct { get; set; }

    public decimal EarnedRatePct { get; set; }

    public decimal ProvisionalCommission { get; set; }

    public decimal EarnedCommission { get; set; }

    public decimal AdjustmentAmount { get; set; }

    /// <summary>The scale used, JSON array of (lossRatioFromPct, commissionRatePct).</summary>
    public string Scale { get; set; } = "[]";

    public Guid ApprovalRequestId { get; set; }

    public string? Reason { get; set; }

    public Instant CreatedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;
}

/// <summary>
/// <c>ri.accounting_decision</c>: the one decision of an adjustment or a commission calculation (BO-RI-10). The key is
/// (kind, subject), so a subject is decided at most once; append-only.
/// </summary>
internal sealed class AccountingDecisionRow
{
    /// <summary><c>ADJUSTMENT</c> or <c>COMMISSION</c>.</summary>
    public string Kind { get; set; } = string.Empty;

    public Guid SubjectId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    /// <summary><c>APPROVED</c> or <c>REJECTED</c>.</summary>
    public string Decision { get; set; } = string.Empty;

    public string? Reason { get; set; }

    public Instant DecidedAt { get; set; }

    public string DecidedBy { get; set; } = string.Empty;
}
