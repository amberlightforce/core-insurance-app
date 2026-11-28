using Microsoft.EntityFrameworkCore.Infrastructure;

namespace CoreIns.Platform.Persistence.Migrations;

/// <summary>BO-PLT-04: schema registry, message exchange log and file transfers (hand-written; runtime access is SQL in <c>OperationsStore</c>).</summary>
[DbContext(typeof(PlatformDbContext))]
[Microsoft.EntityFrameworkCore.Migrations.Migration("20261128120000_PlatformOperations")]
public sealed class PlatformOperations : Microsoft.EntityFrameworkCore.Migrations.Migration
{
    /// <inheritdoc />
    protected override void Up(Microsoft.EntityFrameworkCore.Migrations.MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE plt.schema_registry (
          subject text NOT NULL CHECK (length(subject) BETWEEN 1 AND 200),
          version integer NOT NULL CHECK (version >= 1),
          schema_json jsonb NOT NULL,
          schema_hash text NOT NULL CHECK (schema_hash ~ '^[0-9a-f]{64}$'),
          registered_by text NOT NULL,
          registered_at timestamptz NOT NULL,
          CONSTRAINT pk_schema_registry PRIMARY KEY (subject, version)
        );

        CREATE TABLE plt.message_exchange (
          exchange_id uuid PRIMARY KEY,
          legal_entity text NOT NULL,
          direction text NOT NULL CHECK (direction IN ('Inbound', 'Outbound')),
          counterparty text NOT NULL,
          party_ref text NULL,
          channel text NOT NULL,
          operation text NOT NULL,
          status text NOT NULL,
          correlation_id text NULL,
          started_at timestamptz NOT NULL,
          completed_at timestamptz NULL
        );
        CREATE INDEX ix_message_exchange_party ON plt.message_exchange (legal_entity, party_ref, started_at DESC);
        CREATE INDEX ix_message_exchange_started ON plt.message_exchange (legal_entity, started_at DESC, exchange_id DESC);

        CREATE TABLE plt.file_transfer (
          transfer_id uuid PRIMARY KEY,
          legal_entity text NOT NULL,
          direction text NOT NULL CHECK (direction IN ('Inbound', 'Outbound')),
          partner text NOT NULL,
          file_name text NOT NULL,
          status text NOT NULL CHECK (status IN ('Pending', 'Sent', 'Failed')),
          attempts integer NOT NULL DEFAULT 0 CHECK (attempts >= 0),
          last_error text NULL,
          created_at timestamptz NOT NULL,
          updated_at timestamptz NOT NULL
        );
        CREATE INDEX ix_file_transfer_list ON plt.file_transfer (legal_entity, created_at DESC, transfer_id DESC);
        """);

    /// <inheritdoc />
    protected override void Down(Microsoft.EntityFrameworkCore.Migrations.MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("DROP TABLE plt.file_transfer; DROP TABLE plt.message_exchange; DROP TABLE plt.schema_registry;");
}
