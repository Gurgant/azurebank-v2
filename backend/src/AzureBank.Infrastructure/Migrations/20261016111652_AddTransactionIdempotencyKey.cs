using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzureBank.Infrastructure.Migrations
{
    /// <summary>
    /// Gives a ledger row the <c>Idempotency-Key</c> of the money request that wrote it, on the
    /// caller's own rows, so that a client whose answer was lost can find the movement with
    /// <c>GET /api/transactions?IdempotencyKey=</c> (ADR-0009, ADR-0022).
    /// </summary>
    /// <remarks>
    /// NULLABLE, AND NO BACKFILL. A row written before this migration names no request, and nothing
    /// could name it now: an idempotency record holds no link to the rows its request wrote, and is
    /// swept after 24 h. The demo's seeded history was written by no request. Nullable is also what
    /// lets the app of before run on this schema: on Azure the migration runs before the app moves,
    /// a rollout that fails puts the app of before back, and the schema is never put back.
    ///
    /// The index is NOT unique and is FILTERED: a move between the caller's own accounts writes two
    /// rows under one key, and the listing never asks for a row without one.
    ///
    /// Once this migration is on <c>main</c>, a request that writes or reads the ledger fails on a
    /// database that has not applied it: its statement names a column the table does not have.
    /// Apply it before running the API against an existing database.
    /// </remarks>
    public partial class AddTransactionIdempotencyKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "IdempotencyKey",
                table: "Transactions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_IdempotencyKey",
                table: "Transactions",
                column: "IdempotencyKey",
                filter: "[IdempotencyKey] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Transactions_IdempotencyKey",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                table: "Transactions");
        }
    }
}
