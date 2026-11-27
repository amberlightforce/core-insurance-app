using Microsoft.EntityFrameworkCore.Infrastructure;

namespace CoreIns.Platform.Persistence.Migrations;

/// <summary>BO-PLT-02: administrator-owned catalogue of approval types (hand-written; runtime access is SQL in <c>ApprovalTypeRegister</c>).</summary>
[DbContext(typeof(PlatformDbContext))]
[Microsoft.EntityFrameworkCore.Migrations.Migration("20261127100000_ApprovalTypeRegister")]
public sealed class ApprovalTypeRegister : Microsoft.EntityFrameworkCore.Migrations.Migration
{
    /// <inheritdoc />
    protected override void Up(Microsoft.EntityFrameworkCore.Migrations.MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE plt.approval_type (
          approval_type text PRIMARY KEY CHECK (approval_type ~ '^[A-Z]{2,3}\.[A-Za-z0-9_]{2,64}$'),
          description text NOT NULL,
          default_referral_role text NULL,
          default_authority_type text NULL,
          version integer NOT NULL CHECK (version >= 1),
          registered_by_kind text NOT NULL,
          registered_by_id text NOT NULL,
          updated_at timestamptz NOT NULL
        );
        """);

    /// <inheritdoc />
    protected override void Down(Microsoft.EntityFrameworkCore.Migrations.MigrationBuilder migrationBuilder) => migrationBuilder.Sql("DROP TABLE plt.approval_type;");
}
