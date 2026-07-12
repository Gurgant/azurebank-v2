namespace AzureBank.Seeder.Seeders;

/// <summary>
/// Interface for all database seeders.
/// Seeders are executed in Order sequence by the SeederOrchestrator.
/// </summary>
public interface ISeeder
{
    /// <summary>
    /// Display name for logging purposes.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Execution order (lower values execute first).
    /// Recommended: Roles=1, Users=2, Accounts=3, Transactions=4
    /// </summary>
    int Order { get; }

    /// <summary>
    /// Execute the seeding operation.
    /// Implementations should be idempotent (skip if data exists).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for async operations</param>
    Task SeedAsync(CancellationToken cancellationToken = default);
}
