using System.ComponentModel.DataAnnotations;

namespace AzureBank.Shared.Options;

/// <summary>
/// The database settings the hosts that call <c>AddInfrastructure</c> run with: every one of them
/// opens with its connection limits, the API, the Function and the seeder also retry with its
/// budget, the verifier without retries, and only the API validates them at startup. Binds to the
/// "Database" section; the connection string itself stays where every .NET host reads it,
/// <c>ConnectionStrings:DefaultConnection</c>.
/// </summary>
/// <remarks>
/// <para>
/// The API checks at startup that the connection string is there, parses and names a server: without
/// one, or with one naming no server, the host used to start and answer 500 at the first request that
/// opened the database (measured 2026-09-25, a sign-in through the BFF, both hosts as Production in
/// containers).
/// </para>
/// <para>
/// The connection limits are defaults, never overrides (ADR-0058): <c>SqlConnectionDefaults</c>
/// writes each into the connection string only when the string leaves that keyword unset, so a
/// value in a deployment's own string wins. They live in code rather than in each string because a
/// string is written in many places (compose, CI, user-secrets, the Azure secret) and a keyword
/// missing from one of them fails silently.
/// </para>
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
    /// deadlock, a resource limit; not a command timeout (<c>AddInfrastructure</c> says why). 4
    /// (ADR-0058): EF's back-off then waits about 12 s in all, 32 s on the throttling codes, enough
    /// for a failover, and an outage that lasts longer ends the request instead of holding it. 0
    /// turns retrying off. Until ADR-0058 it was 3.
    /// </summary>
    [Range(0, 20, ErrorMessage = "Database:MaxRetryCount must be between 0 and 20.")]
    public int MaxRetryCount { get; set; } = 4;

    /// <summary>
    /// The cap on EF's back-off between two retries, which grows towards it; on Azure's throttling
    /// codes EF adds 5 s after the cap. 10 seconds (ADR-0058); until then 30.
    /// </summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Seconds SqlClient may take to open a connection, written as <c>Connect Timeout</c> when the
    /// string sets none. It bounds the login and also every BEGIN, COMMIT and ROLLBACK SqlClient
    /// sends. 10 (ADR-0058); SqlClient's own default is 15. At least 1, because 0 means wait forever.
    /// </summary>
    [Range(1, 60, ErrorMessage = "Database:ConnectTimeoutSeconds must be between 1 and 60.")]
    public int ConnectTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// How many times SqlClient itself retries opening a connection, written as
    /// <c>ConnectRetryCount</c> when the string sets none. 0 (ADR-0058), so EF's execution strategy
    /// is the only retry layer. SqlClient's default of 1 retried under each of EF's attempts: one
    /// open of an unroutable address took 36.8 s (<c>AddObservability</c> records the measurement).
    /// </summary>
    [Range(0, 255, ErrorMessage = "Database:ConnectRetryCount must be between 0 and 255.")]
    public int ConnectRetryCount { get; set; }

    /// <summary>
    /// The most connections one process keeps open, written as <c>Max Pool Size</c> when the string
    /// sets none (raised to the string's <c>Min Pool Size</c> if that is higher, which SqlClient
    /// requires). 12 (ADR-0058): two API processes during a revision overlap and a job at 5 make 29,
    /// within the 30 logins the database tier allows. SqlClient's default is 100. The seeder sets 5
    /// in its own <c>appsettings.json</c>.
    /// </summary>
    [Range(1, 200, ErrorMessage = "Database:MaxPoolSize must be between 1 and 200.")]
    public int MaxPoolSize { get; set; } = 12;
}
