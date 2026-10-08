using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DevClock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "dev_clock",
                schema: "plt",
                columns: table => new
                {
                    id = table.Column<short>(type: "smallint", nullable: false),
                    offset_micros = table.Column<long>(type: "bigint", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_dev_clock", x => x.id);
                    table.CheckConstraint("ck_dev_clock_offset", "offset_micros BETWEEN 0 AND 3153600000000000");
                    table.CheckConstraint("ck_dev_clock_single_row", "id = 1");
                    table.CheckConstraint("ck_dev_clock_version", "version >= 0");
                });

            // Hand-written part (D-SL3-12): the one row, and the database-level guarantee that the dev clock only moves forward.
            migrationBuilder.Sql("""
                INSERT INTO plt.dev_clock (id, offset_micros, version, updated_at) VALUES (1, 0, 0, now());

                CREATE FUNCTION plt.dev_clock_forward_only() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  IF TG_OP <> 'UPDATE' THEN
                    RAISE EXCEPTION 'plt.dev_clock is a single row and cannot be deleted or truncated' USING ERRCODE = 'check_violation';
                  END IF;
                  IF NEW.offset_micros < OLD.offset_micros THEN
                    RAISE EXCEPTION 'the dev clock only moves forward' USING ERRCODE = 'check_violation';
                  END IF;
                  RETURN NEW;
                END
                $$;
                CREATE TRIGGER dev_clock_forward_only BEFORE UPDATE OR DELETE ON plt.dev_clock
                  FOR EACH ROW EXECUTE FUNCTION plt.dev_clock_forward_only();
                CREATE TRIGGER dev_clock_no_truncate BEFORE TRUNCATE ON plt.dev_clock
                  FOR EACH STATEMENT EXECUTE FUNCTION plt.dev_clock_forward_only();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "dev_clock",
                schema: "plt");
            migrationBuilder.Sql("DROP FUNCTION plt.dev_clock_forward_only();");
        }
    }
}
