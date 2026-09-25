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
}
