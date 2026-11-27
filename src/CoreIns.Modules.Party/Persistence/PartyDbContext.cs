using CoreIns.Modules.Party.Domain;
using CoreIns.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CoreIns.Modules.Party.Persistence;

/// <summary>
/// The Party module's EF Core context (schema <c>pty</c>). It shares the scope's connection and transaction through
/// <see cref="DbSession"/> (registered with <c>AddModuleDbContext</c>), so party rows, outbox events and audit records
/// commit together. Writes go through this context; reads and searches use Dapper on the same connection
/// (<see cref="Queries.PartyReader"/>). Internal: no other module may use it (architecture rule).
/// </summary>
internal sealed class PartyDbContext(DbContextOptions<PartyDbContext> options) : ModuleDbContext(options)
{
    public DbSet<PartyRow> Parties => Set<PartyRow>();

    public DbSet<PartyNameRow> Names => Set<PartyNameRow>();

    public DbSet<PartySearchKeyRow> SearchKeys => Set<PartySearchKeyRow>();

    public DbSet<PartyIdentifierRow> Identifiers => Set<PartyIdentifierRow>();

    public DbSet<PartyAddressRow> Addresses => Set<PartyAddressRow>();

    public DbSet<PartyContactPointRow> ContactPoints => Set<PartyContactPointRow>();

    public DbSet<IntermediaryRow> Intermediaries => Set<IntermediaryRow>();

    public DbSet<IntermediaryCpdRow> IntermediaryCpd => Set<IntermediaryCpdRow>();

    public DbSet<IntermediaryAppointmentRow> IntermediaryAppointments => Set<IntermediaryAppointmentRow>();

    public DbSet<IntermediaryConflictRow> IntermediaryConflicts => Set<IntermediaryConflictRow>();

    public DbSet<ProducerCodeRow> ProducerCodes => Set<ProducerCodeRow>();

    public DbSet<DsarRestrictionRow> DsarRestrictions => Set<DsarRestrictionRow>();

    public DbSet<VulnerabilityRow> Vulnerabilities => Set<VulnerabilityRow>();

    public DbSet<PartyRoleRow> Roles => Set<PartyRoleRow>();

    public DbSet<PartyRelationshipRow> Relationships => Set<PartyRelationshipRow>();

    public DbSet<AccountRow> Accounts => Set<AccountRow>();

    public DbSet<AccountMemberRow> AccountMembers => Set<AccountMemberRow>();

    public DbSet<CommissionAgreementRow> CommissionAgreements => Set<CommissionAgreementRow>();

    public DbSet<CommissionAgreementVersionRow> CommissionVersions => Set<CommissionAgreementVersionRow>();

    public DbSet<CommissionRateLineRow> CommissionRateLines => Set<CommissionRateLineRow>();

    public DbSet<ProducerOfRecordRow> ProducerOfRecords => Set<ProducerOfRecordRow>();

