using System.CommandLine;
using System.CommandLine.Invocation;
using AzureBank.Infrastructure.Data;
using AzureBank.Seeder.Extensions;
using AzureBank.Seeder.Seeders;
using AzureBank.Shared.Entities;
using Microsoft.AspNetCore.Identity;
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
/// nothing seeded. Now the four demo users and a ledger have to be there afterwards, or the exit
/// code is 1. The seeders themselves are unchanged: they still fill an empty database only, so a
/// seed that was cut short is not completed by running it again; <c>reset</c> starts over.
/// </para>
/// </remarks>
public static class SeedCommand
{
    private const string AzureSqlReason = "seed adds four users whose password and PIN are public.";

    /// <summary>The demo users <see cref="UserSeeder"/> creates.</summary>
    private const int DemoUsers = 4;

    public static Command Create(IServiceProvider services)
    {
        var command = new Command("seed", "Add the demo data to an empty database (local runs and CI only)");

        var forceOption = new Option<bool>(
            aliases: ["--force", "-f"],
            description: "Ignored: the seeders skip data that is already there");

        command.AddOption(forceOption);

        // Asking for the invocation's token is what makes Ctrl+C and SIGTERM cancel the run.
        command.SetHandler(async (InvocationContext invocation) =>
        {
            invocation.ExitCode = await RunAsync(services, invocation.GetCancellationToken());
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
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
    /// Whether the four demo users and a ledger are there, with an Error when they are not. Asked
    /// after the seeders ran, by <c>seed</c> and by <c>reset</c>.
    /// </summary>
    /// <remarks>
    /// The users and "some ledger rows", not the counts 4, 5 and 26: the demo data may grow, and
    /// the suite pins the counts.
    /// </remarks>
    internal static async Task<bool> DemoDataIsCompleteAsync(
        IServiceProvider scoped, ILogger logger, CancellationToken cancellationToken)
    {
        var users = await UserSeeder.GetSeededUsersAsync(
            scoped.GetRequiredService<UserManager<ApplicationUser>>(), cancellationToken);
        var ledgerRows = await scoped.GetRequiredService<AzureBankDbContext>()
            .Transactions.CountAsync(cancellationToken);

        if (users.Count == DemoUsers && ledgerRows > 0)
        {
            return true;
        }

        logger.LogError(
            "The demo data is incomplete: {Users} of {DemoUsers} demo users, {LedgerRows} ledger rows. "
            + "seed fills an empty database only; reset starts again (it drops the database).",
            users.Count,
            DemoUsers,
            ledgerRows);
        return false;
    }
}
