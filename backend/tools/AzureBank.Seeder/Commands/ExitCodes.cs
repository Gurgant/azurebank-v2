namespace AzureBank.Seeder.Commands;

/// <summary>
/// What the tool's process exits with. A job runner acts on this number and on nothing else, so
/// each value says whether running the command again can help (README.md beside the project).
/// </summary>
/// <remarks>
/// <c>seed-pool</c> and <c>recycle</c> also end with a code from 10 to 15: the run finished, and
/// the code names the count somebody should read (<c>Pool/PoolExitCodes.cs</c>).
/// </remarks>
public static class ExitCodes
{
    /// <summary>The command did its work. Running it again is safe and changes nothing.</summary>
    public const int Done = 0;

    /// <summary>
    /// The command failed after it reached for the server, or was cancelled. Running it again is
    /// safe. It is also what System.CommandLine returns for a command line it cannot read; that is
    /// the framework's own value and is left as it is.
    /// </summary>
    public const int Failed = 1;

    /// <summary>
    /// The command refused, and wrote nothing. Before any connection was opened: a setting it needs
    /// is missing, too short, out of range or unreadable, the connection string names no database,
    /// <c>migrate</c> was given a connect timeout of 0, the command was aimed at a server it must not
    /// touch, or the demo flag is not what the command needs (<c>seed-pool</c> and <c>recycle</c>
    /// run only with it on, <c>seed</c> and <c>reset</c> only with it off). After one count, the one
    /// refusal that reads the database: <c>seed</c>, or <c>reset</c> before its drop, on a database
    /// that holds the demo pool's rows. Running it again changes nothing until the configuration does.
    /// </summary>
    public const int Refused = 2;
}
