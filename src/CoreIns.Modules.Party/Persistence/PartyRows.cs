using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.Modules.Party.Persistence;

// Rows of the pty schema (PRD-01 §7.1). Every row carries legal_entity_id, jurisdiction-free audit columns and
// record_version where it is updated (REQ-PTY-035, REQ-PTY-043). Child rows are bitemporal (REQ-PTY-036): valid time
// [valid_from, valid_to) as dates and record time [recorded_from, recorded_to) as instants; a change closes the current
// row (recorded_to) and inserts its successor, so nothing is ever updated in place or deleted.

/// <summary><c>pty.party</c>: the party master (Person or Organisation, independent of role).</summary>
internal sealed class PartyRow
{
    public PartyId PartyId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public string Jurisdiction { get; set; } = string.Empty;

    public PartyNumber PartyNumber { get; set; }

    public string PartyType { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string PreferredLanguage { get; set; } = string.Empty;

    /// <summary>Birth date (P2), AES-GCM envelope bound to the party id (REQ-PTY-060, D-ARC-14).</summary>
    public byte[]? BirthDateEncrypted { get; set; }

    public string? SourceChannel { get; set; }

    public int RecordVersion { get; set; }

    public Instant CreatedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public Instant UpdatedAt { get; set; }
}

/// <summary>Valid and record time of a bitemporal child row.</summary>
internal abstract class BitemporalRow
{
    public PartyId PartyId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public BusinessDate ValidFrom { get; set; }

    public BusinessDate? ValidTo { get; set; }

    public Instant RecordedFrom { get; set; }

    public Instant? RecordedTo { get; set; }

    public string CreatedBy { get; set; } = string.Empty;
}

/// <summary><c>pty.party_name</c>: one name form (native, generated Latin, Latin as on ID document).</summary>
internal sealed class PartyNameRow : BitemporalRow
{
    public Guid NameId { get; set; }

    public string Form { get; set; } = string.Empty;

    public string Script { get; set; } = string.Empty;

    public string? GivenNames { get; set; }

    public string? FamilyName { get; set; }

    public string? FatherName { get; set; }

    public string? MotherName { get; set; }

    public string? OrganisationName { get; set; }

    public string? TradeName { get; set; }

    public string? TransliteratorVersion { get; set; }

    public string? SourceDocumentRef { get; set; }
}

/// <summary><c>pty.party_search_key</c>: normalised and cross-script keys of a name (REQ-PTY-065..067), trigram-indexed.</summary>
internal sealed class PartySearchKeyRow
{
    public long KeyId { get; set; }

    public PartyId PartyId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public Guid NameId { get; set; }

    /// <summary>FULL (whole name) or PART (one name part, incl. its transliteration variants).</summary>
    public string KeyKind { get; set; } = string.Empty;

    public string SearchKey { get; set; } = string.Empty;

    public string RuleVersion { get; set; } = string.Empty;

    public Instant RecordedFrom { get; set; }

    public Instant? RecordedTo { get; set; }
}

/// <summary><c>pty.party_identifier</c>: a typed identifier, value encrypted, keyed blind index (REQ-PTY-046, REQ-PTY-060).</summary>
internal sealed class PartyIdentifierRow : BitemporalRow
{
    public Guid IdentifierId { get; set; }

    public string Scheme { get; set; } = string.Empty;

    public byte[] ValueEncrypted { get; set; } = [];

    public string ValueBlindIndex { get; set; } = string.Empty;

    /// <summary>Last three characters, for masked display (REQ-PTY-044).</summary>
    public string DisplaySuffix { get; set; } = string.Empty;

    public string? IssuingCountry { get; set; }

    public string VerificationStatus { get; set; } = string.Empty;

    public string VerificationSource { get; set; } = string.Empty;

    public Instant? VerifiedAt { get; set; }

    public string ValidatorVersion { get; set; } = string.Empty;
}

/// <summary><c>pty.party_address</c>: a typed address with native and Latin forms (REQ-PTY-012, REQ-PTY-074, REQ-PTY-075).</summary>
internal sealed class PartyAddressRow : BitemporalRow
{
    public Guid AddressId { get; set; }

    public string[] Types { get; set; } = [];

    public bool IsPrimary { get; set; }

    public string Country { get; set; } = string.Empty;

    public string? Street { get; set; }

    public string? Number { get; set; }

