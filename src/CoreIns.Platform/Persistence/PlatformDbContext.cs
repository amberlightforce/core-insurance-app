using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CoreIns.Platform.Persistence;

/// <summary>
/// The <c>plt</c> schema as an EF Core model: it versions the platform tables through EF Core migrations, run only by
/// the migrate job (ADR §1). Runtime access to these tables is set-based SQL (Npgsql/Dapper) on the scope's
/// <see cref="DbSession"/> connection, so the context is internal and used only for migrations.
/// </summary>
internal sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "plt";

    internal const string Pending = "Pending";
    internal const string Dispatched = "Dispatched";

    protected override string Schema => SchemaName;

    public DbSet<OutboxMessageRow> OutboxMessages => Set<OutboxMessageRow>();

    public DbSet<AggregateSequenceRow> AggregateSequences => Set<AggregateSequenceRow>();

    public DbSet<ProcessedEventRow> ProcessedEvents => Set<ProcessedEventRow>();

    public DbSet<DeadLetterRow> DeadLetters => Set<DeadLetterRow>();

    public DbSet<EventArchiveRow> EventArchive => Set<EventArchiveRow>();

    public DbSet<IdempotencyRecordRow> IdempotencyRecords => Set<IdempotencyRecordRow>();

    public DbSet<AuditEventRow> AuditEvents => Set<AuditEventRow>();

    public DbSet<AuditChainHeadRow> AuditChainHeads => Set<AuditChainHeadRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<OutboxMessageRow>(entity =>
        {
            entity.ToTable("outbox_message", table =>
            {
                EnvelopeChecks(table, "outbox_message");
                table.HasCheckConstraint("ck_outbox_message_status", "status IN ('Pending', 'Dispatched')");
                table.HasCheckConstraint("ck_outbox_message_attempts", "attempts >= 0");
            });
            entity.HasKey(e => e.EventId).HasName("pk_outbox_message");
            MapEnvelope(entity);
            entity.Property(e => e.Position).HasColumnName("position").UseIdentityAlwaysColumn();
            entity.Property(e => e.Status).HasColumnName("status").IsRequired();
            entity.Property(e => e.Attempts).HasColumnName("attempts");
            entity.Property(e => e.NextAttemptAt).HasColumnName("next_attempt_at").HasColumnType("timestamptz");
            entity.Property(e => e.LeaseUntil).HasColumnName("lease_until").HasColumnType("timestamptz");
            entity.Property(e => e.LeaseOwner).HasColumnName("lease_owner");
            entity.Property(e => e.LastError).HasColumnName("last_error");
            entity.Property(e => e.DispatchedAt).HasColumnName("dispatched_at").HasColumnType("timestamptz");
            entity.HasIndex(e => new { e.AggregateType, e.AggregateId, e.AggregateSequence }).IsUnique()
                .HasDatabaseName("ux_outbox_message_aggregate_sequence");
            entity.HasIndex(e => e.Position).HasFilter("status = 'Pending'").HasDatabaseName("ix_outbox_message_pending");
            entity.HasIndex(e => e.DispatchedAt).HasFilter("status = 'Dispatched'").HasDatabaseName("ix_outbox_message_dispatched");
        });

        modelBuilder.Entity<AggregateSequenceRow>(entity =>
        {
            entity.ToTable("aggregate_sequence", table =>
                table.HasCheckConstraint("ck_aggregate_sequence_last", "last_sequence >= 1"));
            entity.HasKey(e => new { e.AggregateType, e.AggregateId }).HasName("pk_aggregate_sequence");
            entity.Property(e => e.AggregateType).HasColumnName("aggregate_type");
            entity.Property(e => e.AggregateId).HasColumnName("aggregate_id");
            entity.Property(e => e.LastSequence).HasColumnName("last_sequence");
        });

        modelBuilder.Entity<ProcessedEventRow>(entity =>
        {
            entity.ToTable("processed_event");
            entity.HasKey(e => new { e.Handler, e.EventId }).HasName("pk_processed_event");
            entity.Property(e => e.Handler).HasColumnName("handler");
            entity.Property(e => e.EventId).HasColumnName("event_id");
            entity.Property(e => e.ProcessedAt).HasColumnName("processed_at").HasColumnType("timestamptz");
            entity.Property(e => e.ReplayCount).HasColumnName("replay_count");
        });

        modelBuilder.Entity<DeadLetterRow>(entity =>
        {
            entity.ToTable("outbox_dead_letter", table =>
            {
                table.HasCheckConstraint("ck_outbox_dead_letter_status", "status IN ('Parked', 'Replayed', 'DiscardPending', 'Discarded')");
                table.HasCheckConstraint("ck_outbox_dead_letter_attempts", "attempts >= 1");
            });
            entity.HasKey(e => e.DeadLetterId).HasName("pk_outbox_dead_letter");
            entity.Property(e => e.DeadLetterId).HasColumnName("dead_letter_id");
            entity.Property(e => e.Handler).HasColumnName("handler");
            entity.Property(e => e.EventId).HasColumnName("event_id");
            entity.Property(e => e.EventType).HasColumnName("event_type");
            entity.Property(e => e.AggregateType).HasColumnName("aggregate_type");
            entity.Property(e => e.AggregateId).HasColumnName("aggregate_id");
            entity.Property(e => e.ErrorClass).HasColumnName("error_class");
            entity.Property(e => e.ErrorMessage).HasColumnName("error_message");
            entity.Property(e => e.Attempts).HasColumnName("attempts");
            entity.Property(e => e.ParkedAt).HasColumnName("parked_at").HasColumnType("timestamptz");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.ResolvedAt).HasColumnName("resolved_at").HasColumnType("timestamptz");
            entity.Property(e => e.ResolutionReason).HasColumnName("resolution_reason");
            entity.HasIndex(e => new { e.Handler, e.EventId }).IsUnique().HasFilter("status = 'Parked'")
                .HasDatabaseName("ux_outbox_dead_letter_parked");
        });

        modelBuilder.Entity<EventArchiveRow>(entity =>
        {
            entity.ToTable("event_archive", table => EnvelopeChecks(table, "event_archive"));
            entity.HasKey(e => e.EventId).HasName("pk_event_archive");
            MapEnvelope(entity);
            entity.Property(e => e.Position).HasColumnName("position");
            entity.Property(e => e.ArchivedAt).HasColumnName("archived_at").HasColumnType("timestamptz");
            entity.HasIndex(e => new { e.AggregateType, e.AggregateId, e.AggregateSequence }).IsUnique()
                .HasDatabaseName("ux_event_archive_aggregate_sequence");
            entity.HasIndex(e => e.Position).HasDatabaseName("ix_event_archive_position");
            entity.HasIndex(e => e.RecordedAt).HasDatabaseName("ix_event_archive_recorded_at");
        });

        modelBuilder.Entity<IdempotencyRecordRow>(entity =>
        {
            entity.ToTable("idempotency_record", table =>
            {
                table.HasCheckConstraint("ck_idempotency_record_status", "status IN ('InProgress', 'Completed')");
                table.HasCheckConstraint("ck_idempotency_record_hash", "request_hash ~ '^[0-9a-f]{64}$'");
                table.HasCheckConstraint("ck_idempotency_record_expiry", "expires_at > created_at");
            });
            entity.HasKey(e => new { e.Scope, e.IdempotencyKey }).HasName("pk_idempotency_record");
            entity.Property(e => e.Scope).HasColumnName("scope");
            entity.Property(e => e.IdempotencyKey).HasColumnName("idempotency_key");
            entity.Property(e => e.RequestHash).HasColumnName("request_hash").HasColumnType("char(64)");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.ResponseStatus).HasColumnName("response_status");
            entity.Property(e => e.ResponseContentType).HasColumnName("response_content_type");
            entity.Property(e => e.ResponseBody).HasColumnName("response_body");
            entity.Property(e => e.ResponseHeaders).HasColumnName("response_headers").HasColumnType("jsonb");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(e => e.CompletedAt).HasColumnName("completed_at").HasColumnType("timestamptz");
            entity.Property(e => e.ExpiresAt).HasColumnName("expires_at").HasColumnType("timestamptz");
            entity.HasIndex(e => e.ExpiresAt).HasDatabaseName("ix_idempotency_record_expires_at");
        });

        modelBuilder.Entity<AuditEventRow>(entity =>
        {
            entity.ToTable("audit_event", table =>
            {
                table.HasCheckConstraint("ck_audit_event_sequence", "sequence >= 1");
                table.HasCheckConstraint("ck_audit_event_hashes", "prev_hash ~ '^[0-9a-f]{64}$' AND hash ~ '^[0-9a-f]{64}$'");
                table.HasCheckConstraint("ck_audit_event_actor_kind", "actor_kind IN ('USER', 'SERVICE', 'AI_AGENT')");
                table.HasCheckConstraint("ck_audit_event_outcome", "outcome IN ('Succeeded', 'Rejected', 'Failed')");
                table.HasCheckConstraint("ck_audit_event_origin", "origin IN ('LIVE', 'MIGRATION', 'REPLAY')");
                table.HasCheckConstraint("ck_audit_event_jurisdiction", "jurisdiction ~ '^[A-Z]{2}$'");
                table.HasCheckConstraint("ck_audit_event_correlation", "correlation_id ~ '^[0-9a-f]{32}$'");
                table.HasCheckConstraint("ck_audit_event_changes", "jsonb_typeof(changes) = 'array'");
                table.HasCheckConstraint("ck_audit_event_business_keys", "jsonb_typeof(business_keys) = 'object'");
            });
            entity.HasKey(e => e.AuditId).HasName("pk_audit_event");
            entity.Property(e => e.AuditId).HasColumnName("audit_id");
            entity.Property(e => e.ChainDate).HasColumnName("chain_date");
            entity.Property(e => e.Sequence).HasColumnName("sequence");
            entity.Property(e => e.PrevHash).HasColumnName("prev_hash").HasColumnType("char(64)");
            entity.Property(e => e.Hash).HasColumnName("hash").HasColumnType("char(64)");
            entity.Property(e => e.ActorKind).HasColumnName("actor_kind");
            entity.Property(e => e.ActorId).HasColumnName("actor_id");
            entity.Property(e => e.OnBehalfOf).HasColumnName("on_behalf_of");
            entity.Property(e => e.RoleCodes).HasColumnName("role_codes");
            entity.Property(e => e.AuthorityCheckId).HasColumnName("authority_check_id");
            entity.Property(e => e.AuthorityUsed).HasColumnName("authority_used");
            entity.Property(e => e.Operation).HasColumnName("operation");
            entity.Property(e => e.Outcome).HasColumnName("outcome");
            entity.Property(e => e.ErrorCode).HasColumnName("error_code");
            entity.Property(e => e.ObjectModule).HasColumnName("object_module");
            entity.Property(e => e.ObjectType).HasColumnName("object_type");
            entity.Property(e => e.ObjectId).HasColumnName("object_id");
            entity.Property(e => e.ObjectNumber).HasColumnName("object_number");
            entity.Property(e => e.Changes).HasColumnName("changes").HasColumnType("jsonb");
            entity.Property(e => e.Reason).HasColumnName("reason");
            entity.Property(e => e.Channel).HasColumnName("channel");
            entity.Property(e => e.CorrelationId).HasColumnName("correlation_id").HasColumnType("char(32)");
            entity.Property(e => e.CausationId).HasColumnName("causation_id");
            entity.Property(e => e.AiInteractionId).HasColumnName("ai_interaction_id");
            entity.Property(e => e.BusinessKeys).HasColumnName("business_keys").HasColumnType("jsonb");
            entity.Property(e => e.Origin).HasColumnName("origin");
            entity.Property(e => e.LegalEntity).HasColumnName("legal_entity");
            entity.Property(e => e.Jurisdiction).HasColumnName("jurisdiction").HasColumnType("char(2)");
            entity.Property(e => e.OccurredAt).HasColumnName("occurred_at").HasColumnType("timestamptz");
            entity.Property(e => e.RecordedAt).HasColumnName("recorded_at").HasColumnType("timestamptz");
            entity.HasIndex(e => new { e.ChainDate, e.Sequence }).IsUnique().HasDatabaseName("ux_audit_event_chain");
            entity.HasIndex(e => new { e.ObjectType, e.ObjectId }).HasDatabaseName("ix_audit_event_object");
            entity.HasIndex(e => e.RecordedAt).HasDatabaseName("ix_audit_event_recorded_at");
        });

        modelBuilder.Entity<AuditChainHeadRow>(entity =>
        {
            entity.ToTable("audit_chain_head", table =>
                table.HasCheckConstraint("ck_audit_chain_head", "last_sequence >= 0 AND last_hash ~ '^[0-9a-f]{64}$'"));
            entity.HasKey(e => e.ChainDate).HasName("pk_audit_chain_head");
            entity.Property(e => e.ChainDate).HasColumnName("chain_date");
            entity.Property(e => e.LastSequence).HasColumnName("last_sequence");
            entity.Property(e => e.LastHash).HasColumnName("last_hash").HasColumnType("char(64)");
        });
    }

    private static void EnvelopeChecks(TableBuilder table, string name)
    {
        table.HasCheckConstraint($"ck_{name}_sequence", "aggregate_sequence >= 1");
        table.HasCheckConstraint(
            $"ck_{name}_set",
            "(set_id IS NULL AND set_size IS NULL AND set_index IS NULL) OR "
            + "(set_id IS NOT NULL AND set_size >= 1 AND set_index >= 1 AND set_index <= set_size)");
        table.HasCheckConstraint($"ck_{name}_origin", "origin IN ('LIVE', 'MIGRATION', 'REPLAY')");
        table.HasCheckConstraint($"ck_{name}_classification", "data_classification IN ('P0', 'P1', 'P2', 'P3')");
        table.HasCheckConstraint($"ck_{name}_actor_kind", "actor_kind IN ('USER', 'SERVICE', 'AI_AGENT')");
        table.HasCheckConstraint($"ck_{name}_jurisdiction", "jurisdiction ~ '^[A-Z]{2}$'");
        table.HasCheckConstraint($"ck_{name}_configuration_hash", "configuration_hash ~ '^[0-9a-f]{64}$'");
        table.HasCheckConstraint($"ck_{name}_correlation", "correlation_id ~ '^[0-9a-f]{32}$' AND correlation_id <> repeat('0', 32)");
        table.HasCheckConstraint($"ck_{name}_schema_version", "schema_version ~ '^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)$'");
        table.HasCheckConstraint($"ck_{name}_payload", "jsonb_typeof(payload) = 'object' AND octet_length(payload::text) <= 262144");
        table.HasCheckConstraint(
            $"ck_{name}_business_keys",
            "jsonb_typeof(business_keys) = 'object' AND business_keys <> '{}'::jsonb");
    }

    private static void MapEnvelope<T>(EntityTypeBuilder<T> entity)
        where T : EnvelopeColumns
    {
        entity.Property(e => e.EventId).HasColumnName("event_id");
        entity.Property(e => e.EventType).HasColumnName("event_type").IsRequired();
        entity.Property(e => e.SchemaVersion).HasColumnName("schema_version").IsRequired();
        entity.Property(e => e.Producer).HasColumnName("producer").IsRequired();
        entity.Property(e => e.AggregateType).HasColumnName("aggregate_type").IsRequired();
        entity.Property(e => e.AggregateId).HasColumnName("aggregate_id").IsRequired();
        entity.Property(e => e.AggregateSequence).HasColumnName("aggregate_sequence");
        entity.Property(e => e.OccurredAt).HasColumnName("occurred_at").HasColumnType("timestamptz");
        entity.Property(e => e.RecordedAt).HasColumnName("recorded_at").HasColumnType("timestamptz");
        entity.Property(e => e.LegalEntity).HasColumnName("legal_entity").IsRequired();
        entity.Property(e => e.Jurisdiction).HasColumnName("jurisdiction").HasColumnType("char(2)").IsRequired();
        entity.Property(e => e.ConfigurationHash).HasColumnName("configuration_hash").HasColumnType("char(64)").IsRequired();
        entity.Property(e => e.BusinessKeys).HasColumnName("business_keys").HasColumnType("jsonb").IsRequired();
        entity.Property(e => e.CorrelationId).HasColumnName("correlation_id").HasColumnType("char(32)").IsRequired();
        entity.Property(e => e.CausationId).HasColumnName("causation_id");
        entity.Property(e => e.ActorKind).HasColumnName("actor_kind").IsRequired();
        entity.Property(e => e.ActorId).HasColumnName("actor_id").IsRequired();
        entity.Property(e => e.AiInteractionId).HasColumnName("ai_interaction_id");
        entity.Property(e => e.Origin).HasColumnName("origin").IsRequired();
        entity.Property(e => e.DataClassification).HasColumnName("data_classification").IsRequired();
        entity.Property(e => e.SetId).HasColumnName("set_id");
        entity.Property(e => e.SetSize).HasColumnName("set_size");
        entity.Property(e => e.SetIndex).HasColumnName("set_index");
        entity.Property(e => e.Payload).HasColumnName("payload").HasColumnType("jsonb").IsRequired();
    }
}

