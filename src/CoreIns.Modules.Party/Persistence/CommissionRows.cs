using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.Modules.Party.Persistence;

/// <summary><c>pty.commission_agreement</c> (BO-PTY-10): one agreement per intermediary and scope (product, line; null = all).</summary>
internal sealed class CommissionAgreementRow
{
    public Guid AgreementId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public IntermediaryId IntermediaryId { get; set; }

    public string? ProductCode { get; set; }

    public string? LineOfBusiness { get; set; }

    /// <summary>Product and line joined, "*" for all: the unique key of the scope.</summary>
    public string ScopeKey { get; set; } = string.Empty;

    public Instant CreatedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;
}

/// <summary>
/// <c>pty.commission_agreement_version</c>: DRAFT, PENDING_APPROVAL, ACTIVE or REJECTED. The rates and dates of a version that is not a
/// draft never change; the end of a version is derived from the next ACTIVE one.
/// </summary>
internal sealed class CommissionAgreementVersionRow
{
    public Guid VersionId { get; set; }

    public Guid AgreementId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public int VersionNo { get; set; }

    public string Status { get; set; } = string.Empty;

    public BusinessDate EffectiveFrom { get; set; }

    public decimal ExpectedVolume { get; set; }

    public string Currency { get; set; } = string.Empty;

    public decimal MaxRatePct { get; set; }

    public string ContentHash { get; set; } = string.Empty;

    public Guid? ApprovalRequestId { get; set; }

    public string? Notes { get; set; }

    public Instant CreatedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public string? SubmittedBy { get; set; }

    public Instant? SubmittedAt { get; set; }

    public string? DecidedBy { get; set; }

    public Instant? DecidedAt { get; set; }

    public string? DecisionComment { get; set; }
}

/// <summary><c>pty.commission_rate_line</c>: one rate of a version.</summary>
internal sealed class CommissionRateLineRow
{
    public Guid LineId { get; set; }

    public Guid VersionId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public int LineNo { get; set; }

    public string BusinessType { get; set; } = string.Empty;

    public string? LineOfBusiness { get; set; }

    public string Basis { get; set; } = string.Empty;

    public decimal RatePct { get; set; }
}
