using System.CommandLine;
using System.Runtime.InteropServices;
using AzureBank.Seeder.Commands;
using AzureBank.Seeder.Extensions;
using Microsoft.Extensions.Hosting;
using Serilog;

// ============================================
// AzureBank Database Seeder Tool
// ============================================
// A standalone CLI tool for migrating, seeding and resetting the database.
// Lives outside the main architecture to avoid circular dependencies.
// It is also what the tools image runs (the Dockerfile and README.md beside this file).
//
// Usage:
//   dotnet run --project tools/AzureBank.Seeder -- migrate
//   dotnet run --project tools/AzureBank.Seeder -- seed
//   dotnet run --project tools/AzureBank.Seeder -- reset --confirm
//   dotnet run --project tools/AzureBank.Seeder -- --help
//
// Exit codes (Commands/ExitCodes.cs): 0 done; 1 failed, or the command line was wrong;
// 2 refused before any connection was opened.
// ============================================

/*
  THE CONTENT ROOT IS THE BINARY'S FOLDER, not the current directory, which is what
  Host.CreateApplicationBuilder(args) takes. The tool's appsettings.json sits beside its dll, so
  started from anywhere else it used to run without it: CI starts it from backend/, where one
  `reset` printed 874 lines, 169 of them "Executed DbCommand", and opened with a pool of 12 instead
  of the 5 its settings ask for (measured 2026-10-01; 17 lines and 0 from its own folder).

  THE COMMAND LINE IS NOT A CONFIGURATION SOURCE: Args is not handed to the host. Configuration
  comes from the settings file beside the binary, user-secrets in Development, and the
  environment. The arguments are the command and its options and nothing else, so a secret has no
  reason to be typed there, where a process list shows it and the parser prints back a token it
  does not know. DOTNET_ENVIRONMENT still selects the environment.
*/
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    ContentRootPath = AppContext.BaseDirectory,
});

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

/*
  THE PIN-PEPPER CHECK MOVED INTO THE COMMANDS THAT NEED IT, and this comment records why rather
  than vanishing. It ran here, before the command line was parsed, so with no pepper configured no
  invocation got as far as its command: --help, a missing command and `migrate`, which never reads
  the pepper, all ended in an unhandled OptionsValidationException (exit 139 in a Linux container,
  measured 2026-10-01). `seed` and `reset` now run the validator at their own start, still before
  any database work, so `reset` cannot drop a database it then cannot seed, and answer with a
  sentence and exit 2 (ServiceCollectionExtensions.PinPepperIsUsable).
*/

/*
  SIGTERM CANCELS THE COMMAND, and the registration below is what makes that true. It is what
  `docker stop` sends, and a job runner whose time limit has passed. Without it the process ended at
  once with exit 143 and not a line from the command (measured 2026-10-01 in the tools image, during
  the wait and during a pause of it): System.CommandLine's own handling of process termination
  never got to cancel anything. With it the command's token is cancelled, the command says what
  was cut short and whether running it again is safe, and the exit code is its own, 1.
*/
using var stopping = new CancellationTokenSource();
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, signal =>
{
    signal.Cancel = true;
    stopping.Cancel();
});

// Build CLI with System.CommandLine
var rootCommand = new RootCommand("AzureBank Database Seeder Tool")
{
    Description = "CLI tool for migrating, seeding and resetting the AzureBank database"
};

// Add commands
rootCommand.AddCommand(MigrateCommand.Create(host.Services, stopping.Token));
rootCommand.AddCommand(SeedCommand.Create(host.Services, stopping.Token));
rootCommand.AddCommand(ResetCommand.Create(host.Services, stopping.Token));

// Execute CLI. Each handler sets the exit code on its invocation, and that is what comes back.
try
{
    return await rootCommand.InvokeAsync(args);
}
finally
{
    await Log.CloseAndFlushAsync();
}
