using AzureBank.Infrastructure.Extensions;
using AzureBank.Shared.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace AzureBank.Infrastructure.Data;

/// <summary>
/// Factory for creating DbContext at design time (migrations, scaffolding).
/// EF Core tools use this when running migrations from Infrastructure project.
///
/// Why needed:
/// - Infrastructure project is a class library (no host)
/// - EF Core needs to know how to create DbContext for migrations
/// - Reads connection string from Api's appsettings.json, when that file is there
/// </summary>
/// <remarks>
/// <para>
/// THE API'S SETTINGS ARE OPTIONAL. Until 2026-10-01 the factory required
/// <c>../AzureBank.Api/appsettings.json</c> relative to the current directory and threw without
/// it, so <c>dotnet ef</c> worked only from inside the repository. With no file there is no
/// connection string, which is enough for the commands that open nothing
/// (<c>migrations add</c>, <c>has-pending-model-changes</c>), and <c>--connection</c> supplies one
/// for the commands that do.
/// </para>
/// <para>
/// THE SAME LIMITS AND RETRY BUDGET AS THE HOSTS (ADR-0058). The string read from the settings
/// goes through <see cref="SqlConnectionDefaults"/>, and the context retries with the
/// <c>Database</c> section's budget, 4 retries capped at 10 s unless the file says otherwise.
/// Before, the raw string went to <c>UseSqlServer</c> with no retrying strategy.
/// </para>
/// <para>
/// <c>--connection</c> REPLACES THE STRING AFTER THIS FACTORY HAS RUN: the tool builds the context
/// here and then calls <c>SetConnectionString</c> on it. So a <c>dotnet ef … --connection</c> run
/// retries with this budget and opens with ITS OWN string's limits, which are SqlClient's defaults
/// unless that string sets them.
/// </para>
/// <para>
/// NO ENVIRONMENT VARIABLE IS READ, on purpose. An ambient
/// <c>ConnectionStrings__DefaultConnection</c> would aim every <c>dotnet ef</c> verb,
/// <c>database drop</c> included, at whatever a shell still holds. A deployment migrates through
/// the Seeder's <c>migrate</c> command (ADR-0060), not through this factory.
/// </para>
/// </remarks>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AzureBankDbContext>
{
    public AzureBankDbContext CreateDbContext(string[] args) => Create(Directory.GetCurrentDirectory());

    /// <summary>
    /// The context for a tool started in <paramref name="currentDirectory"/>. Taken as an argument
    /// so a test can name a folder without changing the process's own.
    /// </summary>
    internal static AzureBankDbContext Create(string currentDirectory)
    {
        var builder = new ConfigurationBuilder();

        // Navigate to Api project for configuration. SetBasePath throws on a folder that is not
        // there, so the folder is checked before the files are made optional.
        var apiDirectory = Path.GetFullPath(Path.Combine(currentDirectory, "..", "AzureBank.Api"));
        if (Directory.Exists(apiDirectory))
        {
            builder.SetBasePath(apiDirectory)
                .AddJsonFile("appsettings.json", optional: true)
                .AddJsonFile("appsettings.Development.json", optional: true);
        }

        var configuration = builder.Build();
        var database = configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>()
            ?? new DatabaseOptions();

        var optionsBuilder = new DbContextOptionsBuilder<AzureBankDbContext>();
        optionsBuilder.UseSqlServer(
            SqlConnectionDefaults.Apply(
                configuration.GetConnectionString(DatabaseOptions.ConnectionStringName), database),
            sqlOptions =>
            {
                sqlOptions.EnableRetryOnFailure(
                    maxRetryCount: database.MaxRetryCount,
                    maxRetryDelay: database.MaxRetryDelay,
                    errorNumbersToAdd: null);
                sqlOptions.MigrationsAssembly(typeof(AzureBankDbContext).Assembly.FullName);
            });

        return new AzureBankDbContext(optionsBuilder.Options);
    }
}
