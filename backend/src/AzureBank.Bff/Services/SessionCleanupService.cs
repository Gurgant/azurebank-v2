using AzureBank.Bff.Services.Interfaces;

namespace AzureBank.Bff.Services;

/// <summary>
/// Background service that periodically cleans up expired sessions.
/// Runs every 5 minutes to prevent memory accumulation.
/// </summary>
public class SessionCleanupService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SessionCleanupService> _logger;
    private readonly TimeSpan _cleanupInterval = TimeSpan.FromMinutes(5);

    public SessionCleanupService(
        IServiceProvider serviceProvider,
        ILogger<SessionCleanupService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Session cleanup service started (interval: {Interval})", _cleanupInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_cleanupInterval, stoppingToken);

                using var scope = _serviceProvider.CreateScope();
                var tokenStore = scope.ServiceProvider.GetRequiredService<ITokenStoreService>();

                await tokenStore.CleanupExpiredSessionsAsync();
            }
            catch (OperationCanceledException)
            {
                // Shutdown requested - this is expected
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during session cleanup");
            }
        }

        _logger.LogInformation("Session cleanup service stopped");
    }
}
