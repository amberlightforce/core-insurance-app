using CoreIns.Modules.Channels.Ai;
using CoreIns.Modules.Channels.Domain;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CoreIns.Modules.Channels.Persistence;

/// <summary>The Channels module's EF Core context (schema <c>chn</c>). SL6-CHN-WITHDRAW owns the first migration.</summary>
internal sealed class ChannelsDbContext(DbContextOptions<ChannelsDbContext> options) : ModuleDbContext(options)
{
    public DbSet<WithdrawalRequestRow> WithdrawalRequests => Set<WithdrawalRequestRow>();

    public DbSet<TicketRow> Tickets => Set<TicketRow>();

    public DbSet<PermissionMatrixRow> PermissionMatrices => Set<PermissionMatrixRow>();

    public DbSet<PermissionMatrixEntryRow> PermissionMatrixEntries => Set<PermissionMatrixEntryRow>();

    public DbSet<PortalIdentityRow> PortalIdentities => Set<PortalIdentityRow>();

    protected override string Schema => ChannelsModule.Schema;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.MapDsarRestrictions();

        modelBuilder.Entity<WithdrawalRequestRow>(entity =>
        {
            entity.ToTable("withdrawal_request", table =>
            {
                var states = string.Join(", ", WithdrawalStatuses.All.Select(s => $"'{s}'"));
                table.HasCheckConstraint("ck_withdrawal_request_status", $"status IN ({states})");
                table.HasCheckConstraint("ck_withdrawal_request_confirmed", "status IN ('DECLARED', 'DECLINED') OR confirmed_at IS NOT NULL");
                table.HasCheckConstraint("ck_withdrawal_request_record_version", "record_version >= 1");
            });
            entity.HasKey(e => e.WithdrawalRequestId).HasName("pk_withdrawal_request");
            entity.Property(e => e.WithdrawalRequestId).HasColumnName("withdrawal_request_id");
            entity.Property(e => e.RequestNumber).HasColumnName("request_no");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.Jurisdiction).HasColumnName("jurisdiction").HasColumnType("char(2)");
            entity.Property(e => e.PolicyId).HasColumnName("policy_id");
            entity.Property(e => e.DeclarantName).HasColumnName("declarant_name");
            entity.Property(e => e.DeclarantEmail).HasColumnName("declarant_email");
            entity.Property(e => e.Reason).HasColumnName("reason");
            entity.Property(e => e.Channel).HasColumnName("channel");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.DeclaredAt).HasColumnName("declared_at").HasColumnType("timestamptz");
            entity.Property(e => e.DeclaredBy).HasColumnName("declared_by");
            entity.Property(e => e.ConcludedAt).HasColumnName("concluded_at").HasColumnType("timestamptz");
            entity.Property(e => e.WindowDays).HasColumnName("window_days");
            entity.Property(e => e.DeadlineAt).HasColumnName("deadline_at").HasColumnType("timestamptz");
            entity.Property(e => e.TokenHash).HasColumnName("token_hash");
            entity.Property(e => e.TokenExpiresAt).HasColumnName("token_expires_at").HasColumnType("timestamptz");
            entity.Property(e => e.ConfirmedAt).HasColumnName("confirmed_at").HasColumnType("timestamptz");
            entity.Property(e => e.DeclineCode).HasColumnName("decline_code");
            entity.Property(e => e.ChannelRef).HasColumnName("channel_ref");
            entity.Property(e => e.PolSubmittedAt).HasColumnName("pol_submitted_at").HasColumnType("timestamptz");
            entity.Property(e => e.ClosedAt).HasColumnName("closed_at").HasColumnType("timestamptz");
            entity.Property(e => e.RecordVersion).HasColumnName("record_version").IsConcurrencyToken();

