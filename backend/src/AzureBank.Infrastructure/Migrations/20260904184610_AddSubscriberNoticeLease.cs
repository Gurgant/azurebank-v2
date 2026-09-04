using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzureBank.Infrastructure.Migrations
{
    /// <summary>
    /// Two nullable columns on <c>SubscriberNotices</c> so a runner can hold a row while it delivers
    /// it (ADR-0048): <c>LeasedUntil</c> and <c>LeasedBy</c>, paired by a CHECK constraint the same
    /// way <c>DeliveredAt</c> and <c>DeliveryReceipt</c> already are.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Touches nothing else: no existing column changes, the filtered pending index stays as it is
    /// (a claim can use it; the lease predicate is filtered after), the concurrency token stays
    /// <c>DeliveredAt</c>. Both columns are
    /// nullable, so this is a pure expand against a populated table.
    /// </para>
    /// <para>
    /// The claim is a set-based UPDATE that takes owed rows whose lease is null or expired; it stops
    /// two runners holding one row at once and nothing more. A runner that delivers and dies before
    /// marking is succeeded after its lease lapses; with a sending transport that row goes out
    /// again, with the pickup directory the second attempt is refused and the row stays owed beside
    /// its file — at-least-once, said in the ADR rather than implied away by a column.
    /// </para>
    /// </remarks>
    public partial class AddSubscriberNoticeLease : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LeasedBy",
                table: "SubscriberNotices",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LeasedUntil",
                table: "SubscriberNotices",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_SubscriberNotices_Lease",
                table: "SubscriberNotices",
                sql: "([LeasedUntil] IS NULL AND [LeasedBy] IS NULL) OR ([LeasedUntil] IS NOT NULL AND [LeasedBy] IS NOT NULL)");
        }

        /// <summary>Drops the constraint and both columns.</summary>
        /// <remarks>
        /// What Down costs: any lease in flight, which is nothing durable — a runner that was holding
        /// a row simply finds it owed again. No delivered notice, receipt or mark is touched. But a
        /// relay still RUNNING the new build queries these columns every period and fails at Error
        /// once they are gone: set <c>Notices:Runner=None</c>, or deploy the previous build, before
        /// running Down.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_SubscriberNotices_Lease",
                table: "SubscriberNotices");

            migrationBuilder.DropColumn(
                name: "LeasedBy",
                table: "SubscriberNotices");

            migrationBuilder.DropColumn(
                name: "LeasedUntil",
                table: "SubscriberNotices");
        }
    }
}
