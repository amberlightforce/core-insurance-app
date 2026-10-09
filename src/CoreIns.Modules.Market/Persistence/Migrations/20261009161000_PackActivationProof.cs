using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace CoreIns.Modules.Market.Persistence.Migrations;
/// <summary>Adds frozen request and execution proof while preserving historical genesis activations.</summary>
[DbContext(typeof(MarketDbContext))]
[Migration("20261009161000_PackActivationProof")]
public sealed class PackActivationProof : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "from_version", schema: "mkt", table: "pack_activation", type: "character varying(32)", nullable: true);
        migrationBuilder.AddColumn<string>(name: "parent_hash", schema: "mkt", table: "pack_activation", type: "char(64)", nullable: true);
        migrationBuilder.AddColumn<string>(name: "target_digest", schema: "mkt", table: "pack_activation", type: "char(64)", nullable: true);
        migrationBuilder.AddColumn<string>(name: "reason", schema: "mkt", table: "pack_activation", type: "character varying(128)", nullable: false, defaultValue: "");
        migrationBuilder.AddColumn<string>(name: "requested_principal", schema: "mkt", table: "pack_activation", type: "character varying(200)", nullable: true);
        migrationBuilder.AddColumn<string>(name: "content_hash", schema: "mkt", table: "pack_activation", type: "char(64)", nullable: true);
        migrationBuilder.AddColumn<long>(name: "record_version", schema: "mkt", table: "pack_activation", type: "bigint", nullable: false, defaultValue: 1L);
        migrationBuilder.AddColumn<DateTime>(name: "window_from", schema: "mkt", table: "pack_activation", type: "timestamptz", nullable: true);
        migrationBuilder.AddColumn<DateTime>(name: "window_to", schema: "mkt", table: "pack_activation", type: "timestamptz", nullable: true);
        migrationBuilder.AddColumn<string[]>(name: "hashes_issued", schema: "mkt", table: "pack_activation", type: "text[]", nullable: false, defaultValueSql: "ARRAY[]::text[]");
        migrationBuilder.AddColumn<string>(name: "decision_reason", schema: "mkt", table: "pack_activation", type: "character varying(128)", nullable: true);
        migrationBuilder.Sql(PackActivationSql.Up);
    }
    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(PackActivationSql.Down);
        migrationBuilder.DropColumn(name: "decision_reason", schema: "mkt", table: "pack_activation");
        migrationBuilder.DropColumn(name: "hashes_issued", schema: "mkt", table: "pack_activation");
        migrationBuilder.DropColumn(name: "window_to", schema: "mkt", table: "pack_activation");
        migrationBuilder.DropColumn(name: "window_from", schema: "mkt", table: "pack_activation");
        migrationBuilder.DropColumn(name: "record_version", schema: "mkt", table: "pack_activation");
        migrationBuilder.DropColumn(name: "content_hash", schema: "mkt", table: "pack_activation");
        migrationBuilder.DropColumn(name: "requested_principal", schema: "mkt", table: "pack_activation");
        migrationBuilder.DropColumn(name: "reason", schema: "mkt", table: "pack_activation");
        migrationBuilder.DropColumn(name: "target_digest", schema: "mkt", table: "pack_activation");
        migrationBuilder.DropColumn(name: "parent_hash", schema: "mkt", table: "pack_activation");
        migrationBuilder.DropColumn(name: "from_version", schema: "mkt", table: "pack_activation");
    }
}
