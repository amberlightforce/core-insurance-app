using CoreIns.SharedKernel;

namespace CoreIns.Modules.Finance.Persistence;

// Rows of the fin schema (PRD-09 §7.1). Reference data (book profile, chart, derivations, catalogue, rule sets) is keyed
// by legal entity code and loaded from versioned seed files by migrations; transactional rows carry legal_entity_id.
// Journal entries and lines are append-only (triggers and grants, REQ-FIN-070); business events change state.

/// <summary><c>fin.book_profile</c> (REQ-FIN-089): active books, statutory book and functional currency per legal entity.</summary>
internal sealed class BookProfileRow
{
    public string LegalEntityCode { get; set; } = string.Empty;

    public string FunctionalCurrency { get; set; } = string.Empty;

    public string StatutoryBook { get; set; } = string.Empty;

    public string[] ActiveBooks { get; set; } = [];

    public string Source { get; set; } = string.Empty;
}

/// <summary><c>fin.gl_account</c> (REQ-FIN-004, -091): one account of a legal entity's chart for a book.</summary>
internal sealed class GlAccountRow
{
    public string LegalEntityCode { get; set; } = string.Empty;

    public string Book { get; set; } = string.Empty;

    public string AccountCode { get; set; } = string.Empty;

    public string NameEl { get; set; } = string.Empty;

    public string NameEn { get; set; } = string.Empty;

    public string AccountType { get; set; } = string.Empty;

    public string NormalBalance { get; set; } = string.Empty;

    /// <summary>PRD09_ILLUSTRATIVE (PRD-09 §7.1.4 illustrative code) or TECHNICAL_PLACEHOLDER (not in the PRD).</summary>
    public string CodeOrigin { get; set; } = string.Empty;

    public bool SystemOnly { get; set; }

    public string Status { get; set; } = string.Empty;

    public string Source { get; set; } = string.Empty;
}

/// <summary><c>fin.account_derivation</c> (REQ-FIN-050): PFC GL key → account per book, effective-dated.</summary>
internal sealed class AccountDerivationRow
{
    public string LegalEntityCode { get; set; } = string.Empty;

    public string Book { get; set; } = string.Empty;

    public string GlKey { get; set; } = string.Empty;

    public string AccountCode { get; set; } = string.Empty;

    public BusinessDate ValidFrom { get; set; }

    public BusinessDate? ValidTo { get; set; }
}

/// <summary><c>fin.event_catalogue</c> (REQ-FIN-030): every consumed event type with its posting relevance.</summary>
internal sealed class EventCatalogueRow
{
    public string RegistryName { get; set; } = string.Empty;

    public int SchemaMajor { get; set; }

    public string Relevance { get; set; } = string.Empty;

    public string AmountFields { get; set; } = string.Empty;

    public string Note { get; set; } = string.Empty;
}

/// <summary><c>fin.posting_rule_set</c> (REQ-FIN-052): a versioned rule set per legal entity and book.</summary>
internal sealed class PostingRuleSetRow
{
    public Guid RuleSetId { get; set; }

    public string LegalEntityCode { get; set; } = string.Empty;

    public string Book { get; set; } = string.Empty;

    public int VersionNo { get; set; }

    public string Status { get; set; } = string.Empty;

    public BusinessDate EffectiveFrom { get; set; }

    public string ContentHash { get; set; } = string.Empty;

    public string Source { get; set; } = string.Empty;

    public Instant CreatedAt { get; set; }
}

/// <summary><c>fin.posting_rule</c> (REQ-FIN-048): one rule of a rule-set version.</summary>
internal sealed class PostingRuleRow
{
    public Guid RuleId { get; set; }

    public Guid RuleSetId { get; set; }

    public string RuleCode { get; set; } = string.Empty;

    public string SourceEvent { get; set; } = string.Empty;

    public string EntryType { get; set; } = string.Empty;

    public string SourceAccount { get; set; } = string.Empty;

    public string? ChargeCategory { get; set; }

    public string? ChargeType { get; set; }

    public int Specificity { get; set; }

    public string? AccountCode { get; set; }

    public string? DeriveFrom { get; set; }

    public string DescriptionEl { get; set; } = string.Empty;

    public string DescriptionEn { get; set; } = string.Empty;
}

/// <summary><c>fin.charge_type_view</c>: PFC charge types of a product artefact (GL key, category), immutable per hash.</summary>
internal sealed class ChargeTypeViewRow
{
    public string ArtefactHash { get; set; } = string.Empty;

    public string ChargeType { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public string GlKey { get; set; } = string.Empty;

    public string? Coverage { get; set; }

    public Instant LoadedAt { get; set; }
}

/// <summary><c>fin.policy_context</c>: what FIN keeps from POL PolicyBound per term (CONTEXT relevance).</summary>
internal sealed class PolicyContextRow
{
    public Guid PolicyTermId { get; set; }

    public Guid LegalEntityId { get; set; }

    public Guid PolicyId { get; set; }

    public string PolicyNumber { get; set; } = string.Empty;

    public Guid TransactionId { get; set; }

    public string ProductCode { get; set; } = string.Empty;

    public string ProductVersion { get; set; } = string.Empty;

    public string ArtefactHash { get; set; } = string.Empty;

    public bool ChargeTypesLoaded { get; set; }

    public Guid SourceEventId { get; set; }

    public long AggregateSequence { get; set; }

    public Instant RecordedAt { get; set; }