            entity.HasIndex(e => e.RequestNumber).IsUnique().HasDatabaseName("ux_withdrawal_request_no");
            entity.HasIndex(e => e.ChannelRef).IsUnique().HasDatabaseName("ux_withdrawal_request_channel_ref");
            entity.HasIndex(e => new { e.LegalEntityId, e.PolicyId }).HasDatabaseName("ix_withdrawal_request_policy");
        });

        modelBuilder.Entity<TicketRow>(entity =>
        {
            entity.ToTable("ticket", table =>
            {
                var states = string.Join(", ", TicketStatuses.All.Select(s => $"'{s}'"));
                table.HasCheckConstraint("ck_ticket_status", $"status IN ({states})");
            });
            entity.HasKey(e => e.TicketId).HasName("pk_ticket");
            entity.Property(e => e.TicketId).HasColumnName("ticket_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.Tool).HasColumnName("tool");
            entity.Property(e => e.Arguments).HasColumnName("arguments").HasColumnType("jsonb");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.InteractionId).HasColumnName("interaction_id");
            entity.HasIndex(e => e.InteractionId).HasDatabaseName("ix_ticket_interaction");
            entity.Property(e => e.RequestedBy).HasColumnName("requested_by");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(e => e.ExpiresAt).HasColumnName("expires_at").HasColumnType("timestamptz");
            entity.Property(e => e.DecidedAt).HasColumnName("decided_at").HasColumnType("timestamptz");
            entity.Property(e => e.DecidedBy).HasColumnName("decided_by");
            entity.Property(e => e.ExecutedCommandRef).HasColumnName("executed_command_ref");
            entity.HasIndex(e => new { e.LegalEntityId, e.Status }).HasDatabaseName("ix_ticket_status");
        });

        modelBuilder.Entity<PortalIdentityRow>(entity =>
        {
            entity.ToTable("portal_identity", table =>
            {
                var states = string.Join(", ", PortalIdentityStatuses.All.Select(s => $"'{s}'"));
                table.HasCheckConstraint("ck_portal_identity_status", $"status IN ({states})");
                table.HasCheckConstraint("ck_portal_identity_unlinked", "status = 'LINKED' OR (unlinked_at IS NOT NULL AND unlinked_by IS NOT NULL)");
            });
            entity.HasKey(e => e.PortalIdentityId).HasName("pk_portal_identity");
            entity.Property(e => e.PortalIdentityId).HasColumnName("portal_identity_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.Subject).HasColumnName("subject");
            entity.Property(e => e.PartyId).HasColumnName("party_id");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.Verification).HasColumnName("verification");
            entity.Property(e => e.LinkedAt).HasColumnName("linked_at").HasColumnType("timestamptz");
            entity.Property(e => e.LinkedBy).HasColumnName("linked_by");
            entity.Property(e => e.UnlinkedAt).HasColumnName("unlinked_at").HasColumnType("timestamptz");
            entity.Property(e => e.UnlinkedBy).HasColumnName("unlinked_by");
            entity.Property(e => e.UnlinkReason).HasColumnName("unlink_reason");
            entity.Property(e => e.RecordVersion).HasColumnName("record_version").IsConcurrencyToken();
            // One live link per sign-in identity and per party: fails closed when two staff link at once.
            entity.HasIndex(e => new { e.LegalEntityId, e.Subject }).IsUnique().HasFilter("status = 'LINKED'").HasDatabaseName("ux_portal_identity_subject");
            entity.HasIndex(e => new { e.LegalEntityId, e.PartyId }).IsUnique().HasFilter("status = 'LINKED'").HasDatabaseName("ux_portal_identity_party");
        });

        modelBuilder.Entity<PermissionMatrixRow>(entity =>
        {
            entity.ToTable("permission_matrix", table =>
            {
                var states = string.Join(", ", MatrixStatuses.All.Select(s => $"'{s}'"));
                table.HasCheckConstraint("ck_permission_matrix_status", $"status IN ({states})");
            });
            entity.HasKey(e => e.MatrixId).HasName("pk_permission_matrix");
            entity.Property(e => e.MatrixId).HasColumnName("matrix_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.VersionNo).HasColumnName("version_no");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.Scenarios).HasColumnName("scenarios").HasColumnType("jsonb");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(e => e.LastEditedBy).HasColumnName("last_edited_by");
            entity.Property(e => e.SubmittedBy).HasColumnName("submitted_by");
            entity.Property(e => e.SubmittedAt).HasColumnName("submitted_at").HasColumnType("timestamptz");
            entity.Property(e => e.ApprovedBy).HasColumnName("approved_by");
            entity.Property(e => e.ApprovedAt).HasColumnName("approved_at").HasColumnType("timestamptz");
            entity.Property(e => e.EffectiveFrom).HasColumnName("effective_from").HasColumnType("timestamptz");
            entity.HasIndex(e => new { e.LegalEntityId, e.VersionNo }).IsUnique().HasDatabaseName("ux_permission_matrix_version");
            entity.HasIndex(e => new { e.LegalEntityId, e.Status, e.EffectiveFrom }).HasDatabaseName("ix_permission_matrix_effective");
        });

        modelBuilder.Entity<PermissionMatrixEntryRow>(entity =>
        {
            entity.ToTable("permission_matrix_entry", table =>
            {
                var outcomes = string.Join(", ", MatrixOutcomes.All.Select(s => $"'{s}'"));
                table.HasCheckConstraint("ck_permission_matrix_entry_outcome", $"outcome IN ({outcomes})");
                table.HasCheckConstraint("ck_permission_matrix_entry_level", "level IN ('EU', 'COUNTRY', 'LE')");
                table.HasCheckConstraint("ck_permission_matrix_entry_max", "max_amount IS NULL OR max_amount >= 0");
            });
            entity.HasKey(e => e.EntryId).HasName("pk_permission_matrix_entry");
            entity.Property(e => e.EntryId).HasColumnName("entry_id");
            entity.Property(e => e.MatrixId).HasColumnName("matrix_id");
            entity.Property(e => e.TransactionType).HasColumnName("transaction_type");
            entity.Property(e => e.Channel).HasColumnName("channel");
            entity.Property(e => e.ActorType).HasColumnName("actor_type");
            entity.Property(e => e.ProductLine).HasColumnName("product_line");
            entity.Property(e => e.Outcome).HasColumnName("outcome");
            entity.Property(e => e.MaxAmount).HasColumnName("max_amount").HasColumnType("numeric(18,2)");
            entity.Property(e => e.Level).HasColumnName("level");
            entity.Property(e => e.Removed).HasColumnName("removed");
            entity.HasOne<PermissionMatrixRow>().WithMany().HasForeignKey(e => e.MatrixId).HasConstraintName("fk_permission_matrix_entry_matrix");
            entity.HasIndex(e => new { e.MatrixId, e.TransactionType, e.Channel, e.ActorType, e.ProductLine }).IsUnique().HasDatabaseName("ux_permission_matrix_entry_key");
        });
    }
}

