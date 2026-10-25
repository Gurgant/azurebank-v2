extern alias seeder;

using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using SeederServices = seeder::AzureBank.Seeder.Extensions.ServiceCollectionExtensions;

namespace AzureBank.Tests.Fixtures;

/// <summary>
/// The Seeder's own composition root (<c>AddSeederServices</c>), built the way its
/// <c>Program.cs</c> builds it, with a recorder in place of the console and the configuration a
/// test names. A command run on this provider takes the road the tool takes.
/// </summary>
/// <remarks>
/// The host registers <see cref="IConfiguration"/> in the real tool; here the fixture does. An
/// interceptor registered as <c>IInterceptor</c> reaches the context, because
/// <c>AddInfrastructure</c> attaches every one the provider holds.
/// </remarks>
internal static class SeederHost
{
    /// <summary>A PIN pepper long enough for the shared validator (32 characters at least).</summary>
    public const string Pepper = "seeder-tests-pin-pepper-0123456789abcdef";

    /// <summary>The settings file the tools image ships.</summary>
    public static string CommittedSettingsPath =>
        RepositoryFiles.ExistingFile("backend", "tools", "AzureBank.Seeder", "appsettings.json");

    /// <summary>
    /// The provider. <paramref name="onCommittedSettings"/> puts the Seeder's committed
    /// <c>appsettings.json</c> under <paramref name="settings"/>, which then win.
    /// </summary>
    public static ServiceProvider Build(
        RecordingLoggerProvider log,
        IInterceptor? interceptor,
        bool onCommittedSettings,
        params (string Key, string? Value)[] settings)
    {
        var builder = new ConfigurationBuilder();
        if (onCommittedSettings)
        {
            builder.AddJsonFile(CommittedSettingsPath, optional: false);
        }

        var configuration = builder
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns("Production");

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(log));
        services.AddSingleton<IConfiguration>(configuration);
        if (interceptor is not null)
        {
            services.AddSingleton(interceptor);
        }

        SeederServices.AddSeederServices(services, configuration, environment.Object);
        return services.BuildServiceProvider();
    }
}
