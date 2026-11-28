using Microsoft.EntityFrameworkCore.Infrastructure;

namespace CoreIns.Platform.Persistence.Migrations;

/// <summary>BO-PLT-05: user directory and role assignments (hand-written; runtime access is SQL in <c>UserDirectoryStore</c>).</summary>
[DbContext(typeof(PlatformDbContext))]
[Microsoft.EntityFrameworkCore.Migrations.Migration("20261128100000_UserDirectoryTables")]
public sealed class UserDirectoryTables : Microsoft.EntityFrameworkCore.Migrations.Migration
{
    /// <inheritdoc />
    protected override void Up(Microsoft.EntityFrameworkCore.Migrations.MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE plt.user_profile (
          user_id uuid PRIMARY KEY,
          legal_entity text NOT NULL,
          entra_oid text NOT NULL CHECK (length(entra_oid) BETWEEN 1 AND 200),
          display_name text NOT NULL CHECK (length(display_name) BETWEEN 1 AND 200),
          email text NULL,
          language text NOT NULL DEFAULT 'el' CHECK (language IN ('el', 'en')),
          status text NOT NULL DEFAULT 'Active' CHECK (status IN ('Active', 'Suspended', 'Disabled')),
          status_reason text NULL,
          created_by text NOT NULL,
          created_at timestamptz NOT NULL,
          updated_at timestamptz NOT NULL,
          last_seen_at timestamptz NULL,
          CONSTRAINT uq_user_profile_oid UNIQUE (legal_entity, entra_oid)
        );
        CREATE INDEX ix_user_profile_search ON plt.user_profile (legal_entity, lower(display_name));

        CREATE TABLE plt.role_assignment (
          assignment_id uuid PRIMARY KEY,
          user_id uuid NOT NULL REFERENCES plt.user_profile (user_id),
          legal_entity text NOT NULL,
          role text NOT NULL,
          scope text NULL,
          valid_from timestamptz NULL,
          valid_to timestamptz NULL,
          status text NOT NULL CHECK (status IN ('Active', 'Revoked')),
          granted_by text NOT NULL,
          approved_by text NULL,
          approval_request_id uuid NULL,
          granted_at timestamptz NOT NULL,
          revoked_at timestamptz NULL,
          revoked_by text NULL,
          revoke_reason text NULL
        );
        CREATE UNIQUE INDEX uq_role_assignment_active ON plt.role_assignment (user_id, role, coalesce(scope, '')) WHERE status = 'Active';
        CREATE UNIQUE INDEX uq_role_assignment_approval ON plt.role_assignment (approval_request_id) WHERE approval_request_id IS NOT NULL;
        CREATE INDEX ix_role_assignment_user ON plt.role_assignment (user_id);
        """);

    /// <inheritdoc />
    protected override void Down(Microsoft.EntityFrameworkCore.Migrations.MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("DROP TABLE plt.role_assignment; DROP TABLE plt.user_profile;");
}
