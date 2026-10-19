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

    protected override string Schema => ChannelsModule.Schema;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

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