/// <summary>A customer's declaration of the right of withdrawal (REQ-CHN-007, REQ-CHN-164..166).</summary>
internal sealed class WithdrawalRequestRow
{
    public Guid WithdrawalRequestId { get; set; }

    /// <summary>Business number issued by PLT numbering.</summary>
    public string RequestNumber { get; set; } = string.Empty;

    public LegalEntityId LegalEntityId { get; set; }

    public string Jurisdiction { get; set; } = string.Empty;

    public PolicyId PolicyId { get; set; }

    // TODO(hardening): declarant name, e-mail and reason are personal data; encrypt them with the data-protection envelope.
    public string DeclarantName { get; set; } = string.Empty;

    public string DeclarantEmail { get; set; } = string.Empty;

    public string? Reason { get; set; }

    /// <summary>WEB_DIRECT for the customer, STAFF for a declaration on behalf.</summary>
    public string Channel { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public Instant DeclaredAt { get; set; }

    public string DeclaredBy { get; set; } = string.Empty;

    public Instant ConcludedAt { get; set; }

    public int WindowDays { get; set; }

    /// <summary>The last second of the window (23:59:59 Europe/Athens of the last day).</summary>
    public Instant DeadlineAt { get; set; }

    /// <summary>SHA-256 of the confirmation token (the token itself is never stored).</summary>
    public string? TokenHash { get; set; }

    public Instant? TokenExpiresAt { get; set; }

    /// <summary>Platform time at the confirmation step (the legally relevant time).</summary>
    public Instant? ConfirmedAt { get; set; }

    public string? DeclineCode { get; set; }

    /// <summary>The reference POL receives as <c>channelRef</c> (equals the request number).</summary>
    public string ChannelRef { get; set; } = string.Empty;

    public Instant? PolSubmittedAt { get; set; }

    public Instant? ClosedAt { get; set; }

    public int RecordVersion { get; set; }
}

/// <summary>Design-time factory for <c>dotnet ef migrations add … --project src/CoreIns.Modules.Channels</c>.</summary>
internal sealed class ChannelsDbContextDesignTimeFactory : IDesignTimeDbContextFactory<ChannelsDbContext>
{
    public ChannelsDbContext CreateDbContext(string[] args) =>
        new(ModuleDbContextRegistration.MigrationOptions<ChannelsDbContext>("Host=localhost;Database=coreins_design;Username=design", ChannelsModule.Schema));
}

/// <summary>A version of the transaction-permission matrix of a legal entity (REQ-CHN-047..052). Maker-checker, effective-dated.</summary>
internal sealed class PermissionMatrixRow
{
    public Guid MatrixId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    /// <summary>1, 2, 3, ... per legal entity.</summary>
    public int VersionNo { get; set; }

    public string Status { get; set; } = MatrixStatuses.Draft;

    /// <summary>The scenario list the matrix is tested with (jsonb array).</summary>
    public string Scenarios { get; set; } = "[]";

    public string CreatedBy { get; set; } = string.Empty;

    public Instant CreatedAt { get; set; }

    public string LastEditedBy { get; set; } = string.Empty;

    public string? SubmittedBy { get; set; }

    public Instant? SubmittedAt { get; set; }

    public string? ApprovedBy { get; set; }

    public Instant? ApprovedAt { get; set; }

    /// <summary>When the matrix takes over; set by the checker. The newest SCHEDULED matrix whose time has come is in force.</summary>
    public Instant? EffectiveFrom { get; set; }
}

/// <summary>One entry of a matrix version. A dimension of <c>*</c> is the wildcard; EU and COUNTRY levels are final.</summary>
internal sealed class PermissionMatrixEntryRow
{
    public Guid EntryId { get; set; }

    public Guid MatrixId { get; set; }

    public string TransactionType { get; set; } = string.Empty;

    public string Channel { get; set; } = MatrixDims.Any;

    public string ActorType { get; set; } = MatrixDims.Any;

    public string ProductLine { get; set; } = MatrixDims.Any;

    public string Outcome { get; set; } = MatrixOutcomes.Deny;

    public decimal? MaxAmount { get; set; }

    public string Level { get; set; } = MatrixLevels.LegalEntity;

    /// <summary>A removed entry stays as a row (the app role has no DELETE); readers skip it.</summary>
    public bool Removed { get; set; }
}
