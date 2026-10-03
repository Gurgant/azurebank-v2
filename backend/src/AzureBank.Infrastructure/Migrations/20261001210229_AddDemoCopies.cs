using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzureBank.Infrastructure.Migrations
{
    /// <summary>
    /// Adds the demo's pool: the table <c>DemoCopies</c>, one row per prepared copy, and the nullable
    /// column <c>AspNetUsers.DemoCopyId</c> that says which copy a user belongs to.
    /// </summary>
    /// <remarks>
    /// Nothing changes for a deployment that is not the demo: the table holds no row, the column is
    /// null on every existing and every new user, and the filtered index on it holds no row.
    ///
    /// The key goes from the user to the copy and takes no action on delete, so the database refuses
    /// to delete a copy's row while a user still points at it. <c>DemoCopies.OwnerUserId</c> has no
    /// foreign key: the row of a claimed copy stays after its users are deleted.
    ///
    /// The two CHECK constraints keep a claim whole (<c>ClaimedAt</c> and <c>ClaimId</c> set together
    /// or not at all) and allow <c>DeletedAt</c> only on a copy somebody claimed.
    /// </remarks>
    public partial class AddDemoCopies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DemoCopyId",
                table: "AspNetUsers",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DemoCopies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ClaimedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ClaimId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ClientKey = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: true),
                    Writes = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    DeletedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DemoCopies", x => x.Id);
                    table.CheckConstraint("CK_DemoCopies_ClaimIsWhole", "([ClaimedAt] IS NULL AND [ClaimId] IS NULL) OR ([ClaimedAt] IS NOT NULL AND [ClaimId] IS NOT NULL)");
                    table.CheckConstraint("CK_DemoCopies_DeletedWasClaimed", "[DeletedAt] IS NULL OR [ClaimedAt] IS NOT NULL");
                });

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_DemoCopyId",
                table: "AspNetUsers",
                column: "DemoCopyId",
                filter: "[DemoCopyId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DemoCopies_ClaimId",
                table: "DemoCopies",
                column: "ClaimId",
                unique: true,
                filter: "[ClaimId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DemoCopies_ClientKey",
                table: "DemoCopies",
                columns: new[] { "ClientKey", "ClaimedAt" },
                filter: "[ClientKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DemoCopies_Free",
                table: "DemoCopies",
                column: "CreatedAt",
                filter: "[ClaimedAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DemoCopies_OwnerUserId",
                table: "DemoCopies",
                column: "OwnerUserId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_AspNetUsers_DemoCopies_DemoCopyId",
                table: "AspNetUsers",
                column: "DemoCopyId",
                principalTable: "DemoCopies",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AspNetUsers_DemoCopies_DemoCopyId",
                table: "AspNetUsers");

            migrationBuilder.DropTable(
                name: "DemoCopies");

            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_DemoCopyId",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "DemoCopyId",
                table: "AspNetUsers");
        }
    }
}
