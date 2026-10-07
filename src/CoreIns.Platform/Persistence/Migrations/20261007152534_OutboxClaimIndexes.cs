using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Platform.Persistence.Migrations
{
    /// <summary>
    /// F-1b review M2: partial indexes for the claim's blocked-aggregate lookup (pending events in backoff and pending
    /// events under a live lease), so a claim never evaluates the whole pending backlog.
    /// </summary>
    public partial class OutboxClaimIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_outbox_message_pending_lease",
                schema: "plt",
                table: "outbox_message",
                column: "lease_until",
                filter: "status = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_message_pending_next_attempt",
                schema: "plt",
                table: "outbox_message",
                column: "next_attempt_at",
                filter: "status = 'Pending'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_outbox_message_pending_lease",
                schema: "plt",
                table: "outbox_message");

            migrationBuilder.DropIndex(
                name: "ix_outbox_message_pending_next_attempt",
                schema: "plt",
                table: "outbox_message");
        }
    }
}
