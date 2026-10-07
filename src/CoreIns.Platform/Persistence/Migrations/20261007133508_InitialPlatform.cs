using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CoreIns.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialPlatform : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "plt");

            migrationBuilder.CreateTable(
                name: "aggregate_sequence",
                schema: "plt",
                columns: table => new
                {
                    aggregate_type = table.Column<string>(type: "text", nullable: false),
                    aggregate_id = table.Column<string>(type: "text", nullable: false),
                    last_sequence = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_aggregate_sequence", x => new { x.aggregate_type, x.aggregate_id });
                    table.CheckConstraint("ck_aggregate_sequence_last", "last_sequence >= 1");
                });

            migrationBuilder.CreateTable(
                name: "audit_chain_head",
                schema: "plt",
                columns: table => new
                {
                    chain_date = table.Column<DateOnly>(type: "date", nullable: false),
                    last_sequence = table.Column<long>(type: "bigint", nullable: false),
                    last_hash = table.Column<string>(type: "char(64)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_chain_head", x => x.chain_date);
                    table.CheckConstraint("ck_audit_chain_head", "last_sequence >= 0 AND last_hash ~ '^[0-9a-f]{64}$'");
                });

            migrationBuilder.CreateTable(
                name: "audit_event",
                schema: "plt",
                columns: table => new
                {
                    audit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    chain_date = table.Column<DateOnly>(type: "date", nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false),
                    prev_hash = table.Column<string>(type: "char(64)", nullable: false),
                    hash = table.Column<string>(type: "char(64)", nullable: false),
                    actor_kind = table.Column<string>(type: "text", nullable: false),
                    actor_id = table.Column<string>(type: "text", nullable: false),
                    on_behalf_of = table.Column<string>(type: "text", nullable: true),
                    role_codes = table.Column<string[]>(type: "text[]", nullable: false),
                    authority_check_id = table.Column<Guid>(type: "uuid", nullable: true),
                    authority_used = table.Column<string>(type: "text", nullable: true),
                    operation = table.Column<string>(type: "text", nullable: false),
                    outcome = table.Column<string>(type: "text", nullable: false),
                    error_code = table.Column<string>(type: "text", nullable: true),
                    object_module = table.Column<string>(type: "text", nullable: true),
                    object_type = table.Column<string>(type: "text", nullable: true),
                    object_id = table.Column<string>(type: "text", nullable: true),
                    object_number = table.Column<string>(type: "text", nullable: true),
                    changes = table.Column<string>(type: "jsonb", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    channel = table.Column<string>(type: "text", nullable: true),
                    correlation_id = table.Column<string>(type: "char(32)", nullable: false),
                    causation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    ai_interaction_id = table.Column<Guid>(type: "uuid", nullable: true),
                    business_keys = table.Column<string>(type: "jsonb", nullable: false),
                    origin = table.Column<string>(type: "text", nullable: false),
                    legal_entity = table.Column<string>(type: "text", nullable: false),
                    jurisdiction = table.Column<string>(type: "char(2)", nullable: false),
                    occurred_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    recorded_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_event", x => x.audit_id);
                    table.CheckConstraint("ck_audit_event_actor_kind", "actor_kind IN ('USER', 'SERVICE', 'AI_AGENT')");
                    table.CheckConstraint("ck_audit_event_business_keys", "jsonb_typeof(business_keys) = 'object'");
                    table.CheckConstraint("ck_audit_event_changes", "jsonb_typeof(changes) = 'array'");
                    table.CheckConstraint("ck_audit_event_correlation", "correlation_id ~ '^[0-9a-f]{32}$'");
                    table.CheckConstraint("ck_audit_event_hashes", "prev_hash ~ '^[0-9a-f]{64}$' AND hash ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_audit_event_jurisdiction", "jurisdiction ~ '^[A-Z]{2}$'");
                    table.CheckConstraint("ck_audit_event_origin", "origin IN ('LIVE', 'MIGRATION', 'REPLAY')");
                    table.CheckConstraint("ck_audit_event_outcome", "outcome IN ('Succeeded', 'Rejected', 'Failed')");
                    table.CheckConstraint("ck_audit_event_sequence", "sequence >= 1");
                });

            migrationBuilder.CreateTable(
                name: "event_archive",
                schema: "plt",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<long>(type: "bigint", nullable: false),
                    archived_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    event_type = table.Column<string>(type: "text", nullable: false),
                    schema_version = table.Column<string>(type: "text", nullable: false),
                    producer = table.Column<string>(type: "text", nullable: false),
                    aggregate_type = table.Column<string>(type: "text", nullable: false),
                    aggregate_id = table.Column<string>(type: "text", nullable: false),
                    aggregate_sequence = table.Column<long>(type: "bigint", nullable: false),
                    occurred_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    recorded_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    legal_entity = table.Column<string>(type: "text", nullable: false),
                    jurisdiction = table.Column<string>(type: "char(2)", nullable: false),
                    configuration_hash = table.Column<string>(type: "char(64)", nullable: false),
                    business_keys = table.Column<string>(type: "jsonb", nullable: false),
                    correlation_id = table.Column<string>(type: "char(32)", nullable: false),
                    causation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_kind = table.Column<string>(type: "text", nullable: false),
                    actor_id = table.Column<string>(type: "text", nullable: false),
                    ai_interaction_id = table.Column<Guid>(type: "uuid", nullable: true),
                    origin = table.Column<string>(type: "text", nullable: false),
                    data_classification = table.Column<string>(type: "text", nullable: false),
                    set_id = table.Column<Guid>(type: "uuid", nullable: true),
                    set_size = table.Column<int>(type: "integer", nullable: true),
                    set_index = table.Column<int>(type: "integer", nullable: true),
                    payload = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_event_archive", x => x.event_id);
                    table.CheckConstraint("ck_event_archive_actor_kind", "actor_kind IN ('USER', 'SERVICE', 'AI_AGENT')");
                    table.CheckConstraint("ck_event_archive_business_keys", "jsonb_typeof(business_keys) = 'object' AND business_keys <> '{}'::jsonb");
                    table.CheckConstraint("ck_event_archive_classification", "data_classification IN ('P0', 'P1', 'P2', 'P3')");
                    table.CheckConstraint("ck_event_archive_configuration_hash", "configuration_hash ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_event_archive_correlation", "correlation_id ~ '^[0-9a-f]{32}$' AND correlation_id <> repeat('0', 32)");
                    table.CheckConstraint("ck_event_archive_jurisdiction", "jurisdiction ~ '^[A-Z]{2}$'");
                    table.CheckConstraint("ck_event_archive_origin", "origin IN ('LIVE', 'MIGRATION', 'REPLAY')");
                    table.CheckConstraint("ck_event_archive_payload", "jsonb_typeof(payload) = 'object' AND octet_length(payload::text) <= 262144");
                    table.CheckConstraint("ck_event_archive_schema_version", "schema_version ~ '^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)$'");
                    table.CheckConstraint("ck_event_archive_sequence", "aggregate_sequence >= 1");
                    table.CheckConstraint("ck_event_archive_set", "(set_id IS NULL AND set_size IS NULL AND set_index IS NULL) OR (set_id IS NOT NULL AND set_size >= 1 AND set_index >= 1 AND set_index <= set_size)");
                });

            migrationBuilder.CreateTable(
                name: "idempotency_record",
                schema: "plt",
                columns: table => new
                {
                    scope = table.Column<string>(type: "text", nullable: false),
                    idempotency_key = table.Column<Guid>(type: "uuid", nullable: false),
                    request_hash = table.Column<string>(type: "char(64)", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    response_status = table.Column<int>(type: "integer", nullable: true),
                    response_content_type = table.Column<string>(type: "text", nullable: true),
                    response_body = table.Column<byte[]>(type: "bytea", nullable: true),
                    response_headers = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    completed_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    expires_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_idempotency_record", x => new { x.scope, x.idempotency_key });
                    table.CheckConstraint("ck_idempotency_record_expiry", "expires_at > created_at");
                    table.CheckConstraint("ck_idempotency_record_hash", "request_hash ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_idempotency_record_status", "status IN ('InProgress', 'Completed')");
                });

            migrationBuilder.CreateTable(
                name: "outbox_dead_letter",
                schema: "plt",
                columns: table => new
                {
                    dead_letter_id = table.Column<Guid>(type: "uuid", nullable: false),
                    handler = table.Column<string>(type: "text", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "text", nullable: false),
                    aggregate_type = table.Column<string>(type: "text", nullable: false),
                    aggregate_id = table.Column<string>(type: "text", nullable: false),
                    error_class = table.Column<string>(type: "text", nullable: false),
                    error_message = table.Column<string>(type: "text", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    parked_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    resolved_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    resolution_reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_dead_letter", x => x.dead_letter_id);
                    table.CheckConstraint("ck_outbox_dead_letter_attempts", "attempts >= 1");
                    table.CheckConstraint("ck_outbox_dead_letter_status", "status IN ('Parked', 'Replayed', 'DiscardPending', 'Discarded')");
                });

            migrationBuilder.CreateTable(
                name: "outbox_message",
                schema: "plt",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    status = table.Column<string>(type: "text", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    lease_until = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    lease_owner = table.Column<string>(type: "text", nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    dispatched_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    event_type = table.Column<string>(type: "text", nullable: false),
                    schema_version = table.Column<string>(type: "text", nullable: false),
                    producer = table.Column<string>(type: "text", nullable: false),
                    aggregate_type = table.Column<string>(type: "text", nullable: false),
                    aggregate_id = table.Column<string>(type: "text", nullable: false),
                    aggregate_sequence = table.Column<long>(type: "bigint", nullable: false),
                    occurred_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    recorded_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    legal_entity = table.Column<string>(type: "text", nullable: false),
                    jurisdiction = table.Column<string>(type: "char(2)", nullable: false),
                    configuration_hash = table.Column<string>(type: "char(64)", nullable: false),
                    business_keys = table.Column<string>(type: "jsonb", nullable: false),
                    correlation_id = table.Column<string>(type: "char(32)", nullable: false),
                    causation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_kind = table.Column<string>(type: "text", nullable: false),
                    actor_id = table.Column<string>(type: "text", nullable: false),
                    ai_interaction_id = table.Column<Guid>(type: "uuid", nullable: true),
                    origin = table.Column<string>(type: "text", nullable: false),
                    data_classification = table.Column<string>(type: "text", nullable: false),
                    set_id = table.Column<Guid>(type: "uuid", nullable: true),
                    set_size = table.Column<int>(type: "integer", nullable: true),
                    set_index = table.Column<int>(type: "integer", nullable: true),
                    payload = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_message", x => x.event_id);
                    table.CheckConstraint("ck_outbox_message_actor_kind", "actor_kind IN ('USER', 'SERVICE', 'AI_AGENT')");
                    table.CheckConstraint("ck_outbox_message_attempts", "attempts >= 0");
                    table.CheckConstraint("ck_outbox_message_business_keys", "jsonb_typeof(business_keys) = 'object' AND business_keys <> '{}'::jsonb");
                    table.CheckConstraint("ck_outbox_message_classification", "data_classification IN ('P0', 'P1', 'P2', 'P3')");
                    table.CheckConstraint("ck_outbox_message_configuration_hash", "configuration_hash ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_outbox_message_correlation", "correlation_id ~ '^[0-9a-f]{32}$' AND correlation_id <> repeat('0', 32)");
                    table.CheckConstraint("ck_outbox_message_jurisdiction", "jurisdiction ~ '^[A-Z]{2}$'");
                    table.CheckConstraint("ck_outbox_message_origin", "origin IN ('LIVE', 'MIGRATION', 'REPLAY')");
                    table.CheckConstraint("ck_outbox_message_payload", "jsonb_typeof(payload) = 'object' AND octet_length(payload::text) <= 262144");
                    table.CheckConstraint("ck_outbox_message_schema_version", "schema_version ~ '^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)$'");
                    table.CheckConstraint("ck_outbox_message_sequence", "aggregate_sequence >= 1");
                    table.CheckConstraint("ck_outbox_message_set", "(set_id IS NULL AND set_size IS NULL AND set_index IS NULL) OR (set_id IS NOT NULL AND set_size >= 1 AND set_index >= 1 AND set_index <= set_size)");
                    table.CheckConstraint("ck_outbox_message_status", "status IN ('Pending', 'Dispatched')");
                });

            migrationBuilder.CreateTable(
                name: "processed_event",
                schema: "plt",
                columns: table => new
                {
                    handler = table.Column<string>(type: "text", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    processed_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    replay_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_processed_event", x => new { x.handler, x.event_id });
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_event_object",
                schema: "plt",
                table: "audit_event",
                columns: new[] { "object_type", "object_id" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_event_recorded_at",
                schema: "plt",
                table: "audit_event",
                column: "recorded_at");

            migrationBuilder.CreateIndex(
                name: "ux_audit_event_chain",
                schema: "plt",
                table: "audit_event",
                columns: new[] { "chain_date", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_event_archive_position",
                schema: "plt",
                table: "event_archive",
                column: "position");

            migrationBuilder.CreateIndex(
                name: "ix_event_archive_recorded_at",
                schema: "plt",
                table: "event_archive",
                column: "recorded_at");

            migrationBuilder.CreateIndex(
                name: "ux_event_archive_aggregate_sequence",
                schema: "plt",
                table: "event_archive",
                columns: new[] { "aggregate_type", "aggregate_id", "aggregate_sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_idempotency_record_expires_at",
                schema: "plt",
                table: "idempotency_record",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ux_outbox_dead_letter_parked",
                schema: "plt",
                table: "outbox_dead_letter",
                columns: new[] { "handler", "event_id" },
                unique: true,
                filter: "status = 'Parked'");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_message_dispatched",
                schema: "plt",
                table: "outbox_message",
                column: "dispatched_at",
                filter: "status = 'Dispatched'");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_message_pending",
                schema: "plt",
                table: "outbox_message",
                column: "position",
                filter: "status = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "ux_outbox_message_aggregate_sequence",
                schema: "plt",
                table: "outbox_message",
                columns: new[] { "aggregate_type", "aggregate_id", "aggregate_sequence" },
                unique: true);

            // F-1b (hand-written): plt.audit_event is insert-only for every role (D-ARC-15). The application role also
            // lacks UPDATE/DELETE grants (PlatformModule.PlatformGrants); this trigger refuses them even for the owner.
            migrationBuilder.Sql("""
                CREATE FUNCTION plt.audit_event_is_insert_only() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  RAISE EXCEPTION 'plt.audit_event is insert-only (%)', TG_OP USING ERRCODE = 'insufficient_privilege';
                END
                $$;
                CREATE TRIGGER audit_event_no_update_delete BEFORE UPDATE OR DELETE ON plt.audit_event
                  FOR EACH ROW EXECUTE FUNCTION plt.audit_event_is_insert_only();
                CREATE TRIGGER audit_event_no_truncate BEFORE TRUNCATE ON plt.audit_event
                  FOR EACH STATEMENT EXECUTE FUNCTION plt.audit_event_is_insert_only();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS audit_event_no_truncate ON plt.audit_event;
                DROP TRIGGER IF EXISTS audit_event_no_update_delete ON plt.audit_event;
                DROP FUNCTION IF EXISTS plt.audit_event_is_insert_only();
                """);

            migrationBuilder.DropTable(
                name: "aggregate_sequence",
                schema: "plt");

            migrationBuilder.DropTable(
                name: "audit_chain_head",
                schema: "plt");

            migrationBuilder.DropTable(
                name: "audit_event",
                schema: "plt");

            migrationBuilder.DropTable(
                name: "event_archive",
                schema: "plt");

            migrationBuilder.DropTable(
                name: "idempotency_record",
                schema: "plt");

            migrationBuilder.DropTable(
                name: "outbox_dead_letter",
                schema: "plt");

            migrationBuilder.DropTable(
                name: "outbox_message",
                schema: "plt");

            migrationBuilder.DropTable(
                name: "processed_event",
                schema: "plt");
        }
    }
}