/// <summary>Design-time factory for <c>dotnet ef migrations</c> (no database connection is opened).</summary>
internal sealed class PlatformDbContextDesignTimeFactory : IDesignTimeDbContextFactory<PlatformDbContext>
{
    public PlatformDbContext CreateDbContext(string[] args) =>
        new(ModuleDbContextRegistration.MigrationOptions<PlatformDbContext>(
            "Host=localhost;Database=coreins_design;Username=design", PlatformDbContext.SchemaName));
}

/// <summary>Envelope columns shared by the outbox and the archive.</summary>
internal abstract class EnvelopeColumns
{
    public Guid EventId { get; set; }

    public string EventType { get; set; } = string.Empty;

    public string SchemaVersion { get; set; } = string.Empty;

    public string Producer { get; set; } = string.Empty;

    public string AggregateType { get; set; } = string.Empty;

    public string AggregateId { get; set; } = string.Empty;

    public long AggregateSequence { get; set; }

    public DateTime OccurredAt { get; set; }

    public DateTime RecordedAt { get; set; }

    public string LegalEntity { get; set; } = string.Empty;

    public string Jurisdiction { get; set; } = string.Empty;

    public string ConfigurationHash { get; set; } = string.Empty;

    public string BusinessKeys { get; set; } = "{}";

