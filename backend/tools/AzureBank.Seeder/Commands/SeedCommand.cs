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
/// IT REFUSES THE PUBLIC DEMO TOO (exit 2), in two ways, for the same reason: the demo there is
/// the pool's private copies (ADR-0062), and four users anybody can sign in as do not belong beside
/// them. With <c>Demo:Enabled</c> on it opens nothing. With the flag off, its first statement counts
/// the pool's rows, and one row, the record of a deleted copy included, ends the run before it
/// writes anything: a job whose environment lost the flag is still pointed at the demo's database.
/// </para>
/// <para>
/// IT CHECKS WHAT IT LEFT BEHIND. The seeders skip quietly: a user that fails to be created is
/// logged and passed over, then the accounts find no users and skip, then the ledger finds no
/// accounts and skips. That used to end in "Database seeded successfully!" and exit 0 with
/// nothing seeded. Now the five demo accounts and a ledger of at least the demo's rows have to be
/// there afterwards, or the exit code is 1. The seeders still fill an empty database only, so a
/// seed that was cut short is not completed by running it again; <c>reset</c> starts over.
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

    private const string DemoModeReason =
        "In demo mode the demo is the pool's private copies, which seed-pool builds, and seed's four users "
        + "have a password and a PIN that are public.";

    private const string DemoDatabaseReason =
        "seed's four users have a password and a PIN that are public, and there they would be users "
        + "outside every copy that anybody can sign in as.";

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
                || !services.PinPepperIsUsable(logger, "seed")
                || !DemoMode.IsOffFor(services, logger, "seed", DemoModeReason))
            {
                return ExitCodes.Refused;
            }

            using var scope = services.CreateScope();
            if (await DemoMode.HoldsThePoolAsync(scope.ServiceProvider, logger, "seed", DemoDatabaseReason, cancellationToken))
            {
                return ExitCodes.Refused;
            }

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
    /// Whether the five demo accounts and the demo ledger are there, with an Error when they are
    /// not. Asked after the seeders ran, by <c>seed</c> and by <c>reset</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The accounts <see cref="AccountSeeder"/> names, and at least as many ledger rows as
    /// <see cref="TransactionSeeder"/> writes. "Some ledger rows" was the rule until 2026-10-02,
    /// and it passed a ledger of one: the ledger seeder skips a table that holds any row, so with
    /// the five accounts seeded and one deposit made before the ledger was, <c>seed</c> exited 0
    /// without the demo's rows, and exit 0 is what compose starts the API on.
    /// </para>
    /// <para>
    /// At least, not exactly: the app adds ledger rows and never removes one (the context refuses
    /// a deleted transaction), so a seeded ledger only grows with use. The demo pool's recycler does
    /// delete ledger rows, a deleted copy's, and only on a database that holds the pool, which this
    /// command refuses before it seeds. The count cannot tell the demo's rows from other rows: a
    /// database with that many rows of its own and no demo ledger would pass.
    /// </para>
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
        var demoLedgerRows = TransactionSeeder.DemoLedgerRowCount;

        if (demoAccounts == expected.Length && ledgerRows >= demoLedgerRows)
        {
            return true;
        }

        logger.LogError(
            "The demo data is incomplete: {DemoAccounts} of {Expected} demo accounts, {LedgerRows} ledger rows "
            + "where the demo ledger has {DemoLedgerRows}. "
            + "seed fills an empty database only; reset starts again (it drops the database).",
            demoAccounts,
            expected.Length,
            ledgerRows,
            demoLedgerRows);
        return false;
    }
}
