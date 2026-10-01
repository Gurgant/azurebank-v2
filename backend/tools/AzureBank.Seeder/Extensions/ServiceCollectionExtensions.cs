using AzureBank.Infrastructure.Extensions;
using AzureBank.Seeder.Seeders;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Options;
using AzureBank.Shared.Services.Implementations;
using AzureBank.Shared.Services.Interfaces;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzureBank.Seeder.Extensions;

/// <summary>
/// Extension methods for registering Seeder services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds all services required for database seeding.
    /// </summary>
    public static IServiceCollection AddSeederServices(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        // Add Infrastructure (DbContext)
        services.AddInfrastructure(configuration, environment);

        // Add Identity services (UserManager, RoleManager)
        services.AddIdentity<ApplicationUser, IdentityRole<Guid>>(options =>
        {
            // Match Api's password requirements
            options.Password.RequireDigit = true;
            options.Password.RequireLowercase = true;
            options.Password.RequireUppercase = true;
            options.Password.RequireNonAlphanumeric = true;
            options.Password.RequiredLength = 8;
            options.Password.RequiredUniqueChars = 4;

            // User settings
            options.User.RequireUniqueEmail = true;
        })
        .AddEntityFrameworkStores<Infrastructure.Data.AzureBankDbContext>()
        // Identity's own managers hand their stores no token; these two hand them the run's
        // (Seeders/RunCancellation.cs has the measurement).
        .AddUserManager<CancellableUserManager>()
        .AddRoleManager<CancellableRoleManager>()
        .AddDefaultTokenProviders();
        services.AddScoped<RunCancellation>();

        // PIN-hash pepper keyring (ADR-0011). MUST match the API's Security:PinPepper,
        // else seeded PINs won't verify. Same shared validator as the API. This CLI never
        // starts the host, so .ValidateOnStart() alone would not fire: `seed` and `reset`
        // run the validator themselves, before any database work (PinPepperIsUsable below).
        // Until 2026-10-01 Program.cs ran it ahead of the command line, for every command.
        services.AddOptions<PinHashingOptions>()
            .Bind(configuration.GetSection(PinHashingOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<PinHashingOptions>, PinHashingOptionsValidator>();

        // Add PasswordHasher (from Shared layer) - built with the PIN pepper.
        // Singleton for the same reasons as the API: immutable, singleton-scoped deps.
        services.AddSingleton<IPasswordHasher>(sp =>
            new PasswordHasher(sp.GetRequiredService<IOptions<PinHashingOptions>>().Value));

        // Add Seed Data Options
        services.Configure<SeedDataOptions>(
            configuration.GetSection(SeedDataOptions.SectionName));

        // Register all seeders
        services.AddScoped<ISeeder, RoleSeeder>();
        services.AddScoped<ISeeder, UserSeeder>();
        services.AddScoped<ISeeder, AccountSeeder>();
        services.AddScoped<ISeeder, TransactionSeeder>();

        // Register orchestrator
        services.AddScoped<SeederOrchestrator>();

        return services;
    }

    /// <summary>
    /// Whether the PIN pepper keyring passes the shared validator, with the refusal logged when it
    /// does not. <c>seed</c> and <c>reset</c> ask before they open anything: a false means exit 2.
    /// </summary>
    /// <remarks>
    /// The failures name keys and lengths only (<c>PinHashingOptionsValidator</c>), never a value,
    /// so they are printed as they are, without the exception's stack.
    /// </remarks>
    public static bool PinPepperIsUsable(this IServiceProvider services, ILogger logger, string command)
    {
        try
        {
            services.GetRequiredService<IStartupValidator>().Validate();
            return true;
        }
        catch (OptionsValidationException e)
        {
            logger.LogError(
                "{Command} refused: {Failures} Nothing was opened.", command, string.Join(" ", e.Failures));
            return false;
        }
    }
}