    public string CorrelationId { get; set; } = string.Empty;

    public Guid? CausationId { get; set; }

    public string ActorKind { get; set; } = string.Empty;

    public string ActorId { get; set; } = string.Empty;

    public Guid? AiInteractionId { get; set; }

    public string Origin { get; set; } = string.Empty;

    public string DataClassification { get; set; } = string.Empty;

    public Guid? SetId { get; set; }

    public int? SetSize { get; set; }

    public int? SetIndex { get; set; }

    public string Payload { get; set; } = "{}";
}

/// <summary><c>plt.outbox_message</c>: events written in the producer's transaction, waiting for dispatch.</summary>
internal sealed class OutboxMessageRow : EnvelopeColumns
{
    public long Position { get; set; }

    public string Status { get; set; } = PlatformDbContext.Pending;

    public int Attempts { get; set; }

    public DateTime NextAttemptAt { get; set; }

    public DateTime? LeaseUntil { get; set; }

    public string? LeaseOwner { get; set; }

    public string? LastError { get; set; }

    public DateTime? DispatchedAt { get; set; }
}

/// <summary><c>plt.event_archive</c>: every dispatched event, kept for replay independently of outbox purge.</summary>
internal sealed class EventArchiveRow : EnvelopeColumns
{
    public long Position { get; set; }

