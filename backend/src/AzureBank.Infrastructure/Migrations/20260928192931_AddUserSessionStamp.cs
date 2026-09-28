using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzureBank.Infrastructure.Migrations
{
    /// <summary>
    /// Gives every user a <c>SessionStamp</c>, a counter every per-user sign-out raises by 1
    /// (ADR-0057 §5.3).
    /// </summary>
    /// <remarks>
    /// Its own column, not Identity's <c>SecurityStamp</c>: Identity rewrites that one on a password
    /// or two-factor change, and neither is a sign-out.
    ///
    /// Every existing user starts at 0, the value a new registration gets. The BFF holds its sessions
    /// in memory and restarts with this deployment, so no session carries an older value to compare.
    /// </remarks>
    public partial class AddUserSessionStamp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SessionStamp",
                table: "AspNetUsers",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SessionStamp",
                table: "AspNetUsers");
        }
    }
}
