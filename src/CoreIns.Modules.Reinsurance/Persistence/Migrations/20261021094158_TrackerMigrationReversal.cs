using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Reinsurance.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TrackerMigrationReversal : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "batch_id",
                schema: "ri",
                table: "tracker",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_tracker_batch",
                schema: "ri",
                table: "tracker",
                column: "batch_id");

            // Reversal of a migrated tracker (D-SL6-05). The tracker stays append-only for everyone but ri.reverse_migrated_tracker:
            // UPDATE is always refused, DELETE only inside that SECURITY DEFINER function (current_user = the owner, session_user = the
            // caller), which deletes a row of the given batch only. The truncate seal is unchanged.
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS tr_tracker_seal ON ri.tracker;

                CREATE FUNCTION ri.tracker_seal() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'DELETE' AND current_user <> session_user AND current_setting('ri.tracker_reversal', true) = 'on' THEN
                        RETURN OLD;
                    END IF;
                    RAISE EXCEPTION 'ri.% rows are append-only (REQ-RI-065, PITFALLS 17)', TG_TABLE_NAME USING ERRCODE = '42501';
                END
                $$;

                CREATE TRIGGER tr_tracker_seal BEFORE UPDATE OR DELETE ON ri.tracker FOR EACH ROW EXECUTE FUNCTION ri.tracker_seal();

                CREATE FUNCTION ri.reverse_migrated_tracker(p_batch uuid, p_id uuid) RETURNS boolean
                    LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, ri AS $$
                BEGIN
                    PERFORM 1 FROM ri.tracker WHERE tracker_id = p_id AND batch_id = p_batch FOR UPDATE;
                    IF NOT FOUND THEN RETURN false; END IF;
                    PERFORM set_config('ri.tracker_reversal', 'on', true);
                    DELETE FROM ri.tracker WHERE tracker_id = p_id AND batch_id = p_batch;
                    PERFORM set_config('ri.tracker_reversal', 'off', true);
                    RETURN true;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP FUNCTION IF EXISTS ri.reverse_migrated_tracker(uuid, uuid);
                DROP TRIGGER IF EXISTS tr_tracker_seal ON ri.tracker;
                DROP FUNCTION IF EXISTS ri.tracker_seal();
                CREATE TRIGGER tr_tracker_seal BEFORE UPDATE OR DELETE ON ri.tracker FOR EACH ROW EXECUTE FUNCTION ri.content_append_only();
                """);

            migrationBuilder.DropIndex(
                name: "ix_tracker_batch",
                schema: "ri",
                table: "tracker");

            migrationBuilder.DropColumn(
                name: "batch_id",
                schema: "ri",
                table: "tracker");
        }
    }
}