    public DateTime ArchivedAt { get; set; }
}

/// <summary><c>plt.aggregate_sequence</c>: the last sequence per aggregate (gap-free assignment under a row lock).</summary>
internal sealed class AggregateSequenceRow
{
    public string AggregateType { get; set; } = string.Empty;

    public string AggregateId { get; set; } = string.Empty;

    public long LastSequence { get; set; }
}

/// <summary><c>plt.processed_event</c>: idempotent consumption marker per handler and event.</summary>
internal sealed class ProcessedEventRow
{
    public string Handler { get; set; } = string.Empty;

    public Guid EventId { get; set; }

    public DateTime ProcessedAt { get; set; }

    public int ReplayCount { get; set; }
}

/// <summary><c>plt.outbox_dead_letter</c>: a handler that kept failing for an event.</summary>
internal sealed class DeadLetterRow
{
    public Guid DeadLetterId { get; set; }

    public string Handler { get; set; } = string.Empty;

    public Guid EventId { get; set; }

    public string EventType { get; set; } = string.Empty;

    public string AggregateType { get; set; } = string.Empty;

    public string AggregateId { get; set; } = string.Empty;

    public string ErrorClass { get; set; } = string.Empty;

    public string ErrorMessage { get; set; } = string.Empty;

    public int Attempts { get; set; }

