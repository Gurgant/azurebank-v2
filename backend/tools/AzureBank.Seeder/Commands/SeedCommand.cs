using System.CommandLine;
using AzureBank.Seeder.Seeders;
using Microsoft.Extensions.DependencyInjection;

namespace AzureBank.Seeder.Commands;

/// <summary>
/// CLI command for seeding the database.
/// Usage: azurebank-seeder seed [--force]
/// </summary>
public static class SeedCommand
{
    public static Command Create(IServiceProvider services)
    {
        var command = new Command("seed", "Seed the database with test data");

        var forceOption = new Option<bool>(
            aliases: ["--force", "-f"],
            description: "Force seeding (currently ignored, seeders check for existing data)");

        command.AddOption(forceOption);

        command.SetHandler(async (force) =>
        {
            using var scope = services.CreateScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<SeederOrchestrator>();

            await orchestrator.SeedAllAsync();

            Console.WriteLine();
            Console.WriteLine("Database seeded successfully!");
        }, forceOption);

        return command;
    }
}
