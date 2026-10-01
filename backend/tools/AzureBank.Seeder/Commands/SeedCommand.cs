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
/// CLI command for seeding the database with the demo data: for local runs and CI, never Azure.
/// Usage: azurebank-seeder seed
/// </summary>
/// <remarks>
/// <para>
/// IT REFUSES AN AZURE SQL SERVER (exit 2, nothing opened). The four users it creates have a
/// password and a PIN that are in this repository, and the image that runs <c>migrate</c> against
/// a deployment holds this command too.
/// </para>
/// <para>
/// IT CHECKS WHAT IT LEFT BEHIND. The seeders skip quietly: a user that fails to be created is
/// logged and passed over, then the accounts find no users and skip, then the ledger finds no
/// accounts and skips. That used to end in "Database seeded successfully!" and exit 0 with
/// nothing seeded. Now the five demo accounts and a ledger have to be there afterwards, or the
/// exit code is 1. The seeders still fill an empty database only, so a seed that was cut short is
/// not completed by running it again; <c>reset</c> starts over.
/// </para>
/// <para>
/// THE CHECK GOES BY THE ACCOUNT NUMBERS, NOT THE HANDLES. compose runs <c>seed</c> on every
/// <c>up</c>, so it also runs on a database people have used, and it has to pass there. A user may
/// rename their handle: looked up by handle, one rename made every later run say "3 of 4 demo
/// users" and exit 1, and compose never started the API again (measured 2026-10-01). An account's
/// number never changes and its row is never removed: closing one marks it deleted, which is why
/// the count ignores the soft-delete filter. Each of the four demo users owns at least one of the
/// five, so all five there means all four users were created.
/// </para>
/// </remarks>
public static class SeedCommand
{
    private const string AzureSqlReason = "seed adds four users whose password and PIN are public.";

    /// <param name="services">The tool's provider.</param>
    /// <param name="stopping">Cancelled when the process is asked to stop (SIGTERM; Program.cs).</param>
    public static Command Create(IServiceProvider services, CancellationToken stopping = default)
    {
        var command = new Command("seed", "Add the demo data to an empty database (local runs and CI only)");

        var forceOption = new Option<bool>(
            aliases: ["--force", "-f"],
            description: "Ignored: the seeders skip data that is already there");

        command.AddOption(forceOption);

        // Asking for the invocation's token is what makes Ctrl+C cancel the run; a SIGTERM
        // cancels the other one.
        command.SetHandler(async (InvocationContext invocation) =>
        {
            using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(
                invocation.GetCancellationToken(), stopping);
            invocation.ExitCode = await RunAsync(services, cancelled.Token);
        });

        return command;
    }

    /// <summary>
    /// Runs the command on <paramref name="services"/> and returns its exit code. Nothing it meets
    /// is thrown: a failure is one Error line and <see cref="ExitCodes.Failed"/>.
    /// </summary>
    public static async Task<int> RunAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("AzureBank.Seeder.Seed");

        try
        {
            if (ConnectionTarget.ReadOrRefuse(services, logger, "seed", AzureSqlReason) is null
                || !services.PinPepperIsUsable(logger, "seed"))
            {
                return ExitCodes.Refused;
            }

            using var scope = services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<SeederOrchestrator>().SeedAllAsync(cancellationToken);

            if (!await DemoDataIsCompleteAsync(scope.ServiceProvider, logger, cancellationToken))
            {
                return ExitCodes.Failed;
            }

            logger.LogInformation("The demo data is in place");
            return ExitCodes.Done;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // The token, not the exception's type: SqlClient reports a command cancelled in flight
            // as a SqlException (MigrateCommand has the measurement).
            logger.LogWarning(
                "seed was cancelled. A seed cut short cannot be completed by running it again; run reset.");
            return ExitCodes.Failed;
        }
        catch (Exception e)
        {
            logger.LogError(e, "seed failed: {Reason}", MigrateCommand.FirstLine(e));
            return ExitCodes.Failed;
        }
    }

    /// <summary>
    /// Whether the five demo accounts and a ledger are there, with an Error when they are not.
    /// Asked after the seeders ran, by <c>seed</c> and by <c>reset</c>.
    /// </summary>
    /// <remarks>
    /// The accounts <see cref="AccountSeeder"/> names and "some ledger rows", not the 26: the
    /// ledger may grow, with the demo data or with use, and the suite pins the counts.
    /// </remarks>
    internal static async Task<bool> DemoDataIsCompleteAsync(
        IServiceProvider scoped, ILogger logger, CancellationToken cancellationToken)
    {
        var context = scoped.GetRequiredService<AzureBankDbContext>();
        var expected = AccountSeeder.DemoAccountNumbers;

        // Closed accounts count: the row is still there, behind the soft-delete filter.
        var demoAccounts = await context.Accounts
            .IgnoreQueryFilters()
            .Where(account => expected.Contains(account.AccountNumber))
            .Select(account => account.AccountNumber)
            .Distinct()
            .CountAsync(cancellationToken);
        var ledgerRows = await context.Transactions.CountAsync(cancellationToken);

        if (demoAccounts == expected.Length && ledgerRows > 0)
        {
            return true;
        }

        logger.LogError(
            "The demo data is incomplete: {DemoAccounts} of {Expected} demo accounts, {LedgerRows} ledger rows. "
            + "seed fills an empty database only; reset starts again (it drops the database).",
            demoAccounts,
            expected.Length,
            ledgerRows);
        return false;
    }
}