    public DateTime ParkedAt { get; set; }

    public string Status { get; set; } = "Parked";

    public DateTime? ResolvedAt { get; set; }

    public string? ResolutionReason { get; set; }
}

/// <summary><c>plt.idempotency_record</c>: key → original result, kept 7 days (contract §3.5.3).</summary>
internal sealed class IdempotencyRecordRow
{
    public string Scope { get; set; } = string.Empty;

    public Guid IdempotencyKey { get; set; }

    public string RequestHash { get; set; } = string.Empty;

    public string Status { get; set; } = "InProgress";

    public int? ResponseStatus { get; set; }

    public string? ResponseContentType { get; set; }

    public byte[]? ResponseBody { get; set; }

    public string? ResponseHeaders { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public DateTime ExpiresAt { get; set; }
}

/// <summary><c>plt.audit_event</c>: insert-only, hash-chained per day (D-ARC-15).</summary>
internal sealed class AuditEventRow
{
    public Guid AuditId { get; set; }

    public DateOnly ChainDate { get; set; }

    public long Sequence { get; set; }

    public string PrevHash { get; set; } = string.Empty;

    public string Hash { get; set; } = string.Empty;

    public string ActorKind { get; set; } = string.Empty;

    public string ActorId { get; set; } = string.Empty;

