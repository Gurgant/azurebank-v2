using AzureBank.Shared.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AzureBank.Seeder.Commands;

/// <summary>What a connection string turned out to be, read without opening anything.</summary>
public enum ConnectionTargetKind
{
    /// <summary>There is no connection string.</summary>
    Missing,

    /// <summary>SqlClient's parser refuses the string, or it names no server.</summary>
    Unreadable,

    /// <summary>The string names a server.</summary>
    Named,
}

/// <summary>
/// What every command asks of its connection string before it opens a connection: is there one,
/// can it be read, and does it name an Azure SQL server.
/// </summary>
/// <remarks>
/// <para>
/// WHY THE NAME MATTERS. The image that runs <c>migrate</c> against a deployment also holds
/// <c>seed</c> and <c>reset</c>. <c>seed</c> creates four users whose password and PIN are in this
/// repository; <c>reset</c> drops the database, and the <c>MigrateAsync</c> after it would create a
/// new one at the service's default size. Neither may run against Azure SQL, and a sentence in a
/// document is not a guard.
/// </para>
/// <para>
/// THE RULE GOES BY THE SERVER'S NAME: the five suffixes SqlClient itself treats as Azure SQL
/// (<c>s_azureSqlServerEndpoints</c> in its <c>AdapterUtil</c>, read at v6.1.1). A private alias
/// or an IP address in front of an Azure SQL server is NOT recognised; nothing in this repository
/// reaches one that way.
/// </para>
/// <para>
/// THE SERVER IS READ AS SQLCLIENT READS IT. Every spelling (<c>Server</c>, <c>Data Source</c>,
/// <c>Address</c>, <c>Addr</c>, <c>Network Address</c>) lands in <c>DataSource</c> and the last
/// one written wins, so this reads the one that would be opened (measured 2026-10-01 on 22
/// strings). The protocol prefix, the port and the instance are cut off, and trailing dots, which
/// DNS ignores.
/// </para>
/// </remarks>
/// <param name="Kind">Whether a string is there and names a server.</param>
/// <param name="Server">The server's name alone; empty unless <paramref name="Kind"/> is Named.</param>
/// <param name="IsAzureSql">Whether <paramref name="Server"/> ends in an Azure SQL suffix.</param>
public sealed record ConnectionTarget(ConnectionTargetKind Kind, string Server, bool IsAzureSql)
{
    /// <summary>The environment variable every refusal names: where the string comes from in a container.</summary>
    public const string Variable = "ConnectionStrings__" + DatabaseOptions.ConnectionStringName;

    private static readonly string[] AzureSqlSuffixes =
    [
        ".database.windows.net",
        ".database.cloudapi.de",
        ".database.usgovcloudapi.net",
        ".database.chinacloudapi.cn",
        ".database.fabric.microsoft.com",
    ];

    private static readonly string[] ProtocolPrefixes = ["tcp:", "np:", "lpc:", "admin:"];

    /// <summary>Reads <paramref name="connectionString"/>. Opens nothing and never throws.</summary>
    public static ConnectionTarget Read(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return new ConnectionTarget(ConnectionTargetKind.Missing, string.Empty, IsAzureSql: false);
        }

        string dataSource;
        try
        {
            dataSource = new SqlConnectionStringBuilder(connectionString).DataSource;
        }
        catch (Exception e) when (e is ArgumentException or FormatException or OverflowException)
        {
            // The three SqlConnectionDefaults.Apply catches. The exception is dropped on purpose:
            // its message quotes the string ("Keyword not supported: '…'"), and a password holding
            // an unquoted ';' puts its tail there.
            return new ConnectionTarget(ConnectionTargetKind.Unreadable, string.Empty, IsAzureSql: false);
        }

        var server = ServerOf(dataSource ?? string.Empty);
        if (server.Length == 0)
        {
            return new ConnectionTarget(ConnectionTargetKind.Unreadable, string.Empty, IsAzureSql: false);
        }

        return new ConnectionTarget(
            ConnectionTargetKind.Named,
            server,
            AzureSqlSuffixes.Any(suffix => server.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// The target of the configured connection string, or null once the refusal has been logged:
    /// the string is missing or unreadable, or <paramref name="refusedOnAzureSql"/> is given and
    /// the server is an Azure SQL one. A null means exit <see cref="ExitCodes.Refused"/>.
    /// </summary>
    /// <param name="services">The tool's provider; the string is read from its configuration.</param>
    /// <param name="logger">Where the refusal goes.</param>
    /// <param name="command">The command's name, as typed.</param>
    /// <param name="refusedOnAzureSql">
    /// Why this command must not run against Azure SQL, as a sentence; null for a command that may.
    /// </param>
    public static ConnectionTarget? ReadOrRefuse(
        IServiceProvider services, ILogger logger, string command, string? refusedOnAzureSql)
    {
        var target = Read(services.GetRequiredService<IConfiguration>()
            .GetConnectionString(DatabaseOptions.ConnectionStringName));

        switch (target.Kind)
        {
            case ConnectionTargetKind.Missing:
                logger.LogError(
                    "{Command} refused: there is no connection string. Set {Variable}. Nothing was opened.",
                    command,
                    Variable);
                return null;

            case ConnectionTargetKind.Unreadable:
                logger.LogError(
                    "{Command} refused: {Variable} is not a SQL Server connection string this tool can read. "
                    + "Its text is not shown. A password that holds ';', '=' or a quote has to be quoted. "
                    + "Nothing was opened.",
                    command,
                    Variable);
                return null;
        }

        if (target.IsAzureSql && refusedOnAzureSql is not null)
        {
            logger.LogError(
                "{Command} refused: {Server} is an Azure SQL server. {Reason} Nothing was opened.",
                command,
                target.Server,
                refusedOnAzureSql);
            return null;
        }

        return target;
    }

    private static string ServerOf(string dataSource)
    {
        var server = dataSource.Trim();
        foreach (var prefix in ProtocolPrefixes)
        {
            if (server.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                server = server[prefix.Length..];
                break;
            }
        }

        // A named pipe is written \\server\pipe\…: the name follows the leading backslashes.
        server = server.TrimStart('\\');

        var comma = server.LastIndexOf(',');
        if (comma >= 0)
        {
            server = server[..comma];
        }

        var backslash = server.IndexOf('\\');
        if (backslash >= 0)
        {
            server = server[..backslash];
        }

        server = server.Trim();

        // "x.database.windows.net." is the same host. "." alone is the local machine and stays.
        var withoutDots = server.TrimEnd('.');
        return withoutDots.Length > 0 ? withoutDots : server;
    }
}
