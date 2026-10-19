using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CoreIns.Platform.Persistence.Migrations;

/// <summary>SL6-PLT-AI: AI feature registry, per-feature toggles, kill switch rows and AI interaction log (hand-written; runtime access is set-based SQL).</summary>
[DbContext(typeof(PlatformDbContext))]
[Migration("20261019100000_AiControlPlane")]
public sealed class AiControlPlane : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE plt.ai_feature (
          feature_id text PRIMARY KEY CHECK (feature_id ~ '^AI-[A-Z]{2,3}-[0-9]{2,}$'),
          register_status text NULL,
          register_status_at timestamptz NULL,
          created_at timestamptz NOT NULL
        );
        CREATE TABLE plt.ai_toggle (
          feature_id text PRIMARY KEY REFERENCES plt.ai_feature (feature_id),
          enabled boolean NOT NULL,
          version bigint NOT NULL CHECK (version >= 1),
          reason text NULL,
          updated_by_kind text NOT NULL,
          updated_by_id text NOT NULL,
          updated_at timestamptz NOT NULL
        );
        CREATE TABLE plt.ai_kill_switch (
          scope text PRIMARY KEY CHECK (scope = 'GLOBAL' OR scope ~ '^AI-[A-Z]{2,3}-[0-9]{2,}$'),
          active boolean NOT NULL,
          version bigint NOT NULL CHECK (version >= 1),
          reason text NULL,
          updated_by_kind text NOT NULL,
          updated_by_id text NOT NULL,
          updated_at timestamptz NOT NULL
        );
        CREATE TABLE plt.ai_interaction (
          interaction_id uuid PRIMARY KEY,
          feature_id text NOT NULL,
          legal_entity text NULL,
          actor_kind text NOT NULL,
          actor_id text NOT NULL,
          status text NOT NULL CHECK (status IN ('Suggested', 'Discarded', 'Refused')),
          refusal_reason text NULL,
          inputs jsonb NOT NULL,
          output jsonb NULL,
          outcome text NULL CHECK (outcome IN ('Accepted', 'Rejected', 'Modified')),
          outcome_comment text NULL,
          outcome_by_kind text NULL,
          outcome_by_id text NULL,
          outcome_at timestamptz NULL,
          created_at timestamptz NOT NULL
        );
        CREATE INDEX ix_ai_interaction_feature ON plt.ai_interaction (feature_id, created_at);
        """);

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(
        "DROP TABLE plt.ai_interaction; DROP TABLE plt.ai_kill_switch; DROP TABLE plt.ai_toggle; DROP TABLE plt.ai_feature;");
}
