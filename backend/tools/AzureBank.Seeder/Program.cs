using System.CommandLine;
using AzureBank.Seeder.Commands;
using AzureBank.Seeder.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;

// ============================================
// AzureBank Database Seeder Tool
// ============================================
// A standalone CLI tool for database seeding and management.
// Lives outside the main architecture to avoid circular dependencies.
//
// Usage:
//   dotnet run --project tools/AzureBank.Seeder -- seed
//   dotnet run --project tools/AzureBank.Seeder -- reset --confirm
//   dotnet run --project tools/AzureBank.Seeder -- --help
// ============================================

// Build host with services
var builder = Host.CreateApplicationBuilder(args);

// Configure Serilog for console output
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .WriteTo.Console(
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

builder.Services.AddSerilog();

// Register all seeder services
builder.Services.AddSeederServices(builder.Configuration, builder.Environment);

var host = builder.Build();

// Fail fast on a misconfigured options set (e.g. a missing/short PIN pepper) BEFORE
// any command runs — `reset` wipes and re-migrates the DB, so late validation would
// destroy data then throw. This CLI never calls host.StartAsync(), so .ValidateOnStart()
// alone would never fire; invoking the startup validator here runs it explicitly.
host.Services.GetService<IStartupValidator>()?.Validate();

// Build CLI with System.CommandLine
var rootCommand = new RootCommand("AzureBank Database Seeder Tool")
{
    Description = "CLI tool for seeding and managing the AzureBank database"
};

// Add commands
rootCommand.AddCommand(SeedCommand.Create(host.Services));
rootCommand.AddCommand(ResetCommand.Create(host.Services));

// Execute CLI
return await rootCommand.InvokeAsync(args);