    protected override string Schema => PartyModule.Schema;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<CommissionAgreementRow>(entity =>
        {
            entity.ToTable("commission_agreement");
            entity.HasKey(e => e.AgreementId).HasName("pk_commission_agreement");
            entity.Property(e => e.AgreementId).HasColumnName("agreement_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.IntermediaryId).HasColumnName("intermediary_id");
            entity.Property(e => e.ProductCode).HasColumnName("product_code");
            entity.Property(e => e.LineOfBusiness).HasColumnName("line_of_business");
            entity.Property(e => e.ScopeKey).HasColumnName("scope_key");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.HasIndex(e => new { e.LegalEntityId, e.IntermediaryId, e.ScopeKey }).IsUnique().HasDatabaseName("ux_commission_agreement_scope");
            entity.HasOne<IntermediaryRow>().WithMany().HasForeignKey(e => e.IntermediaryId).HasConstraintName("fk_commission_agreement_intermediary").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CommissionAgreementVersionRow>(entity =>
        {
            entity.ToTable("commission_agreement_version", table =>
            {
                table.HasCheckConstraint("ck_commission_version_status", "status IN ('DRAFT','PENDING_APPROVAL','ACTIVE','REJECTED')");
                table.HasCheckConstraint("ck_commission_version_rate", "max_rate_pct >= 0 AND max_rate_pct <= 100");
            });
            entity.HasKey(e => e.VersionId).HasName("pk_commission_agreement_version");
            entity.Property(e => e.VersionId).HasColumnName("version_id");
            entity.Property(e => e.AgreementId).HasColumnName("agreement_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.VersionNo).HasColumnName("version_no");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.EffectiveFrom).HasColumnName("effective_from");
            entity.Property(e => e.ExpectedVolume).HasColumnName("expected_volume").HasColumnType("numeric(18,2)");
            entity.Property(e => e.Currency).HasColumnName("currency");
            entity.Property(e => e.MaxRatePct).HasColumnName("max_rate_pct").HasColumnType("numeric(7,4)");
            entity.Property(e => e.ContentHash).HasColumnName("content_hash");
            entity.Property(e => e.ApprovalRequestId).HasColumnName("approval_request_id");
            entity.Property(e => e.Notes).HasColumnName("notes");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.SubmittedBy).HasColumnName("submitted_by");
            entity.Property(e => e.SubmittedAt).HasColumnName("submitted_at").HasColumnType("timestamptz");
            entity.Property(e => e.DecidedBy).HasColumnName("decided_by");
            entity.Property(e => e.DecidedAt).HasColumnName("decided_at").HasColumnType("timestamptz");
            entity.Property(e => e.DecisionComment).HasColumnName("decision_comment");
            entity.HasIndex(e => new { e.AgreementId, e.VersionNo }).IsUnique().HasDatabaseName("ux_commission_version_no");
            entity.HasIndex(e => e.AgreementId).IsUnique().HasFilter("status IN ('DRAFT','PENDING_APPROVAL')").HasDatabaseName("ux_commission_version_open");
            entity.HasOne<CommissionAgreementRow>().WithMany().HasForeignKey(e => e.AgreementId).HasConstraintName("fk_commission_version_agreement").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CommissionRateLineRow>(entity =>
        {
            entity.ToTable("commission_rate_line", table =>
            {
                table.HasCheckConstraint("ck_commission_line_rate", "rate_pct >= 0 AND rate_pct <= 100");
            });
            entity.HasKey(e => e.LineId).HasName("pk_commission_rate_line");
            entity.Property(e => e.LineId).HasColumnName("line_id");
            entity.Property(e => e.VersionId).HasColumnName("version_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.LineNo).HasColumnName("line_no");
            entity.Property(e => e.BusinessType).HasColumnName("business_type");
            entity.Property(e => e.LineOfBusiness).HasColumnName("line_of_business");
            entity.Property(e => e.Basis).HasColumnName("basis");
            entity.Property(e => e.RatePct).HasColumnName("rate_pct").HasColumnType("numeric(7,4)");
            entity.HasIndex(e => new { e.VersionId, e.LineNo }).IsUnique().HasDatabaseName("ux_commission_line_no");
            entity.HasOne<CommissionAgreementVersionRow>().WithMany().HasForeignKey(e => e.VersionId).HasConstraintName("fk_commission_line_version").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ProducerOfRecordRow>(entity =>
        {
            entity.ToTable("producer_of_record", table =>
            {
                BitemporalChecks(table, "producer_of_record");
                table.HasCheckConstraint("ck_producer_of_record_consequence", "commission_consequence IN ('NEW_PRODUCER_FROM_DATE','OLD_PRODUCER_KEEPS_EARNED','OLD_PRODUCER_KEEPS_ALL')");
                table.HasCheckConstraint("ck_producer_of_record_split", "split_share IS NULL OR (split_share > 0 AND split_share <= 100)");
            });
            entity.HasKey(e => e.PorId).HasName("pk_producer_of_record");
            entity.Property(e => e.PorId).HasColumnName("por_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.PolicyId).HasColumnName("policy_id");
            entity.Property(e => e.PolicyTermId).HasColumnName("policy_term_id");
            entity.Property(e => e.IntermediaryId).HasColumnName("intermediary_id");
            entity.Property(e => e.ProducerCode).HasColumnName("producer_code");
            entity.Property(e => e.ValidFrom).HasColumnName("valid_from");
            entity.Property(e => e.ValidTo).HasColumnName("valid_to");
            entity.Property(e => e.Reason).HasColumnName("reason");
            entity.Property(e => e.Note).HasColumnName("note");
            entity.Property(e => e.CommissionConsequence).HasColumnName("commission_consequence");
            entity.Property(e => e.SplitShare).HasColumnName("split_share").HasColumnType("numeric(7,4)");
            entity.Property(e => e.LetterReference).HasColumnName("letter_reference");
            entity.Property(e => e.TransferId).HasColumnName("transfer_id");
            entity.Property(e => e.RecordedFrom).HasColumnName("recorded_from").HasColumnType("timestamptz");
            entity.Property(e => e.RecordedTo).HasColumnName("recorded_to").HasColumnType("timestamptz");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.HasIndex(e => new { e.PolicyId, e.ValidFrom }).IsUnique().HasFilter("recorded_to IS NULL").HasDatabaseName("ux_producer_of_record_current_start");
            entity.HasIndex(e => new { e.IntermediaryId, e.ValidFrom }).HasDatabaseName("ix_producer_of_record_intermediary");
            entity.HasOne<IntermediaryRow>().WithMany().HasForeignKey(e => e.IntermediaryId).HasConstraintName("fk_producer_of_record_intermediary").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AccountRow>(entity =>
        {
            entity.ToTable("account", table =>
            {
                table.HasCheckConstraint("ck_account_status", "status IN ('PENDING','ACTIVE','WITHDRAWN')");
                table.HasCheckConstraint("ck_account_type", "account_type IN ('INDIVIDUAL','HOUSEHOLD','BUSINESS')");
            });
            entity.HasKey(e => e.AccountId).HasName("pk_account");
            entity.Property(e => e.AccountId).HasColumnName("account_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.AccountNumber).HasColumnName("account_number");
            entity.Property(e => e.AccountType).HasColumnName("account_type");
            entity.Property(e => e.Name).HasColumnName("name");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.HolderPartyId).HasColumnName("holder_party_id");
            entity.Property(e => e.WithdrawnOn).HasColumnName("withdrawn_on");
            entity.Property(e => e.WithdrawReason).HasColumnName("withdraw_reason");
            entity.Property(e => e.RecordVersion).HasColumnName("record_version");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            entity.HasIndex(e => new { e.LegalEntityId, e.AccountNumber }).IsUnique().HasDatabaseName("ux_account_number");
            entity.HasIndex(e => e.HolderPartyId).HasDatabaseName("ix_account_holder");
            entity.HasOne<PartyRow>().WithMany().HasForeignKey(e => e.HolderPartyId).HasConstraintName("fk_account_holder").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AccountMemberRow>(entity =>
        {
            entity.ToTable("account_member");
            entity.HasKey(e => e.MemberId).HasName("pk_account_member");
            entity.Property(e => e.MemberId).HasColumnName("member_id");
            entity.Property(e => e.AccountId).HasColumnName("account_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.PartyId).HasColumnName("party_id");
            entity.Property(e => e.MemberRole).HasColumnName("member_role");
            entity.Property(e => e.AddedOn).HasColumnName("added_on");
            entity.Property(e => e.AddedAt).HasColumnName("added_at").HasColumnType("timestamptz");
            entity.Property(e => e.RemovedAt).HasColumnName("removed_at").HasColumnType("timestamptz");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.HasIndex(e => e.AccountId).HasDatabaseName("ix_account_member_account");
            entity.HasIndex(e => e.PartyId).HasDatabaseName("ix_account_member_party");
            entity.HasIndex(e => new { e.AccountId, e.PartyId }).IsUnique().HasFilter("removed_at IS NULL").HasDatabaseName("ux_account_member_current");
            entity.HasOne<AccountRow>().WithMany().HasForeignKey(e => e.AccountId).HasConstraintName("fk_account_member_account").OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PartyRow>().WithMany().HasForeignKey(e => e.PartyId).HasConstraintName("fk_account_member_party").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PartyRoleRow>(entity =>
        {
            entity.ToTable("party_role", table => BitemporalChecks(table, "party_role"));
            entity.HasKey(e => e.RowId).HasName("pk_party_role");
            entity.Property(e => e.RowId).HasColumnName("row_id");
            entity.Property(e => e.RoleId).HasColumnName("role_id");
            MapBitemporal(entity);
            entity.Property(e => e.RoleType).HasColumnName("role_type");
            entity.Property(e => e.ContextModule).HasColumnName("context_module");
            entity.Property(e => e.ContextType).HasColumnName("context_type");
            entity.Property(e => e.ContextId).HasColumnName("context_id");
            entity.HasIndex(e => new { e.PartyId, e.RoleType }).HasDatabaseName("ix_party_role_party");
            entity.HasIndex(e => e.RoleId).HasDatabaseName("ix_party_role_role");
            entity.HasOne<PartyRow>().WithMany().HasForeignKey(e => e.PartyId).HasConstraintName("fk_party_role_party").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PartyRelationshipRow>(entity =>
        {
            entity.ToTable("party_relationship", table =>
            {
                BitemporalChecks(table, "party_relationship");
                table.HasCheckConstraint("ck_party_relationship_distinct", "from_party_id <> to_party_id");
            });
            entity.HasKey(e => e.RowId).HasName("pk_party_relationship");
            entity.Property(e => e.RowId).HasColumnName("row_id");
            entity.Property(e => e.RelationshipId).HasColumnName("relationship_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.FromPartyId).HasColumnName("from_party_id");
            entity.Property(e => e.ToPartyId).HasColumnName("to_party_id");
            entity.Property(e => e.RelationshipType).HasColumnName("relationship_type");
            entity.Property(e => e.ValidFrom).HasColumnName("valid_from");
            entity.Property(e => e.ValidTo).HasColumnName("valid_to");
            entity.Property(e => e.RecordedFrom).HasColumnName("recorded_from").HasColumnType("timestamptz");
            entity.Property(e => e.RecordedTo).HasColumnName("recorded_to").HasColumnType("timestamptz");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.HasIndex(e => e.FromPartyId).HasDatabaseName("ix_party_relationship_from");
            entity.HasIndex(e => e.ToPartyId).HasDatabaseName("ix_party_relationship_to");
            entity.HasIndex(e => e.RelationshipId).HasDatabaseName("ix_party_relationship_rel");
            entity.HasOne<PartyRow>().WithMany().HasForeignKey(e => e.FromPartyId).HasConstraintName("fk_party_relationship_from").OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PartyRow>().WithMany().HasForeignKey(e => e.ToPartyId).HasConstraintName("fk_party_relationship_to").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<VulnerabilityRow>(entity =>
        {
            entity.ToTable("vulnerability");
            entity.HasKey(e => e.PartyId).HasName("pk_vulnerability");
            entity.Property(e => e.PartyId).HasColumnName("party_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.Flag).HasColumnName("flag");
            entity.Property(e => e.RuleCodes).HasColumnName("rule_codes");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<DsarRestrictionRow>(entity =>
        {
            entity.ToTable("dsar_restriction");
            entity.HasKey(e => e.RestrictionId).HasName("pk_dsar_restriction");
            entity.Property(e => e.RestrictionId).HasColumnName("restriction_id");
            entity.Property(e => e.PartyId).HasColumnName("party_id");
            entity.Property(e => e.RecordClass).HasColumnName("record_class");
            entity.Property(e => e.LegalBasis).HasColumnName("legal_basis");
            entity.Property(e => e.RetentionEnd).HasColumnName("retention_end");
            entity.Property(e => e.Reason).HasColumnName("reason");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.HasIndex(e => new { e.PartyId, e.RecordClass }).IsUnique().HasDatabaseName("ux_dsar_restriction_subject_class");
        });

        modelBuilder.Entity<PartyRow>(entity =>
        {
            entity.ToTable("party", table =>
            {
                table.HasCheckConstraint("ck_party_type", "party_type IN ('PERSON', 'ORGANISATION')");
                table.HasCheckConstraint("ck_party_status", Codes.CheckSql<PartyStatus>("status"));
                table.HasCheckConstraint("ck_party_jurisdiction", "jurisdiction ~ '^[A-Z]{2}$'");
                table.HasCheckConstraint("ck_party_record_version", "record_version >= 1");
            });
            entity.HasKey(e => e.PartyId).HasName("pk_party");
            entity.Property(e => e.PartyId).HasColumnName("party_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.Jurisdiction).HasColumnName("jurisdiction").HasColumnType("char(2)");
            entity.Property(e => e.PartyNumber).HasColumnName("party_number");
            entity.Property(e => e.PartyType).HasColumnName("party_type");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.PreferredLanguage).HasColumnName("preferred_language");
            entity.Property(e => e.BirthDateEncrypted).HasColumnName("birth_date_encrypted");
            entity.Property(e => e.SourceChannel).HasColumnName("source_channel");
            entity.Property(e => e.RecordVersion).HasColumnName("record_version").IsConcurrencyToken();
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            entity.HasIndex(e => new { e.LegalEntityId, e.PartyNumber }).IsUnique().HasDatabaseName("ux_party_number");
        });

        modelBuilder.Entity<PartyNameRow>(entity =>
        {
            entity.ToTable("party_name", table =>
            {
                table.HasCheckConstraint("ck_party_name_form", "form IN ('NATIVE', 'LATIN_GENERATED', 'LATIN_AS_ON_DOCUMENT')");
                BitemporalChecks(table, "party_name");
            });
            entity.HasKey(e => e.NameId).HasName("pk_party_name");
            entity.Property(e => e.NameId).HasColumnName("name_id");
            MapBitemporal(entity);
            entity.Property(e => e.Form).HasColumnName("form");
            entity.Property(e => e.Script).HasColumnName("script");
            entity.Property(e => e.GivenNames).HasColumnName("given_names");
            entity.Property(e => e.FamilyName).HasColumnName("family_name");
            entity.Property(e => e.FatherName).HasColumnName("father_name");
            entity.Property(e => e.MotherName).HasColumnName("mother_name");
            entity.Property(e => e.OrganisationName).HasColumnName("organisation_name");
            entity.Property(e => e.TradeName).HasColumnName("trade_name");
            entity.Property(e => e.TransliteratorVersion).HasColumnName("transliterator_version");
            entity.Property(e => e.SourceDocumentRef).HasColumnName("source_document_ref");
            entity.HasIndex(e => e.PartyId).HasDatabaseName("ix_party_name_party");
            entity.HasOne<PartyRow>().WithMany().HasForeignKey(e => e.PartyId).HasConstraintName("fk_party_name_party").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PartySearchKeyRow>(entity =>
        {
            entity.ToTable("party_search_key", table => table.HasCheckConstraint("ck_party_search_key_kind", "key_kind IN ('FULL', 'PART')"));
            entity.HasKey(e => e.KeyId).HasName("pk_party_search_key");
            entity.Property(e => e.KeyId).HasColumnName("key_id").UseIdentityAlwaysColumn();
            entity.Property(e => e.PartyId).HasColumnName("party_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.NameId).HasColumnName("name_id");
            entity.Property(e => e.KeyKind).HasColumnName("key_kind");
            entity.Property(e => e.SearchKey).HasColumnName("search_key");
            entity.Property(e => e.RuleVersion).HasColumnName("rule_version");
            entity.Property(e => e.RecordedFrom).HasColumnName("recorded_from").HasColumnType("timestamptz");
            entity.Property(e => e.RecordedTo).HasColumnName("recorded_to").HasColumnType("timestamptz");

            // Trigram index for fuzzy and word-similarity matches; text_pattern_ops for exact and prefix (REQ-PTY-066).
            entity.HasIndex(e => e.SearchKey).HasMethod("gin").HasOperators("gin_trgm_ops").HasDatabaseName("ix_party_search_key_trgm");
            entity.HasIndex(e => new { e.LegalEntityId, e.SearchKey }).HasOperators("uuid_ops", "text_pattern_ops")
                .HasFilter("recorded_to IS NULL").HasDatabaseName("ix_party_search_key_prefix");
            entity.HasIndex(e => e.PartyId).HasDatabaseName("ix_party_search_key_party");
            entity.HasOne<PartyRow>().WithMany().HasForeignKey(e => e.PartyId).HasConstraintName("fk_party_search_key_party").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PartyIdentifierRow>(entity =>
        {
            entity.ToTable("party_identifier", table =>
            {
                table.HasCheckConstraint(
                    "ck_party_identifier_verification",
                    "verification_status IN ('SELF_DECLARED', 'DOCUMENT_VERIFIED', 'REGISTRY_VERIFIED', 'VERIFICATION_FAILED', 'EXPIRED')");
                table.HasCheckConstraint("ck_party_identifier_blind_index", "value_blind_index ~ '^v[0-9]+:'");
                BitemporalChecks(table, "party_identifier");
            });
            entity.HasKey(e => e.IdentifierId).HasName("pk_party_identifier");
            entity.Property(e => e.IdentifierId).HasColumnName("identifier_id");
            MapBitemporal(entity);
            entity.Property(e => e.Scheme).HasColumnName("scheme");
            entity.Property(e => e.ValueEncrypted).HasColumnName("value_encrypted");
            entity.Property(e => e.ValueBlindIndex).HasColumnName("value_blind_index");
            entity.Property(e => e.DisplaySuffix).HasColumnName("display_suffix");
            entity.Property(e => e.IssuingCountry).HasColumnName("issuing_country").HasColumnType("char(2)");
            entity.Property(e => e.VerificationStatus).HasColumnName("verification_status");
            entity.Property(e => e.VerificationSource).HasColumnName("verification_source");
            entity.Property(e => e.VerifiedAt).HasColumnName("verified_at").HasColumnType("timestamptz");
            entity.Property(e => e.ValidatorVersion).HasColumnName("validator_version");
            entity.HasIndex(e => e.PartyId).HasDatabaseName("ix_party_identifier_party");

            // REQ-PTY-052: one current holder per (legal entity, scheme, value). Values written under different blind-index
            // key versions are caught by the handler's candidate search (BlindIndexer.SearchCandidatesAsync).
            entity.HasIndex(e => new { e.LegalEntityId, e.Scheme, e.ValueBlindIndex }).IsUnique()
                .HasFilter("valid_to IS NULL AND recorded_to IS NULL").HasDatabaseName("ux_party_identifier_current");
            entity.HasOne<PartyRow>().WithMany().HasForeignKey(e => e.PartyId).HasConstraintName("fk_party_identifier_party").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PartyAddressRow>(entity =>
        {
            entity.ToTable("party_address", table =>
            {
                table.HasCheckConstraint("ck_party_address_state", "validation_state IN ('VALIDATED', 'UNVALIDATED', 'INVALID')");
                table.HasCheckConstraint("ck_party_address_types", "cardinality(types) >= 1");
                BitemporalChecks(table, "party_address");
            });
            entity.HasKey(e => e.AddressId).HasName("pk_party_address");
            entity.Property(e => e.AddressId).HasColumnName("address_id");
            MapBitemporal(entity);
            entity.Property(e => e.Types).HasColumnName("types");
            entity.Property(e => e.IsPrimary).HasColumnName("is_primary");
            entity.Property(e => e.Country).HasColumnName("country").HasColumnType("char(2)");
            entity.Property(e => e.Street).HasColumnName("street");
            entity.Property(e => e.Number).HasColumnName("number");
            entity.Property(e => e.Building).HasColumnName("building");
            entity.Property(e => e.Floor).HasColumnName("floor");
            entity.Property(e => e.Unit).HasColumnName("unit");
            entity.Property(e => e.Postcode).HasColumnName("postcode");
            entity.Property(e => e.Locality).HasColumnName("locality");
            entity.Property(e => e.Municipality).HasColumnName("municipality");
            entity.Property(e => e.RegionalUnit).HasColumnName("regional_unit");
            entity.Property(e => e.Region).HasColumnName("region");
            entity.Property(e => e.FreeLines).HasColumnName("free_lines");
            entity.Property(e => e.LatinStreet).HasColumnName("latin_street");
            entity.Property(e => e.LatinBuilding).HasColumnName("latin_building");
            entity.Property(e => e.LatinLocality).HasColumnName("latin_locality");
            entity.Property(e => e.LatinMunicipality).HasColumnName("latin_municipality");
            entity.Property(e => e.LatinRegionalUnit).HasColumnName("latin_regional_unit");
            entity.Property(e => e.LatinRegion).HasColumnName("latin_region");
            entity.Property(e => e.LatinFreeLines).HasColumnName("latin_free_lines");
            entity.Property(e => e.FormattedLines).HasColumnName("formatted_lines");
            entity.Property(e => e.FormattedLinesLatin).HasColumnName("formatted_lines_latin");
            entity.Property(e => e.ValidationState).HasColumnName("validation_state");
            entity.Property(e => e.RuleSetId).HasColumnName("rule_set_id");
            entity.Property(e => e.Description).HasColumnName("description");
            entity.HasIndex(e => e.PartyId).HasDatabaseName("ix_party_address_party");
            entity.HasIndex(e => new { e.LegalEntityId, e.Postcode }).HasDatabaseName("ix_party_address_postcode");
            entity.HasOne<PartyRow>().WithMany().HasForeignKey(e => e.PartyId).HasConstraintName("fk_party_address_party").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PartyContactPointRow>(entity =>
        {
            entity.ToTable("party_contact_point", table =>
            {
                table.HasCheckConstraint("ck_party_contact_point_type", "type IN ('MOBILE', 'LANDLINE', 'WORK_PHONE', 'FAX', 'EMAIL', 'SECURE_INBOX')");
                table.HasCheckConstraint("ck_party_contact_point_purpose", "purpose IN ('PERSONAL', 'WORK')");
                table.HasCheckConstraint("ck_party_contact_point_verification", "verification_status IN ('UNVERIFIED', 'VERIFIED', 'BOUNCING')");
                BitemporalChecks(table, "party_contact_point");
            });
            entity.HasKey(e => e.ContactPointId).HasName("pk_party_contact_point");
            entity.Property(e => e.ContactPointId).HasColumnName("contact_point_id");
            MapBitemporal(entity);
            entity.Property(e => e.Type).HasColumnName("type");
            entity.Property(e => e.Value).HasColumnName("value");
            entity.Property(e => e.Purpose).HasColumnName("purpose");
            entity.Property(e => e.IsPrimary).HasColumnName("is_primary");
            entity.Property(e => e.VerificationStatus).HasColumnName("verification_status");
            entity.Property(e => e.Source).HasColumnName("source");
            entity.HasIndex(e => e.PartyId).HasDatabaseName("ix_party_contact_point_party");
            entity.HasOne<PartyRow>().WithMany().HasForeignKey(e => e.PartyId).HasConstraintName("fk_party_contact_point_party").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<IntermediaryRow>(entity =>
        {
            entity.ToTable("intermediary", table =>
            {
                table.HasCheckConstraint("ck_intermediary_status", Codes.CheckSql<IntermediaryStatus>("status"));
                table.HasCheckConstraint("ck_intermediary_register_status", "register_status IN ('ACTIVE', 'SUSPENDED', 'DELETED')");
                table.HasCheckConstraint("ck_intermediary_record_version", "record_version >= 1");
            });
            entity.HasKey(e => e.IntermediaryId).HasName("pk_intermediary");
            entity.Property(e => e.IntermediaryId).HasColumnName("intermediary_id");
            entity.Property(e => e.PartyId).HasColumnName("party_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.IntermediaryType).HasColumnName("intermediary_type");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.RegisterName).HasColumnName("register_name");
            entity.Property(e => e.Chamber).HasColumnName("chamber");
            entity.Property(e => e.RegisterNumber).HasColumnName("register_number");
            entity.Property(e => e.RegistrationCategory).HasColumnName("registration_category");
            entity.Property(e => e.RegistrationDate).HasColumnName("registration_date");
            entity.Property(e => e.RegisterStatus).HasColumnName("register_status");
            entity.Property(e => e.VerificationLink).HasColumnName("verification_link");
            entity.Property(e => e.EvidenceRef).HasColumnName("evidence_ref");
            entity.Property(e => e.RegisterVerifiedAt).HasColumnName("register_verified_at").HasColumnType("timestamptz");
            entity.Property(e => e.ValidFrom).HasColumnName("valid_from");
            entity.Property(e => e.RecordVersion).HasColumnName("record_version").IsConcurrencyToken();
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            entity.Property(e => e.ParentIntermediaryId).HasColumnName("parent_intermediary_id");
            entity.HasIndex(e => new { e.LegalEntityId, e.PartyId }).IsUnique().HasDatabaseName("ux_intermediary_party");
            entity.HasIndex(e => new { e.LegalEntityId, e.RegisterNumber }).HasDatabaseName("ix_intermediary_register_number");
            entity.HasOne<PartyRow>().WithMany().HasForeignKey(e => e.PartyId).HasConstraintName("fk_intermediary_party").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<IntermediaryCpdRow>(entity =>
        {
            entity.ToTable("intermediary_cpd");
            entity.HasKey(e => new { e.IntermediaryId, e.Year }).HasName("pk_intermediary_cpd");
            entity.Property(e => e.IntermediaryId).HasColumnName("intermediary_id");
            entity.Property(e => e.Year).HasColumnName("year");
            entity.Property(e => e.Hours).HasColumnName("hours").HasColumnType("numeric(6,2)");
            entity.Property(e => e.RequiredHours).HasColumnName("required_hours").HasColumnType("numeric(6,2)");
            entity.Property(e => e.RecordedAt).HasColumnName("recorded_at").HasColumnType("timestamptz");
            entity.HasOne<IntermediaryRow>().WithMany().HasForeignKey(e => e.IntermediaryId)
                .HasConstraintName("fk_intermediary_cpd_intermediary").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<IntermediaryAppointmentRow>(entity =>
        {
            entity.ToTable("intermediary_appointment");
            entity.HasKey(e => e.AppointmentId).HasName("pk_intermediary_appointment");
            entity.Property(e => e.AppointmentId).HasColumnName("appointment_id");
            entity.Property(e => e.IntermediaryId).HasColumnName("intermediary_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.ProductCode).HasColumnName("product_code");
            entity.Property(e => e.ValidFrom).HasColumnName("valid_from");
            entity.Property(e => e.ValidTo).HasColumnName("valid_to");
            entity.HasIndex(e => e.IntermediaryId).HasDatabaseName("ix_intermediary_appointment_intermediary");
            entity.HasOne<IntermediaryRow>().WithMany().HasForeignKey(e => e.IntermediaryId)
                .HasConstraintName("fk_intermediary_appointment_intermediary").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<IntermediaryConflictRow>(entity =>
        {
            entity.ToTable("intermediary_conflict_of_interest");
            entity.HasKey(e => e.ConflictId).HasName("pk_intermediary_conflict_of_interest");
            entity.Property(e => e.ConflictId).HasColumnName("conflict_id");
            entity.Property(e => e.IntermediaryId).HasColumnName("intermediary_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.Description).HasColumnName("description");
            entity.Property(e => e.DeclaredOn).HasColumnName("declared_on");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.HasIndex(e => e.IntermediaryId).HasDatabaseName("ix_intermediary_coi_intermediary");
            entity.HasOne<IntermediaryRow>().WithMany().HasForeignKey(e => e.IntermediaryId)
                .HasConstraintName("fk_intermediary_coi_intermediary").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ProducerCodeRow>(entity =>
        {
            entity.ToTable("producer_code", table =>
            {
                table.HasCheckConstraint("ck_producer_code_status", Codes.CheckSql<ProducerCodeStatus>("status"));
                table.HasCheckConstraint("ck_producer_code_validity", "valid_to IS NULL OR valid_to > valid_from");
            });
            entity.HasKey(e => e.ProducerCodeId).HasName("pk_producer_code");
            entity.Property(e => e.ProducerCodeId).HasColumnName("producer_code_id");
            entity.Property(e => e.IntermediaryId).HasColumnName("intermediary_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.Code).HasColumnName("code");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.CollectPremium).HasColumnName("collect_premium");
            entity.Property(e => e.IssueCoverNotes).HasColumnName("issue_cover_notes");
            entity.Property(e => e.BindWithinAuthority).HasColumnName("bind_within_authority");
            entity.Property(e => e.ServiceOnly).HasColumnName("service_only");
            entity.Property(e => e.ValidFrom).HasColumnName("valid_from");
            entity.Property(e => e.ValidTo).HasColumnName("valid_to");
            entity.Property(e => e.RecordVersion).HasColumnName("record_version").IsConcurrencyToken();
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.HasIndex(e => new { e.LegalEntityId, e.Code }).IsUnique().HasDatabaseName("ux_producer_code_code");
            entity.HasOne<IntermediaryRow>().WithMany().HasForeignKey(e => e.IntermediaryId)
                .HasConstraintName("fk_producer_code_intermediary").OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void BitemporalChecks(TableBuilder table, string name)
    {
        table.HasCheckConstraint($"ck_{name}_valid", "valid_to IS NULL OR valid_to > valid_from");
        table.HasCheckConstraint($"ck_{name}_recorded", "recorded_to IS NULL OR recorded_to > recorded_from");
    }

    private static void MapBitemporal<T>(EntityTypeBuilder<T> entity)
        where T : BitemporalRow
    {
        entity.Property(e => e.PartyId).HasColumnName("party_id");
        entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
        entity.Property(e => e.ValidFrom).HasColumnName("valid_from");
        entity.Property(e => e.ValidTo).HasColumnName("valid_to");
        entity.Property(e => e.RecordedFrom).HasColumnName("recorded_from").HasColumnType("timestamptz");
        entity.Property(e => e.RecordedTo).HasColumnName("recorded_to").HasColumnType("timestamptz");
        entity.Property(e => e.CreatedBy).HasColumnName("created_by");
    }
}

/// <summary>Design-time factory for <c>dotnet ef migrations add … --project src/CoreIns.Modules.Party</c> (no connection opened).</summary>
internal sealed class PartyDbContextDesignTimeFactory : IDesignTimeDbContextFactory<PartyDbContext>
{
    public PartyDbContext CreateDbContext(string[] args) =>
        new(ModuleDbContextRegistration.MigrationOptions<PartyDbContext>("Host=localhost;Database=coreins_design;Username=design", PartyModule.Schema));
}
