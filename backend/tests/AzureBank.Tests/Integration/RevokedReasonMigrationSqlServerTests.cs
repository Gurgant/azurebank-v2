using AzureBank.Infrastructure.Data;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AzureBank.Tests.Integration;

/// <summary>
/// The data half of <c>AddRefreshTokenRevokedReason</c> on real SQL Server (06 §4.1): every ACTIVE
/// legacy grant is revoked as <c>Deployment</c>, and nothing else is touched.
/// </summary>
/// <remarks>
/// <para>
/// A scratch database of its own, migrated to the migration BEFORE this one, seeded with the four
/// shapes a legacy row can have, then migrated forward. The shared test database cannot show this:
/// it has already applied the migration by the time any test reads it, and its rows were written by
/// whichever tests ran before.
/// </para>
/// <para>
/// Foreign keys are switched off in the scratch database, because the rows under test need a user
/// only to satisfy a key that plays no part in the migration's WHERE clause.
/// </para>
/// </remarks>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class RevokedReasonMigrationSqlServerTests
{
    private const string Previous = "20260909173646_AddSubscriberNoticeAuditEventId";
    private const string ThisOne = "20260928134643_AddRefreshTokenRevokedReason";

    [SqlServerFact]
    public async Task EveryActiveLegacyGrant_IsRevokedAsDeployment_AndNothingElseIsTouched()
    {
        var connectionString = new SqlConnectionStringBuilder(SqlServerFactAttribute.ConnectionString!)
        {
            InitialCatalog = $"AzureBankTests_RevokedReason_{Guid.NewGuid():N}",
            Pooling = false,
        }.ConnectionString;

        await using var context = new AzureBankDbContext(
            new DbContextOptionsBuilder<AzureBankDbContext>().UseSqlServer(connectionString).Options);
        var migrator = context.GetService<IMigrator>();

        try
        {
            await migrator.MigrateAsync(Previous);

            var active = Guid.NewGuid();
            var activeToo = Guid.NewGuid();
            var expired = Guid.NewGuid();
            var revoked = Guid.NewGuid();
            var legacyRevokedAt = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
            await context.Database.ExecuteSqlRawAsync("ALTER TABLE [RefreshTokens] NOCHECK CONSTRAINT ALL;");
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO [RefreshTokens] ([Id], [UserId], [TokenHash], [ExpiresAt], [CreatedAt], [RevokedAt], [IpAddress], [UserAgent])
                VALUES
                  ({active},    {Guid.NewGuid()}, {"hash-active"},     DATEADD(DAY, 6, SYSUTCDATETIME()),  SYSUTCDATETIME(), NULL,              {"ip"}, {"ua"}),
                  ({activeToo}, {Guid.NewGuid()}, {"hash-active-too"}, DATEADD(HOUR, 1, SYSUTCDATETIME()), SYSUTCDATETIME(), NULL,              {"ip"}, {"ua"}),
                  ({expired},   {Guid.NewGuid()}, {"hash-expired"},    DATEADD(DAY, -1, SYSUTCDATETIME()), SYSUTCDATETIME(), NULL,              {"ip"}, {"ua"}),
                  ({revoked},   {Guid.NewGuid()}, {"hash-revoked"},    DATEADD(DAY, 6, SYSUTCDATETIME()),  SYSUTCDATETIME(), {legacyRevokedAt}, {"ip"}, {"ua"});
                """);
            var before = DateTime.UtcNow.AddMinutes(-1);

            await migrator.MigrateAsync(ThisOne);

            var rows = await context.Database
                .SqlQuery<Row>($"SELECT [Id], [RevokedAt], [RevokedReason] FROM [RefreshTokens]")
                .ToDictionaryAsync(r => r.Id);

            rows.Should().HaveCount(4);
            foreach (var id in new[] { active, activeToo })
            {
                rows[id].RevokedReason.Should().Be("Deployment", "an active legacy grant would renew for days under the new code");
                rows[id].RevokedAt.Should().BeAfter(before);
            }

            rows[expired].RevokedAt.Should().BeNull("an expired grant is already refused, and is not active");
            rows[expired].RevokedReason.Should().BeNull();
            rows[revoked].RevokedAt.Should().Be(legacyRevokedAt, "a revoked row keeps the instant it was revoked");
            rows[revoked].RevokedReason.Should().BeNull("a revoked legacy row keeps no reason: that is what marks it legacy");

            // The CHECK constraint the runbook's hand-written SQL meets (06 §5): a misspelt reason is
            // refused by the database, since EF could not read it back.
            var typo = () => context.Database.ExecuteSqlRawAsync(
                "UPDATE [RefreshTokens] SET [RevokedReason] = N'Incidnet' WHERE [TokenHash] = N'hash-expired';");
            (await typo.Should().ThrowAsync<SqlException>()).Which.Number.Should().Be(547);

            // And Down takes the column away again.
            await migrator.MigrateAsync(Previous);
            (await context.Database
                    .SqlQuery<int?>($"SELECT COL_LENGTH('RefreshTokens', 'RevokedReason') AS [Value]")
                    .SingleAsync())
                .Should().BeNull();
        }
        finally
        {
            await context.Database.EnsureDeletedAsync();
        }
    }

    private sealed record Row(Guid Id, DateTime? RevokedAt, string? RevokedReason);
}
