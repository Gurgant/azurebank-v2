using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzureBank.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DecoupleUserNameFromAzureTag : Migration
    {
        // Data-only migration (no schema change): decouple Identity's UserName from the
        // public AzureTag handle (ADR-0015). New rows already get UserName = the user id at
        // registration; this backfills existing rows. Idempotent — re-running sets the same
        // id-derived value.
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE [AspNetUsers]
                SET [UserName] = LOWER(CONVERT(nvarchar(36), [Id])),
                    [NormalizedUserName] = UPPER(CONVERT(nvarchar(36), [Id]));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The AzureTag column is unchanged, so the old coupling can be restored exactly.
            migrationBuilder.Sql(
                """
                UPDATE [AspNetUsers]
                SET [UserName] = [AzureTag],
                    [NormalizedUserName] = UPPER([AzureTag]);
                """);
        }
    }
}