    public int RecordVersion { get; set; }
}

/// <summary><c>fin.business_event</c> (REQ-FIN-034): every consumed event, normalised, with its intake state (§7.3.1).</summary>
internal sealed class BusinessEventRow
{
    public Guid BusinessEventId { get; set; }

    public Guid? LegalEntityId { get; set; }

    public string LegalEntityCode { get; set; } = string.Empty;

    public string Jurisdiction { get; set; } = string.Empty;

    public string SourceModule { get; set; } = string.Empty;

    public Guid SourceEventId { get; set; }

    public string EventType { get; set; } = string.Empty;

    public string RegistryName { get; set; } = string.Empty;

    public string SchemaVersion { get; set; } = string.Empty;

    public string AggregateType { get; set; } = string.Empty;

    public string AggregateId { get; set; } = string.Empty;

    public long AggregateSequence { get; set; }

    /// <summary>True when the event arrived after a later event of its aggregate (a replayed dead letter, D-ARC-26).</summary>
    public bool OutOfOrder { get; set; }

    public string Origin { get; set; } = string.Empty;

    public Instant OccurredAt { get; set; }

    public Guid? SetId { get; set; }

    public int? SetSize { get; set; }

    public int? SetIndex { get; set; }

    public string Relevance { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    /// <summary>Dependency the event waits for (e.g. <c>term:&lt;id&gt;</c>), REQ-FIN-040.</summary>
    public string? WaitingOn { get; set; }

    public string? ExceptionReason { get; set; }

    public string? ExceptionDetail { get; set; }

    public BusinessDate? AccountingDate { get; set; }

    public string Payload { get; set; } = "{}";

    public string BusinessKeys { get; set; } = "{}";

    public string ConfigurationHash { get; set; } = string.Empty;

    public string CorrelationId { get; set; } = string.Empty;

    public Guid? PolicyId { get; set; }

    public Guid? PolicyTermId { get; set; }

    public Guid? BillingAccountId { get; set; }

    public Guid[] JournalIds { get; set; } = [];

    public int Attempts { get; set; }

    public Instant ReceivedAt { get; set; }

    public Instant UpdatedAt { get; set; }

    public int RecordVersion { get; set; }
}

/// <summary><c>fin.financial_period</c>: calendar-month accounting periods (close states arrive with W5-FIN close work).</summary>
internal sealed class FinancialPeriodRow
{
    public Guid PeriodId { get; set; }

    public Guid LegalEntityId { get; set; }

    public string PeriodCode { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public Instant CreatedAt { get; set; }
}

/// <summary><c>fin.journal_entry</c> (REQ-FIN-067): append-only journal header.</summary>
internal sealed class JournalEntryRow
{
    public Guid JournalId { get; set; }

    public string JournalNumber { get; set; } = string.Empty;

    public Guid LegalEntityId { get; set; }

    public string LegalEntityCode { get; set; } = string.Empty;

    public string Jurisdiction { get; set; } = string.Empty;

    public string Book { get; set; } = string.Empty;

    public BusinessDate AccountingDate { get; set; }

    public BusinessDate BusinessDate { get; set; }

    public Guid PeriodId { get; set; }

    public string SourceType { get; set; } = string.Empty;

    public string SourceModule { get; set; } = string.Empty;

    public string SourceEventType { get; set; } = string.Empty;

    public Guid[] SourceEventIds { get; set; } = [];

    public string SourceRef { get; set; } = string.Empty;

    public Guid RuleSetId { get; set; }

    public int RuleSetVersion { get; set; }

    public string[] RuleCodes { get; set; } = [];

    public Guid? ReversesJournalId { get; set; }

    public string? Reason { get; set; }

    public string FunctionalCurrency { get; set; } = string.Empty;

    public string CorrelationId { get; set; } = string.Empty;

    public Instant PostedAt { get; set; }

    public string PostedBy { get; set; } = string.Empty;
}

/// <summary><c>fin.journal_line</c> (REQ-FIN-067, -075): append-only line with amounts and dimensions.</summary>
internal sealed class JournalLineRow
{
    public Guid LineId { get; set; }

    public Guid JournalId { get; set; }

    public int LineNo { get; set; }

    public Guid LegalEntityId { get; set; }

    public string Book { get; set; } = string.Empty;

    public string AccountCode { get; set; } = string.Empty;

    public string Side { get; set; } = string.Empty;

    public decimal Amount { get; set; }

    public string Currency { get; set; } = string.Empty;

    public decimal AmountFunctional { get; set; }

    public string FunctionalCurrency { get; set; } = string.Empty;

    public string RuleCode { get; set; } = string.Empty;

    public BusinessDate BusinessDate { get; set; }

    public string? ProductCode { get; set; }

    public string? ProductVersion { get; set; }

    public string? CoverageCode { get; set; }

    public string? ChargeType { get; set; }

    public string? ChargeCategory { get; set; }

    public string? GlKey { get; set; }

    public Guid? PolicyId { get; set; }

    public string? PolicyNumber { get; set; }

    public Guid? PolicyTermId { get; set; }

    public Guid? PolicyTransactionId { get; set; }

    public Guid? ChargeId { get; set; }

    public Guid? BillingAccountId { get; set; }

    public Guid? InvoiceId { get; set; }

    public Guid? ReceiptId { get; set; }

    public Guid? ClaimId { get; set; }

    public Guid? ExposureId { get; set; }

    public Guid? ReserveLineId { get; set; }

    public string? CostType { get; set; }

    public string? CostCategory { get; set; }

    public Guid? ClaimPaymentId { get; set; }

    public Guid? DisbursementId { get; set; }

    public Guid? RefundId { get; set; }
}
