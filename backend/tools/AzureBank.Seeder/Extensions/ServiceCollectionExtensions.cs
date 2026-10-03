using System.Text.RegularExpressions;
using AzureBank.Infrastructure.Extensions;
using AzureBank.Seeder.Pool;
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
        // starts the host, so .ValidateOnStart() alone would not fire: `seed`, `reset`,
        // `seed-pool` and `recycle` run the validator themselves, before any database work
        // (PinPepperIsUsable below).
        // Until 2026-10-01 Program.cs ran it ahead of the command line, for every command.
        services.AddOptions<PinHashingOptions>()
            .Bind(configuration.GetSection(PinHashingOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<PinHashingOptions>>(new PinHashingOptionsValidator(configuration));

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

        // The demo's settings. This CLI never starts the host, so .ValidateOnStart() fires only when
        // the start-up validator is run by hand, and PinPepperIsUsable below runs it: a command that
        // asks it refuses a demo setting out of range as it refuses the pepper, exit 2, before any
        // database work. `seed`, `reset`, `seed-pool` and `recycle` ask it; `migrate` reads
        // neither and does not.
        services.AddOptions<DemoOptions>()
            .Bind(configuration.GetSection(DemoOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<DemoOptions>, DemoOptionsValidator>();

        // The demo pool: what `seed-pool` and `recycle` run. The builder creates the roles with the
        // seeder `seed` uses, which it asks for by its own type: above it is registered only as one
        // of the ISeeders the orchestrator runs.
        services.AddScoped<RoleSeeder>();
        services.AddScoped<DemoCopyBuilder>();
        services.AddScoped<DemoCopyRecycler>();

        return services;
    }

    /// <summary>
    /// Whether the PIN pepper keyring passes the shared validator, and the demo's settings theirs,
    /// with the refusal logged when one does not. <c>seed</c>, <c>reset</c>, <c>seed-pool</c> and
    /// <c>recycle</c> ask before they open anything: a false means exit 2.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pepper's failures name keys and lengths only (<c>PinHashingOptionsValidator</c>), never a
    /// value. The demo's name a key and the number it holds (<c>DemoOptionsValidator</c>), which is
    /// no secret. So they are printed as they are, without the exception's stack.
    /// </para>
    /// <para>
    /// THREE SHAPES OF ONE REFUSAL. One section that fails its validator throws its own failure.
    /// Both together come as one <see cref="AggregateException"/> of the two. And a value the binder
    /// cannot turn into its setting's type ("yes" for the flag, a word for a number) throws before
    /// any validator runs, with a message that quotes the value: that refusal names the key and the
    /// type, and not the value. Each is a configuration to change, so each is exit 2; until
    /// 2026-10-03 the last two reached the command's catch-all and ended as exit 1, with a stack.
    /// </para>
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
            return Refused(logger, command, e.Failures);
        }
        catch (AggregateException e) when (e.InnerExceptions.All(inner => inner is OptionsValidationException))
        {
            return Refused(
                logger, command, e.InnerExceptions.Cast<OptionsValidationException>().SelectMany(inner => inner.Failures));
        }
        catch (InvalidOperationException e) when (UnreadableSetting(e) is { } setting)
        {
            logger.LogError(
                "{Command} refused: {Key} holds a value that cannot be read as {Type}. Nothing was opened.",
                command,
                setting.Key,
                setting.Type);
            return false;
        }
    }

    private static bool Refused(ILogger logger, string command, IEnumerable<string> failures)
    {
        logger.LogError("{Command} refused: {Failures} Nothing was opened.", command, string.Join(" ", failures));
        return false;
    }

    // The binder's own sentence: "Failed to convert configuration value '…' at 'Demo:Enabled' to
    // type 'System.Boolean'." Read from its end, since the value can hold anything. Another
    // InvalidOperationException is no setting's, and is not caught.
    private static readonly Regex FailedConversion = new(
        @" at '(?<key>[^']+)' to type '(?<type>[^']+)'\.$", RegexOptions.CultureInvariant);

    private static (string Key, string Type)? UnreadableSetting(InvalidOperationException exception)
    {
        if (!exception.Message.StartsWith("Failed to convert configuration value ", StringComparison.Ordinal))
        {
            return null;
        }

        var match = FailedConversion.Match(exception.Message);
        if (!match.Success)
        {
            return null;
        }

        var type = match.Groups["type"].Value;
        return (match.Groups["key"].Value, type.StartsWith("System.", StringComparison.Ordinal) ? type["System.".Length..] : type);
    }
}
