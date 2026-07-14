using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;

namespace AzureBank.Tests.Fixtures;

/// <summary>
/// Custom WebApplicationFactory that supports both in-memory and SQL Server testing.
///
/// Default: Uses in-memory database for fast unit-style integration tests.
/// With Testcontainers: Call SetConnectionString() to use real SQL Server.
///
/// Usage (in-memory):
///   public class MyTests : IClassFixture&lt;CustomWebApplicationFactory&gt;
///   {
///       public MyTests(CustomWebApplicationFactory factory) => _factory = factory;
///   }
///
/// Usage (SQL Server):
///   [Collection("SqlServer")]
///   public class MyTests : IClassFixture&lt;CustomWebApplicationFactory&gt;
///   {
///       public MyTests(SqlServerContainerFixture dbFixture, CustomWebApplicationFactory factory)
///       {
///           factory.SetConnectionString(dbFixture.ConnectionString);
///       }
///   }
/// </summary>
public class CustomWebApplicationFactory : WebApplicationFactory<Program>
{
    /// <summary>
    /// Test-only HMAC key for idempotency request fingerprinting (ADR-0009).
    /// Public so tests can recompute fingerprints when seeding records directly.
    /// </summary>
    public const string IdempotencyHashKey =
        "integration-tests-only-idempotency-hmac-key-0123456789abcdef";

    private string? _connectionString;
    private bool _enableSqlRetryOnFailure;
    private readonly List<IInterceptor> _interceptors = new();

    // One database per factory instance. The name and root are fixed fields so that
    // the temporary provider built below and the application's own provider resolve
    // the SAME in-memory store (a name generated inside the options lambda would
    // yield a different database on every options build).
    private readonly string _databaseName = $"TestDb_{Guid.NewGuid():N}";
    private readonly InMemoryDatabaseRoot _databaseRoot = new();

    /// <summary>
    /// Sets the connection string for real SQL Server testing.
    /// Must be called before CreateClient() if using Testcontainers.
    /// </summary>
    public void SetConnectionString(string connectionString)
    {
        _connectionString = connectionString;
    }

    /// <summary>
    /// Enables EF's SQL Server retrying execution strategy (EnableRetryOnFailure),
    /// mirroring the production wiring. Opt-in: only the transient-retry proof
    /// needs it, so the default SQL path stays non-retrying (a transient injected
    /// there would surface instead of being retried). SQL Server path only.
    /// </summary>
    public void EnableSqlRetryOnFailure()
    {
        _enableSqlRetryOnFailure = true;
    }

    /// <summary>
    /// Registers an EF interceptor on the test DbContext (e.g. to inject a
    /// one-shot transient fault). Must be called before CreateClient().
    /// </summary>
    public void AddInterceptor(IInterceptor interceptor)
    {
        _interceptors.Add(interceptor);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // The Testing environment has no appsettings.Testing.json, so the required
        // JWT signing secret is supplied here. Test-only value - NOT a real secret.
        builder.UseSetting("Jwt:Secret",
            "integration-tests-only-signing-key-0123456789abcdef0123456789abcdef");

        // Idempotency HMAC fingerprinting key (ADR-0009). Test-only value.
        builder.UseSetting("Idempotency:HashKey", IdempotencyHashKey);

        builder.ConfigureServices(services =>
        {
            // Remove the real database context registration.
            // EF Core 9+ breaking change: AddDbContext registers the provider setup as
            // IDbContextOptionsConfiguration<T>, which re-applies SQL Server on top of
            // any provider added below. Both registrations must be removed, otherwise
            // the host fails at startup with a SqlServer+InMemory provider collision.
            services.RemoveAll(typeof(DbContextOptions<AzureBankDbContext>));
            services.RemoveAll(typeof(IDbContextOptionsConfiguration<AzureBankDbContext>));

            if (!string.IsNullOrEmpty(_connectionString))
            {
                // Use real SQL Server from Testcontainers
                services.AddDbContext<AzureBankDbContext>(options =>
                {
                    options.UseSqlServer(_connectionString, sql =>
                    {
                        if (_enableSqlRetryOnFailure)
                        {
                            // Mirror production (ServiceCollectionExtensions):
                            // the retrying strategy re-runs the transfer delegate
                            // on a transient fault, which is exactly what the
                            // transient-retry proof needs to exercise.
                            sql.EnableRetryOnFailure(
                                maxRetryCount: 3,
                                maxRetryDelay: TimeSpan.FromSeconds(5),
                                errorNumbersToAdd: null);
                        }
                    });

                    if (_interceptors.Count > 0)
                    {
                        options.AddInterceptors(_interceptors);
                    }
                });
            }
            else
            {
                // Fallback to in-memory for fast unit-style integration tests
                services.AddDbContext<AzureBankDbContext>(options =>
                {
                    options.UseInMemoryDatabase(_databaseName, _databaseRoot);

                    // Transfers wrap their work in BeginTransactionAsync; InMemory
                    // throws on transactions by default. Ignore (non-atomic is fine
                    // for these tests; real transactional behavior is covered by
                    // the SQL Server path).
                    options.ConfigureWarnings(w =>
                        w.Ignore(InMemoryEventId.TransactionIgnoredWarning));

                    // Downgrade SQL Server rowversion columns (see class docs).
                    options.ReplaceService<IModelCustomizer, InMemoryTestModelCustomizer>();
                });
            }

            // Build service provider and initialize database
            var sp = services.BuildServiceProvider();
            using var scope = sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();

            if (!string.IsNullOrEmpty(_connectionString))
            {
                // Apply migrations for real database
                db.Database.Migrate();
            }
            else
            {
                // Just create schema for in-memory
                db.Database.EnsureCreated();
            }

            // Seed Identity roles: registration assigns Roles.Default via
            // AddToRoleAsync, which fails if the role rows do not exist.
            // (In real environments the AzureBank.Seeder tool creates them.)
            var roleManager = scope.ServiceProvider
                .GetRequiredService<RoleManager<IdentityRole<Guid>>>();

            foreach (var roleName in Roles.All)
            {
                if (!roleManager.RoleExistsAsync(roleName).GetAwaiter().GetResult())
                {
                    roleManager.CreateAsync(new IdentityRole<Guid>(roleName))
                        .GetAwaiter().GetResult();
                }
            }
        });

        // Set environment to Testing
        builder.UseEnvironment("Testing");
    }
}
