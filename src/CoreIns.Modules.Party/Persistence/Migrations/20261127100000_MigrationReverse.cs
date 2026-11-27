using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Party.Persistence.Migrations
{
    /// <summary>
    /// BO-MIG-06: reversal of migrated parties (D-SL6-05). The app role has no DELETE grant on pty tables, so a SECURITY DEFINER function
    /// (runs as the table owner) removes one migrated party (with its child rows) or one migrated contact point, only while it has no
    /// post-migration activity, under the row lock. No table changes: the model snapshot is unchanged.
    /// </summary>
    [DbContext(typeof(PartyDbContext))]
    [Migration("20261127100000_MigrationReverse")]
    public partial class MigrationReverse : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE FUNCTION pty.reverse_migrated(p_kind text, p_batch uuid, p_id uuid) RETURNS boolean
                    LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pty AS $$
                BEGIN
                    IF p_kind = 'PARTY' THEN
                        PERFORM 1 FROM pty.party
                            WHERE party_id = p_id AND source_channel LIKE 'MIGRATION:' || p_batch::text || ':%' AND record_version = 1 FOR UPDATE;
                        IF NOT FOUND THEN RETURN false; END IF;
                        IF EXISTS (SELECT 1 FROM pty.dsar_restriction WHERE party_id = p_id)
                           OR EXISTS (SELECT 1 FROM pty.intermediary WHERE party_id = p_id) THEN
                            RETURN false;
                        END IF;
                        DELETE FROM pty.party_search_key WHERE party_id = p_id;
                        DELETE FROM pty.party_name WHERE party_id = p_id;
                        DELETE FROM pty.party_identifier WHERE party_id = p_id;
                        DELETE FROM pty.party_address WHERE party_id = p_id;
                        DELETE FROM pty.party_contact_point WHERE party_id = p_id;
                        DELETE FROM pty.vulnerability WHERE party_id = p_id;
                        DELETE FROM pty.party WHERE party_id = p_id;
                        RETURN true;
                    ELSIF p_kind = 'CONTACT' THEN
                        PERFORM 1 FROM pty.party_contact_point
                            WHERE contact_point_id = p_id AND source = 'MIGRATION:' || p_batch::text AND recorded_to IS NULL FOR UPDATE;
                        IF NOT FOUND THEN RETURN false; END IF;
                        DELETE FROM pty.party_contact_point WHERE contact_point_id = p_id;
                        RETURN true;
                    END IF;
                    RETURN false;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS pty.reverse_migrated(text, uuid, uuid);");
    }
}
