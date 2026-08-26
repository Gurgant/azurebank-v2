using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzureBank.Infrastructure.Migrations
{
    /// <summary>
    /// Creates the table that records what the audit chain looked like each time somebody ran the
    /// verifier.
    /// </summary>
    /// <remarks>
    /// It starts EMPTY and nothing seeds a genesis record: an empty table is a meaningful state,
    /// meaning nobody has ever run the anchor mode, and a synthetic first row would be a claim about
    /// a moment that never happened. Nothing on AuditEvents is touched -- no column, no index, no
    /// data -- and there is deliberately no foreign key from CoveredThroughSequence to
    /// AuditEvents.Sequence: an anchor must be able to outlive the rows it names, because that
    /// mismatch is the evidence.
    /// </remarks>
    public partial class AddAuditAnchors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditAnchors",
                columns: table => new
                {
                    AnchorSequence = table.Column<long>(type: "bigint", nullable: false),
                    PayloadVersion = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    AnchorKeyId = table.Column<string>(type: "nchar(16)", fixedLength: true, maxLength: 16, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    LowestCoveredSequence = table.Column<long>(type: "bigint", nullable: true),
                    CoveredThroughSequence = table.Column<long>(type: "bigint", nullable: true),
                    CoveredRowCount = table.Column<long>(type: "bigint", nullable: true),
                    TailRowHash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: true),
                    VerifiedUnderChainKeyId = table.Column<string>(type: "nchar(16)", fixedLength: true, maxLength: 16, nullable: false),
                    PreviousAnchorPayloadHash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: true),
                    AnchoredValue = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: true),
                    PayloadHash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    Mac = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditAnchors", x => x.AnchorSequence);
                    table.CheckConstraint("CK_AuditAnchors_FirstLink", "([AnchorSequence] = 1 AND [PreviousAnchorPayloadHash] IS NULL) OR ([AnchorSequence] > 1 AND [PreviousAnchorPayloadHash] IS NOT NULL)");
                    table.CheckConstraint("CK_AuditAnchors_Range", "[AnchorSequence] >= 1 AND ([CoveredRowCount] IS NULL OR ([CoveredRowCount] >= 1 AND [LowestCoveredSequence] <= [CoveredThroughSequence]))");
                    table.CheckConstraint("CK_AuditAnchors_Shape", "([Kind] = 'Anchor' AND [LowestCoveredSequence] IS NOT NULL AND [CoveredThroughSequence] IS NOT NULL AND [CoveredRowCount] IS NOT NULL AND [TailRowHash] IS NOT NULL AND [AnchoredValue] IS NOT NULL) OR ([Kind] = 'GapMarker' AND [LowestCoveredSequence] IS NULL AND [CoveredThroughSequence] IS NULL AND [CoveredRowCount] IS NULL AND [TailRowHash] IS NULL AND [AnchoredValue] IS NULL)");
                });
        }

        /// <inheritdoc />
        /// <remarks>
        /// WHAT DOWN COSTS. Dropping this table destroys the only records of what the chain looked
        /// like at those instants, and re-running Up restores nothing -- the records cannot be
        /// recomputed, because each one names a moment that has passed. Down is for a migration
        /// under which no anchor has yet been written.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditAnchors");
        }
    }
}
