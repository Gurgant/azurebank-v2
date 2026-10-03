using System.CommandLine;
using System.CommandLine.Invocation;
using AzureBank.Infrastructure.Data;
using AzureBank.Seeder.Extensions;
using AzureBank.Seeder.Seeders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AzureBank.Seeder.Commands;

/// <summary>
/// CLI command for resetting and reseeding the database: development and CI only.
/// Usage: azurebank-seeder reset [--confirm]
/// WARNING: This deletes ALL data!
/// </summary>
/// <remarks>
/// <para>
/// IT REFUSES AN AZURE SQL SERVER (exit 2, nothing opened). It drops the database and creates it
/// again, and on Azure SQL the database <c>MigrateAsync</c> creates is a new resource at the
/// service's default size, not the one the infrastructure made.
/// </para>
/// <para>
/// IT REFUSES DEMO MODE (exit 2, nothing opened): there the database is the pool's, with the
/// private copies visitors hold (ADR-0062).
/// </para>
/// <para>
/// THE PIN PEPPER IS CHECKED BEFORE THE DROP. Seeding needs it, and a reset that dropped the
/// database and then could not seed would leave nothing. That check used to run in
/// <c>Program.cs</c>, ahead of the command line; it is here now, with the same guarantee.
/// </para>
/// </remarks>
public static class ResetCommand
{
    private const string AzureSqlReason =
        "reset drops the database and creates it again, and on Azure that would create a new database "
        + "at the service's default size.";

    private const string DemoModeReason =
        "reset drops the database, and in demo mode that is the pool's, with the copies visitors hold.";

    /// <param name="services">The tool's provider.</param>
    /// <param name="stopping">Cancelled when the process is asked to stop (SIGTERM; Program.cs).</param>
    public static Command Create(IServiceProvider services, CancellationToken stopping = default)
    {
        var command = new Command("reset", "Reset database (DELETE all data) and reseed (development and CI only)");

        var confirmOption = new Option<bool>(
            aliases: ["--confirm", "-y"],
            description: "Skip confirmation prompt (for CI/CD)");

        command.AddOption(confirmOption);

        // Asking for the invocation's token is what makes Ctrl+C cancel the run; a SIGTERM
        // cancels the other one.
        command.SetHandler(async (InvocationContext invocation) =>
        {
            using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(
                invocation.GetCancellationToken(), stopping);
            invocation.ExitCode = await RunAsync(
                services,
                invocation.ParseResult.GetValueForOption(confirmOption),
                cancelled.Token);
        });

        return command;
    }

    /// <summary>
    /// Runs the command on <paramref name="services"/> and returns its exit code. Nothing it meets
    /// is thrown: a failure is one Error line and <see cref="ExitCodes.Failed"/>.
    /// </summary>
    public static async Task<int> RunAsync(IServiceProvider services, bool confirm, CancellationToken cancellationToken)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("AzureBank.Seeder.Reset");

        try
        {
            if (ConnectionTarget.ReadOrRefuse(services, logger, "reset", AzureSqlReason) is null
                || !services.PinPepperIsUsable(logger, "reset")
                || !DemoMode.IsOffFor(services, logger, "reset", DemoModeReason))
            {
                return ExitCodes.Refused;
            }

            if (!confirm)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("WARNING: This will DELETE all data in the database!");
                Console.ResetColor();
                Console.Write("Are you sure you want to continue? (y/N): ");

                var response = Console.ReadLine();
                if (!string.Equals(response, "y", StringComparison.OrdinalIgnoreCase))
                {
                    // A prompt somebody declined: nothing was done and nothing failed.
                    Console.WriteLine("Operation cancelled.");
                    return ExitCodes.Done;
                }
            }

            using var scope = services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();

            logger.LogInformation("Deleting database...");
            await context.Database.EnsureDeletedAsync(cancellationToken);

            logger.LogInformation("Applying migrations...");
            await context.Database.MigrateAsync(cancellationToken);

            logger.LogInformation("Running seeders...");
            await scope.ServiceProvider.GetRequiredService<SeederOrchestrator>().SeedAllAsync(cancellationToken);

            if (!await SeedCommand.DemoDataIsCompleteAsync(scope.ServiceProvider, logger, cancellationToken))
            {
                return ExitCodes.Failed;
            }

            logger.LogInformation("Database reset and reseeded");
            return ExitCodes.Done;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // The token, not the exception's type: SqlClient reports a command cancelled in flight
            // as a SqlException (MigrateCommand has the measurement).
            logger.LogWarning(
                "reset was cancelled. The database may be missing or half built; run reset again.");
            return ExitCodes.Failed;
        }
        catch (Exception e)
        {
            logger.LogError(e, "reset failed: {Reason}", MigrateCommand.FirstLine(e));
            return ExitCodes.Failed;
        }
    }
}
