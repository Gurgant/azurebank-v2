using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzureBank.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUniqueEmailIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Guard: a UNIQUE index cannot be built if duplicate NormalizedEmail values already
            // exist (they could have accumulated while the old EmailIndex was non-unique and
            // RequireUniqueEmail was only an in-process advisory). On this project every deploy
            // migrates a FRESH schema (the Seeder reset drops + re-migrates; tests start clean),
            // so this is a no-op in practice — but it turns SQL Server's opaque duplicate-key
            // abort (error 1505) into an actionable message for any hand-restored database. We do
            // NOT auto-delete or merge rows on an identity table: resolving duplicates is a
            // manual operator decision.
            migrationBuilder.Sql("""
                IF EXISTS (
                    SELECT 1 FROM [AspNetUsers]
                    WHERE [NormalizedEmail] IS NOT NULL
                    GROUP BY [NormalizedEmail]
                    HAVING COUNT(*) > 1)
                BEGIN
                    THROW 50000, 'AddUniqueEmailIndex aborted: duplicate NormalizedEmail values exist. Resolve them before applying (SELECT NormalizedEmail, COUNT(*) FROM AspNetUsers GROUP BY NormalizedEmail HAVING COUNT(*) > 1;).', 1;
                END
                """);

            migrationBuilder.DropIndex(
                name: "EmailIndex",
                table: "AspNetUsers");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "AspNetUsers",
                column: "NormalizedEmail",
                unique: true,
                filter: "[NormalizedEmail] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "EmailIndex",
                table: "AspNetUsers");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "AspNetUsers",
                column: "NormalizedEmail");
        }
    }
}
