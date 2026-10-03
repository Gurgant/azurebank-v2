using AzureBank.Infrastructure.Data;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace AzureBank.Tests.Unit.Data;

/// <summary>
/// The model the code builds is the model the last migration recorded: no entity or configuration
/// was changed without a migration that carries the change.
/// </summary>
/// <remarks>
/// <para>
/// On a real database the drift is refused by <c>MigrateAsync</c> itself, so every SQL-gated test
/// meets it. This asks the same question where no SQL Server is configured: the provider compares
/// the two models in memory and opens no connection, so the connection string only has to parse.
/// </para>
/// <para>
/// It is the check <c>dotnet ef migrations has-pending-model-changes</c> makes. What it cannot
/// see is a difference that changes no table, such as who mints a key.
/// </para>
/// </remarks>
public class ModelSnapshotTests
{
    [Fact]
    public void TheModel_HoldsNoChangeThatNoMigrationCarries()
    {
        using var db = new AzureBankDbContext(
            new DbContextOptionsBuilder<AzureBankDbContext>()
                .UseSqlServer(CustomWebApplicationFactory.PlaceholderConnectionString)
                .Options);

        db.Database.HasPendingModelChanges().Should().BeFalse(
            "a change to the model needs a migration: run `dotnet ef migrations add` and commit what it writes");
    }
}