    public string? Building { get; set; }

    public string? Floor { get; set; }

    public string? Unit { get; set; }

    public string? Postcode { get; set; }

    public string? Locality { get; set; }

    public string? Municipality { get; set; }

    public string? RegionalUnit { get; set; }

    public string? Region { get; set; }

    public string[] FreeLines { get; set; } = [];

    public string? LatinStreet { get; set; }

    public string? LatinBuilding { get; set; }

    public string? LatinLocality { get; set; }

    public string? LatinMunicipality { get; set; }

    public string? LatinRegionalUnit { get; set; }

    public string? LatinRegion { get; set; }

    public string[] LatinFreeLines { get; set; } = [];

    public string[] FormattedLines { get; set; } = [];

    public string[] FormattedLinesLatin { get; set; } = [];

    public string ValidationState { get; set; } = string.Empty;

    public string? RuleSetId { get; set; }

    public string? Description { get; set; }

    /// <summary>A detached copy (arrays shared, never mutated) for a new record version; the caller sets the id and record time.</summary>
    public PartyAddressRow Copy() => (PartyAddressRow)MemberwiseClone();
}

/// <summary><c>pty.party_contact_point</c>: phone, email, … (REQ-PTY-081..083).</summary>
internal sealed class PartyContactPointRow : BitemporalRow
{
    public Guid ContactPointId { get; set; }

    public string Type { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public string Purpose { get; set; } = string.Empty;

    public bool IsPrimary { get; set; }

    public string VerificationStatus { get; set; } = string.Empty;

    public string Source { get; set; } = string.Empty;

    /// <summary>A detached copy for a new record version; the caller sets the id and record time.</summary>
    public PartyContactPointRow Copy() => (PartyContactPointRow)MemberwiseClone();
}

/// <summary><c>pty.intermediary</c>: an intermediary record of a party (REQ-PTY-187, REQ-PTY-188).</summary>
internal sealed class IntermediaryRow
{
    public IntermediaryId IntermediaryId { get; set; }

    public PartyId PartyId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public string IntermediaryType { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string RegisterName { get; set; } = string.Empty;

    public string Chamber { get; set; } = string.Empty;

    public string RegisterNumber { get; set; } = string.Empty;

    public string RegistrationCategory { get; set; } = string.Empty;

    public BusinessDate RegistrationDate { get; set; }

    public string RegisterStatus { get; set; } = string.Empty;

    public string? VerificationLink { get; set; }

    public string? EvidenceRef { get; set; }

    public Instant RegisterVerifiedAt { get; set; }

    public BusinessDate ValidFrom { get; set; }

    public int RecordVersion { get; set; }

    public Instant CreatedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public Instant UpdatedAt { get; set; }

    /// <summary>Hierarchy parent (REQ-PTY-193).</summary>
    public IntermediaryId? ParentIntermediaryId { get; set; }
}

/// <summary><c>pty.intermediary_cpd</c>: CPD hours per calendar year (REQ-PTY-191).</summary>
internal sealed class IntermediaryCpdRow
{
    public IntermediaryId IntermediaryId { get; set; }

    public int Year { get; set; }

    public decimal Hours { get; set; }

    public decimal RequiredHours { get; set; }

    public Instant RecordedAt { get; set; }
}

/// <summary><c>pty.intermediary_appointment</c>: product appointment of an intermediary.</summary>
internal sealed class IntermediaryAppointmentRow
{
    public Guid AppointmentId { get; set; }

    public IntermediaryId IntermediaryId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public string ProductCode { get; set; } = string.Empty;

    public BusinessDate ValidFrom { get; set; }

    public BusinessDate? ValidTo { get; set; }
}

/// <summary><c>pty.intermediary_conflict_of_interest</c>: declared conflicts of interest.</summary>
internal sealed class IntermediaryConflictRow
{
    public Guid ConflictId { get; set; }

    public IntermediaryId IntermediaryId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public string Description { get; set; } = string.Empty;

    public BusinessDate DeclaredOn { get; set; }

    public string Status { get; set; } = string.Empty;
}

/// <summary><c>pty.producer_code</c>: a producer code with its authorities (REQ-PTY-204, REQ-PTY-205).</summary>
internal sealed class ProducerCodeRow
{
    public ProducerCodeId ProducerCodeId { get; set; }

