using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzureBank.Infrastructure.Migrations
{
    /// <summary>
    /// Widens <c>Transactions.TransactionNumber</c> from 20 to 24 characters so the generated
    /// number can carry a 10-character suffix and a trailing check symbol. The old format filled
    /// the column exactly, which is why this could not be done in place.
    ///
    /// <para>
    /// Existing rows are untouched and keep their older, shorter numbers — 19 characters before
    /// PR #89, 20 between #89 and this change — and the column only gets wider, so nothing is
    /// truncated and no backfill is needed. EF scaffolds this as a plain <c>AlterColumn</c>, and the
    /// emitted SQL was inspected rather than assumed: because the column carries
    /// <c>IX_Transactions_TransactionNumber</c>, EF Core drops that unique index, runs the ALTER,
    /// and recreates it. That is EF Core's unconditional pattern for an <c>AlterColumn</c> on an
    /// indexed column, NOT a SQL Server requirement — probed on the same LocalDB engine, SQL Server
    /// widens an nvarchar index key in place when the type is unchanged and the new size is no
    /// smaller. Recorded that way round because the earlier wording asserted the constraint as if it
    /// had been measured, and only the emitted script had been.
    /// </para>
    /// <para>
    /// <b>Down is genuinely lossy and will fail rather than truncate.</b> Narrowing back to 20 with
    /// any post-migration number present raises a truncation error; that is the correct behaviour
    /// (silently cutting four characters off a transaction identifier would be worse) but it means
    /// this migration is not freely reversible once new numbers have been written.
    /// </para>
    /// </summary>
    public partial class WidenTransactionNumberForCheckSymbol : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "TransactionNumber",
                table: "Transactions",
                type: "nvarchar(24)",
                maxLength: 24,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(20)",
                oldMaxLength: 20);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "TransactionNumber",
                table: "Transactions",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(24)",
                oldMaxLength: 24);
        }
    }
}