    public string? OnBehalfOf { get; set; }

    public string[] RoleCodes { get; set; } = [];

    public Guid? AuthorityCheckId { get; set; }

    public string? AuthorityUsed { get; set; }

    public string Operation { get; set; } = string.Empty;

    public string Outcome { get; set; } = string.Empty;

    public string? ErrorCode { get; set; }

    public string? ObjectModule { get; set; }

    public string? ObjectType { get; set; }

    public string? ObjectId { get; set; }

    public string? ObjectNumber { get; set; }

    public string Changes { get; set; } = "[]";

    public string? Reason { get; set; }

    public string? Channel { get; set; }

    public string CorrelationId { get; set; } = string.Empty;

    public Guid? CausationId { get; set; }

    public Guid? AiInteractionId { get; set; }

    public string BusinessKeys { get; set; } = "{}";

    public string Origin { get; set; } = string.Empty;

    public string LegalEntity { get; set; } = string.Empty;

    public string Jurisdiction { get; set; } = string.Empty;

    public DateTime OccurredAt { get; set; }

    public DateTime RecordedAt { get; set; }
}

/// <summary><c>plt.audit_chain_head</c>: the last sequence and hash of each day's chain (a cache; verification uses the events).</summary>
internal sealed class AuditChainHeadRow
{
    public DateOnly ChainDate { get; set; }

    public long LastSequence { get; set; }

    public string LastHash { get; set; } = string.Empty;
}
