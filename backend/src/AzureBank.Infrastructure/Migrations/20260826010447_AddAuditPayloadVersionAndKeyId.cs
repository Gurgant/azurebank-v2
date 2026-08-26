using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzureBank.Infrastructure.Migrations
{
    /// <summary>
    /// Records, per audit row, WHICH payload rendering and WHICH chain key wrote it.
    /// </summary>
    /// <remarks>
    /// WHY THIS EXISTS. The hashed payload used to carry a version prefix compiled into the hasher
    /// and no key identity at all, so verification recomputed every row with whatever the version and
    /// key are NOW. A payload change or a key rotation therefore rejected rows that were written
    /// correctly, reporting them the way it reports tampering. Rows can never be re-hashed once an
    /// external anchor certifies them, so this has to land before the first anchor exists rather than
    /// with the rotation that needs it.
    /// </remarks>
    public partial class AddAuditPayloadVersionAndKeyId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            /*
              KeyId IS DELIBERATELY LEFT NULL ON EVERY EXISTING ROW, and that is the only honest value
              available. Those rows were written under a payload that had no key-identity element, so
              nothing about which key wrote them was ever recorded or hashed. Writing the currently
              configured key's id onto them would be inventing a fact -- and it would be a fact
              outside their hashed payload, which is to say unfalsifiable. NULL means "no key identity
              was recorded"; the walk reads such rows under the founding key.
            */
            migrationBuilder.AddColumn<string>(
                name: "KeyId",
                table: "AuditEvents",
                type: "nchar(16)",
                fixedLength: true,
                maxLength: 16,
                nullable: true);

            /*
              THREE STATEMENTS FOR PayloadVersion, AND NO DEFAULT AT ANY POINT.

              The scaffolded version was one AddColumn with defaultValue: "" -- which fails twice.
              It backfills every historical row with the empty string, so a table that verified
              yesterday reports every row as an unrenderable scheme; and it leaves a DEFAULT
              constraint behind, which is precisely a way for an INSERT that omits the column to mint
              a row declaring a scheme nobody chose. The column has to be assigned by the writer that
              renders the payload, never by the database.

              So: add it nullable, state the true value for rows already written, then close it.
            */
            migrationBuilder.AddColumn<string>(
                name: "PayloadVersion",
                table: "AuditEvents",
                type: "nvarchar(8)",
                maxLength: 8,
                nullable: true);

            // 'v2' is provably what wrote them rather than a guess: the literal "v2" has been the
            // first element of the payload for every row any live table holds.
            migrationBuilder.Sql(
                "UPDATE [AuditEvents] SET [PayloadVersion] = N'v2' WHERE [PayloadVersion] IS NULL;");

            migrationBuilder.AlterColumn<string>(
                name: "PayloadVersion",
                table: "AuditEvents",
                type: "nvarchar(8)",
                maxLength: 8,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(8)",
                oldMaxLength: 8,
                oldNullable: true);
        }

        /// <inheritdoc />
        /// <remarks>
        /// WHAT DOWN COSTS, stated because it is not recoverable by re-running Up. Every row written
        /// after this migration hashes its own version and key identity, so dropping these columns
        /// leaves those rows unverifiable: the payload they were hashed over contains two values the
        /// schema no longer holds, and re-adding the columns cannot restore which key wrote them.
        /// Down is for a migration that has not yet had a row written under it.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "KeyId",
                table: "AuditEvents");

            migrationBuilder.DropColumn(
                name: "PayloadVersion",
                table: "AuditEvents");
        }
    }
}