    public IntermediaryId IntermediaryId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public string Code { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public bool CollectPremium { get; set; }

    public bool IssueCoverNotes { get; set; }

    public bool BindWithinAuthority { get; set; }

    public bool ServiceOnly { get; set; }

    public BusinessDate ValidFrom { get; set; }

    public BusinessDate? ValidTo { get; set; }

    public int RecordVersion { get; set; }

    public Instant CreatedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;
}

/// <summary><c>pty.dsar_restriction</c>: a record class of a data subject restricted by a DSAR (D-SL6-07); one row per (subject, class).</summary>
internal sealed class DsarRestrictionRow
{
    public Guid RestrictionId { get; set; }

    public PartyId PartyId { get; set; }

    public string RecordClass { get; set; } = string.Empty;

    public string LegalBasis { get; set; } = string.Empty;

    /// <summary>ISO date or UNSET_F404.</summary>
    public string RetentionEnd { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    public Instant CreatedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;
}

/// <summary>
/// <c>pty.vulnerability</c>: the vulnerable-customer flag of a party (REQ-PTY-011, NX-HD-02); one row per party. Removing the flag sets
/// <see cref="Flag"/> false (the app role has no DELETE, and the history of who flagged and cleared stays in the audit trail). The vulnerability
/// category is never stored here, only rule codes (P3 stays out).
/// </summary>
internal sealed class VulnerabilityRow
{
    public PartyId PartyId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public bool Flag { get; set; }

    /// <summary>Handling rule codes, comma separated (codes never contain a comma).</summary>
    public string RuleCodes { get; set; } = string.Empty;

    public Instant UpdatedAt { get; set; }

    public string UpdatedBy { get; set; } = string.Empty;
}

/// <summary>
/// <c>pty.party_role</c> (BO-PTY-03): a role a party holds, optionally within a context object. Bitemporal: the logical role is
/// <see cref="RoleId"/>; every change closes the current row (recorded_to) and inserts its successor under a new <see cref="RowId"/>.
/// </summary>
internal sealed class PartyRoleRow : BitemporalRow
{
    public Guid RowId { get; set; }

    public Guid RoleId { get; set; }

    public string RoleType { get; set; } = string.Empty;

    public string? ContextModule { get; set; }

    public string? ContextType { get; set; }

    public string? ContextId { get; set; }
}

/// <summary><c>pty.party_relationship</c> (BO-PTY-03): a directed, bitemporal link between two parties.</summary>
internal sealed class PartyRelationshipRow
{
    public Guid RowId { get; set; }

    public Guid RelationshipId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public PartyId FromPartyId { get; set; }

    public PartyId ToPartyId { get; set; }

    public string RelationshipType { get; set; } = string.Empty;

    public BusinessDate ValidFrom { get; set; }

    public BusinessDate? ValidTo { get; set; }

    public Instant RecordedFrom { get; set; }

    public Instant? RecordedTo { get; set; }

    public string CreatedBy { get; set; } = string.Empty;
}

/// <summary>
/// <c>pty.account</c> (BO-PTY-04, REQ-PTY-004): the customer account. The holder is the POLICYHOLDER role in <c>party_role</c> whose context is
/// this account (bitemporal, one at a time); <see cref="HolderPartyId"/> is the current holder kept here for reads and search.
/// </summary>
internal sealed class AccountRow
{
    public AccountId AccountId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public string AccountNumber { get; set; } = string.Empty;

    public string AccountType { get; set; } = string.Empty;

    public string? Name { get; set; }

    /// <summary>PENDING, ACTIVE or WITHDRAWN.</summary>
    public string Status { get; set; } = string.Empty;

    public PartyId HolderPartyId { get; set; }

    public BusinessDate? WithdrawnOn { get; set; }

    public string? WithdrawReason { get; set; }

    public int RecordVersion { get; set; }

    public Instant CreatedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public Instant UpdatedAt { get; set; }
}

/// <summary><c>pty.account_member</c> (BO-PTY-04): a party that belongs to an account; <see cref="RemovedAt"/> null = current member.</summary>
internal sealed class AccountMemberRow
{
    public Guid MemberId { get; set; }

    public AccountId AccountId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public PartyId PartyId { get; set; }

    public string MemberRole { get; set; } = string.Empty;

    public BusinessDate AddedOn { get; set; }

    public Instant AddedAt { get; set; }

    public Instant? RemovedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;
}
