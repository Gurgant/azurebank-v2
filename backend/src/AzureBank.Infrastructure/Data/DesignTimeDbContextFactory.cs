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
/// - Reads connection string from Api's appsettings.json
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AzureBankDbContext>
{
    public AzureBankDbContext CreateDbContext(string[] args)
    {
        // Navigate to Api project for configuration
        var basePath = Path.GetFullPath(
            Path.Combine(Directory.GetCurrentDirectory(), "..", "AzureBank.Api"));

        var configuration = new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .Build();

        var connectionString = configuration.GetConnectionString("DefaultConnection");

        var optionsBuilder = new DbContextOptionsBuilder<AzureBankDbContext>();
        optionsBuilder.UseSqlServer(connectionString, sqlOptions =>
        {
            sqlOptions.MigrationsAssembly(typeof(AzureBankDbContext).Assembly.FullName);
        });

        return new AzureBankDbContext(optionsBuilder.Options);
    }
}
