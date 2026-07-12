using System.CommandLine;
using AzureBank.Infrastructure.Data;
using AzureBank.Seeder.Seeders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AzureBank.Seeder.Commands;

/// <summary>
/// CLI command for resetting and reseeding the database.
/// Usage: azurebank-seeder reset [--confirm]
/// WARNING: This deletes ALL data!
/// </summary>
public static class ResetCommand
{
    public static Command Create(IServiceProvider services)
    {
        var command = new Command("reset", "Reset database (DELETE all data) and reseed");

        var confirmOption = new Option<bool>(
            aliases: ["--confirm", "-y"],
            description: "Skip confirmation prompt (for CI/CD)");

        command.AddOption(confirmOption);

        command.SetHandler(async (confirm) =>
        {
            if (!confirm)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("WARNING: This will DELETE all data in the database!");
                Console.ResetColor();
                Console.Write("Are you sure you want to continue? (y/N): ");

                var response = Console.ReadLine();
                if (!string.Equals(response, "y", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("Operation cancelled.");
                    return;
                }
            }

            using var scope = services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();

            Console.WriteLine("Deleting database...");
            await context.Database.EnsureDeletedAsync();

            Console.WriteLine("Applying migrations...");
            await context.Database.MigrateAsync();

            Console.WriteLine("Running seeders...");
            var orchestrator = scope.ServiceProvider.GetRequiredService<SeederOrchestrator>();
            await orchestrator.SeedAllAsync();

            Console.WriteLine();
            Console.WriteLine("Database reset and reseeded successfully!");
        }, confirmOption);

        return command;
    }
}
