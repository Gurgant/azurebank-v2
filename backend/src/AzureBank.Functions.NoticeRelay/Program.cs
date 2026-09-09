using AzureBank.Infrastructure.Extensions;
using AzureBank.Infrastructure.Notices;
using AzureBank.Shared.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace AzureBank.Functions.NoticeRelay;

/// <summary>
/// The composition root of the second runner (ADR-0051).
/// </summary>
/// <remarks>
/// <para>
/// A NAMED CLASS RATHER THAN TOP-LEVEL STATEMENTS, and not for style. Top-level statements emit a
/// <c>Program</c> in the GLOBAL namespace, and the test project already references
/// <c>AzureBank.Api</c>, whose own global <c>Program</c> is the type
/// <c>WebApplicationFactory&lt;Program&gt;</c> is built on — two of them is CS0433 at every use
/// site. The Seeder solves the same collision with <c>Aliases="seeder"</c> on its project
/// reference; this project is referenced for its TYPES (the tests construct the Function class), so
/// moving the entry point into a namespace is the cheaper half of that trade.
/// </para>
/// <para>
/// IT REGISTERS WHAT ONE SWEEP NEEDS AND NOTHING ELSE: the DbContext, the pickup transport, and the
/// Notices section validated as THIS runner. Compare <c>AddVerifierServices</c>, which is the same
/// three registrations plus the audit key the verifier walks the chain with — this host walks
/// nothing, so it holds no key.
/// </para>
/// <para>
/// NO SECRET LIVES HERE, and that is worth saying because it is not obvious.
/// <c>AddInfrastructure</c> registers a DbContext and validates nothing; the DbContext has no
/// <c>SaveChangesInterceptor</c> (rejected deliberately — see <c>AuditChain</c>'s remarks); and
/// ADR-0048 D7 already decided that a delivered notice writes no audit row. So this host needs a
/// connection string and the <c>Notices</c> section, and none of the six validated secrets. A second
/// deployable that carries no secret is a smaller thing to reason about than one that does, and the
/// ADR says so rather than leaving a reader to check.
/// </para>
/// <para>
/// RETRY ON, unlike the verifier. That tool turns it off because a retrying execution strategy makes
/// EF pre-buffer a whole streamed walk; this host streams nothing — a sweep reads a bounded batch —
/// so it keeps the API's own composition, and a transient fault costs a retry rather than a period.
/// </para>
/// <para>
/// <c>ConfigureFunctionsWorkerDefaults</c>, not the newer <c>FunctionsApplication</c> builder: this
/// host has no HTTP trigger and no ASP.NET pipeline, and this is the shape measured to start on Core
/// Tools 4.13.0 with a net10.0 worker.
/// </para>
/// </remarks>
internal static class Program
{
    private static async Task Main(string[] args)
    {
        var host = new HostBuilder()
            .ConfigureFunctionsWorkerDefaults()
            .ConfigureServices((context, services) => Register(context.Configuration, context.HostingEnvironment, services))
            .Build();

        await host.RunAsync();
    }

    /// <summary>
    /// The registrations, separately so a test can build the same container the host builds and
    /// assert what it refuses — the composition root is where a second runner's configuration rules
    /// are enforced, and a root nobody can construct is a root nobody can test.
    /// </summary>
    internal static IServiceCollection Register(
        IConfiguration configuration, IHostEnvironment environment, IServiceCollection services)
    {
        services.AddInfrastructure(configuration, environment);

        services.AddOptions<NoticeRelayOptions>()
            .Bind(configuration.GetSection(NoticeRelayOptions.SectionName))
            .ValidateAsRunner(NoticeRunner.Function)
            // This host's cadence is Notices:Schedule, so it takes the schedule rule the way the API
            // takes the period rule. Without it a missing value is an indexing failure that leaves
            // the host running and delivering nothing (ADR-0051 D3).
            .ValidateTheScheduleItTicksOn(NoticeRunner.Function)
            // This host's cadence is Notices:Schedule, so it takes the schedule rule the way the API
            // takes the period rule. Without it a missing value is an indexing failure that leaves
            // the host running and delivering nothing (ADR-0051 D3).
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // The same last hop the API and the verb use: a pickup directory on this machine. One
        // transport, three hosts — the seam a sending transport would replace in all three at once.
        services.AddSingleton<INoticeTransport, PickupDirectoryTransport>();

        // The two facts a Function class cannot hold, because it is constructed per invocation:
        // the name this host claims under, and when it last ticked.
        services.AddSingleton<NoticeRelayHostState>();

        return services;
    }
}
