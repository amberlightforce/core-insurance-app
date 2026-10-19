using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.Modules.Billing.Persistence;

/// <summary>A non-payment (delinquency) case of one policy term: notice, waiting clock, cure or cancellation request.</summary>
internal sealed class DelinquencyCaseRow
{
    public Guid CaseId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public BillingAccountId BillingAccountId { get; set; }

    public PolicyTermId TermId { get; set; }

    public PolicyId PolicyId { get; set; }

    public string ProductCode { get; set; } = string.Empty;

    public string PlanCode { get; set; } = string.Empty;

    public string State { get; set; } = string.Empty;

    public string Trigger { get; set; } = string.Empty;

    public decimal OverdueAmount { get; set; }

    public string Currency { get; set; } = string.Empty;

    public DocumentId? NoticeDocumentId { get; set; }

    public BusinessDate? NotificationDate { get; set; }

    public Guid? ClockInstanceId { get; set; }

    public Instant? Deadline { get; set; }

    public BusinessDate? PromiseDate { get; set; }

    public string? HoldReason { get; set; }

    public string? Outcome { get; set; }

    public Guid? CancellationRequestId { get; set; }

    public Instant OpenedAt { get; set; }

    public Instant? ResolvedAt { get; set; }

    public Instant? UpdatedAt { get; set; }

    public int RecordVersion { get; set; }
}

/// <summary>A contact with the payer about a case (phone, letter, e-mail).</summary>
internal sealed class DelinquencyContactRow
{
    public Guid ContactId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public Guid CaseId { get; set; }

    public string Channel { get; set; } = string.Empty;

    public string Outcome { get; set; } = string.Empty;

    public string? Note { get; set; }

    public string RecordedBy { get; set; } = string.Empty;

    public Instant RecordedAt { get; set; }
}
