using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzureBank.Infrastructure.Migrations
{
    /// <summary>
    /// Gives a notice the identity of the audit row it belongs to (ADR-0052), so the evidence check
    /// can ask about THIS notice instead of asking whether the user has ever done this kind of thing.
    /// </summary>
    /// <remarks>
    /// WHY IT REOPENS SOMETHING. AddSubscriberNotices wrote that the two rows are "joined by
    /// (ActorUserId, Event) when the notice is rendered", and that was exact while every kind
    /// happened once per account. ADR-0047 made PinChanged repeatable, and from that moment one
    /// surviving audit row answered for every change notice a user had, while a missing one raised
    /// nothing. ADR-0052 takes the decision that migration's remarks implied was settled.
    ///
    /// STILL NO FOREIGN KEY TO AuditEvents, and the earlier sentence survives intact: a notice whose
    /// evidence has gone missing must be FOUND rather than refused, and a constraint would refuse the
    /// write or the delete that made it missing. Nothing on AuditEvents is touched here either -- no
    /// column, no index, no data. What changes is that the notice now NAMES the row, and naming is
    /// not constraining.
    ///
    /// NULLABLE, AND THE BACKLOG IS THE REASON. Every row written before this migration has no
    /// reference, and the check falls back to the old (ActorUserId, Event) question for them. Without
    /// that fallback the first run after deployment would report the entire existing backlog as
    /// missing its evidence -- a detective control turned into noise on the day it shipped.
    ///
    /// The index is FILTERED to the rows that have one, because the exact query never asks about the
    /// others and a filtered index does not carry them.
    ///
    /// ⚠️ Once this migration is on <c>main</c>, EVERY enrolment and PIN change on a database that
    /// has not applied it fails: the notice insert names a column the table does not have, and that
    /// insert is part of the same transaction, so the PIN is not set either. Apply it before running
    /// the API against an existing database.
    /// </remarks>
    public partial class AddSubscriberNoticeAuditEventId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AuditEventId",
                table: "SubscriberNotices",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SubscriberNotices_AuditEventId",
                table: "SubscriberNotices",
                column: "AuditEventId",
                filter: "[AuditEventId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SubscriberNotices_AuditEventId",
                table: "SubscriberNotices");

            migrationBuilder.DropColumn(
                name: "AuditEventId",
                table: "SubscriberNotices");
        }
    }
}
