using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzureBank.Infrastructure.Migrations
{
    /// <summary>
    /// Gives a revoked grant its reason (06 §4.1), and revokes every grant still active as
    /// <c>Deployment</c>.
    /// </summary>
    /// <remarks>
    /// THE REASON DECIDES WHAT A LATER RENEWAL MEANS. Only a grant revoked because its session ENDED
    /// makes a renewal received after the revoke a tripwire; every other reason is a lever pulled
    /// from outside the session, which a live session can learn of late and innocently. So the column
    /// exists before any code writes a revoke, and a row with no reason is a legacy one.
    ///
    /// WHY THE ACTIVE ROWS ARE REVOKED, and not left to expire. They were issued for seven days and
    /// slid forward on every rotation; the grant now lives sixty minutes from sign-in and is never
    /// extended. Left active, a legacy row would renew for up to a week under the new code, which
    /// reads a grant instead of rotating it. The deployment this is written for restarts the BFF and
    /// the API together (one app with a sidecar, 06 §3), and the BFF holds its sessions in
    /// memory, so no live session holds one of these rows by then. A deployment that restarted only
    /// the API would sign each such session out once, at its next renewal.
    ///
    /// SYSUTCDATETIME() for RevokedAt, not a request's ReceivedAt stamp, because there is no request.
    /// No comparison reads it: the tripwire compares stamps only on <c>SessionEnded</c> rows.
    ///
    /// ⚠️ A PRECONDITION FOR ORDER, which ADR-0057 records (06 F14): this must run before any
    /// environment holds live sessions across a deployment. The previous revision reads a revoked
    /// row without a successor as reuse and revokes every token of its user, so during a rolling
    /// swap every row revoked here would be read as theft by the old code.
    ///
    /// The CHECK constraint refuses a reason the enum does not have: the runbook writes this column
    /// by hand (06 §5), and EF could not read such a row back.
    ///
    /// Down drops the constraint and the column and does NOT un-revoke the rows Up revoked: their
    /// sessions are gone, and a grant that came back to life would outlive any session it belonged to.
    /// </remarks>
    public partial class AddRefreshTokenRevokedReason : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RevokedReason",
                table: "RefreshTokens",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_RefreshTokens_RevokedReason",
                table: "RefreshTokens",
                sql: "[RevokedReason] IS NULL OR [RevokedReason] IN ('SessionEnded', 'SignOutEverywhere', 'ReuseContainment', 'Incident', 'Deployment')");

            // Every ACTIVE legacy grant: not revoked and not expired. An expired one is already refused
            // and the cleanup sweep deletes it; a revoked one keeps its null reason, which marks it
            // legacy.
            migrationBuilder.Sql(
                "UPDATE [RefreshTokens] SET [RevokedAt] = SYSUTCDATETIME(), [RevokedReason] = N'Deployment' "
                + "WHERE [RevokedAt] IS NULL AND [ExpiresAt] > SYSUTCDATETIME();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_RefreshTokens_RevokedReason",
                table: "RefreshTokens");

            migrationBuilder.DropColumn(
                name: "RevokedReason",
                table: "RefreshTokens");
        }
    }
}
