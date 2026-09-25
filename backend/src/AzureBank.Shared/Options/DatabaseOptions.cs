using System.ComponentModel.DataAnnotations;

namespace AzureBank.Shared.Options;

/// <summary>
/// The database the API works against. Binds to the "Database" section; the connection string itself
/// stays where every .NET host reads it, <c>ConnectionStrings:DefaultConnection</c>.
/// </summary>
/// <remarks>
/// The API checks at startup that the connection string is there and parses: without it the host
/// used to start and answer 500 at the first request that opened the database (measured
/// 2026-09-25, a sign-in through the BFF, both hosts as Production in containers).
/// </remarks>
public class DatabaseOptions
{
    /// <summary>Configuration section name in appsettings.json.</summary>
    public const string SectionName = "Database";

    /// <summary>The name under <c>ConnectionStrings</c> the API opens.</summary>
    public const string ConnectionStringName = "DefaultConnection";

    /// <summary>Longest <see cref="MaxRetryDelay"/> the API starts with.</summary>
    public static readonly TimeSpan LongestRetryDelay = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How many times EF retries an operation that failed transiently -- a lost connection, a
    /// deadlock, a resource limit; not a command timeout (<c>AddInfrastructure</c> says why). 3, the
    /// value the code has always had; 0 turns retrying off.
    /// </summary>
    [Range(0, 20, ErrorMessage = "Database:MaxRetryCount must be between 0 and 20.")]
    public int MaxRetryCount { get; set; } = 3;

    /// <summary>The longest wait between two retries; EF's backoff grows towards it. 30 seconds.</summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(30);
}
