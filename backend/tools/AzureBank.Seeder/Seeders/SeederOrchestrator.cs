using Microsoft.Extensions.Logging;

namespace AzureBank.Seeder.Seeders;

/// <summary>
/// Orchestrates all registered seeders in order.
/// Seeders are executed sequentially based on their Order property.
/// </summary>
public class SeederOrchestrator
{
    private readonly IEnumerable<ISeeder> _seeders;
    private readonly ILogger<SeederOrchestrator> _logger;

    public SeederOrchestrator(
        IEnumerable<ISeeder> seeders,
        ILogger<SeederOrchestrator> logger)
    {
        // Order seeders by their Order property (ascending)
        _seeders = seeders.OrderBy(s => s.Order);
        _logger = logger;
    }

    /// <summary>
    /// Execute all seeders in sequence.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task SeedAllAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting database seeding...");

        var seederList = _seeders.ToList();
        _logger.LogInformation("Found {Count} seeders to execute", seederList.Count);

        foreach (var seeder in seederList)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Seeding cancelled");
                break;
            }

            _logger.LogInformation("Running {Seeder} (Order: {Order})...", seeder.Name, seeder.Order);

            try
            {
                await seeder.SeedAsync(cancellationToken);
                _logger.LogInformation("Completed {Seeder}", seeder.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to execute {Seeder}", seeder.Name);
                throw;
            }
        }

        _logger.LogInformation("Database seeding completed successfully");
    }
}
