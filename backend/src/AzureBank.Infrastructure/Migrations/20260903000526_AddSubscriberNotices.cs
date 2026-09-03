using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzureBank.Infrastructure.Migrations
{
    /// <summary>
    /// Creates the table that records what an account holder is owed and has not yet been told
    /// (ADR-0045): today, the notice that a transfer PIN was enrolled.
    /// </summary>
    /// <remarks>
    /// It starts EMPTY and nothing seeds it. Nothing on AuditEvents is touched -- no column, no index,
    /// no data -- and there is deliberately no foreign key to AuditEvents: the audit row and the
    /// notice are written in the same save and joined by (ActorUserId, Event) when the notice is
    /// rendered, so that a notice whose evidence has gone missing is FOUND rather than refused. The
    /// one foreign key names the account holder and cascades with them, which is how a notice is
    /// erased: with its owner, never on its own. No address is stored; it is read from AspNetUsers
    /// at rendering time.
    ///
    /// ⚠️ Once this migration is on <c>main</c>, EVERY enrolment on a database that has not applied
    /// it fails: the notice insert is part of the enrolment's transaction, so the PIN is not set
    /// either (ADR-0044 D1, in the direction that protects the record). Apply it before running the
    /// API against a long-lived database.
    /// </remarks>
    public partial class AddSubscriberNotices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SubscriberNotices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Event = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DeliveredAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeliveryReceipt = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubscriberNotices", x => x.Id);
                    table.CheckConstraint("CK_SubscriberNotices_Delivery", "([DeliveredAt] IS NULL AND [DeliveryReceipt] IS NULL) OR ([DeliveredAt] IS NOT NULL AND [DeliveryReceipt] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_SubscriberNotices_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SubscriberNotices_Pending",
                table: "SubscriberNotices",
                column: "OccurredAt",
                filter: "[DeliveredAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SubscriberNotices_UserId",
                table: "SubscriberNotices",
                column: "UserId");
        }

        /// <inheritdoc />
        /// <remarks>
        /// WHAT DOWN COSTS. Dropping this table destroys the only record that a notice was owed and
        /// whether it was ever rendered; re-running Up restores nothing, and no notice can be
        /// re-derived from the audit row alone. Down is for a migration under which no notice has
        /// yet been written.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SubscriberNotices");
        }
    }
}
