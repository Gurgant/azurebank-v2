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
        .AddDefaultTokenProviders();

        // PIN-hash pepper keyring (ADR-0011). MUST match the API's Security:PinPepper,
        // else seeded PINs won't verify. Same shared validator as the API. Eager
        // fail-fast is triggered in Program.cs (this CLI never starts the host, so
        // .ValidateOnStart() alone would not fire — see Program.cs).
        services.AddOptions<PinHashingOptions>()
            .Bind(configuration.GetSection(PinHashingOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<PinHashingOptions>, PinHashingOptionsValidator>();

        // Add PasswordHasher (from Shared layer) - built with the PIN pepper.
        services.AddScoped<IPasswordHasher>(sp =>
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
}
