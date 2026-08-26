using AzureBank.AuditVerifier.Extensions;
using AzureBank.Infrastructure.Data;
using AzureBank.Infrastructure.Extensions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Xunit;

namespace AzureBank.Tests.Unit.Tools;

/// <summary>
/// The verifier's context must NOT have a retrying execution strategy, or its walk stops streaming.
/// </summary>
/// <remarks>
/// <para>
/// This is the least visible dependency in the tool. <c>VerifyAsync</c> uses
/// <c>AsAsyncEnumerable()</c>, but EF refuses to stream when the context can retry -- a stream
/// cannot be replayed from the middle, so <c>QueryCompilationContext.IsBuffering</c> is taken from
/// <c>ExecutionStrategy.RetriesOnFailure</c> and the whole resultset is pre-buffered.
/// </para>
/// <para>
/// MEASURED on 40,006 audit rows, same table, only the composition differing: <b>3 MB</b> of managed
/// heap with retry off and <b>34 MB</b> with it on, the 34 MB already allocated before the first row
/// was examined. Nothing about the calling code changes, which is why this needs a test rather than
/// a comment: dropping the argument reverts the tool to carrying the entire audit table in memory,
/// and every existing test would still pass.
/// </para>
/// </remarks>
public class VerifierUsesAStreamingContextTests
{
    private static IConfiguration Configuration() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = @"Server=(localdb)\MSSQLLocalDB;Database=X;Trusted_Connection=True",
            ["Audit:ChainKey"] = new string('k', 40),

            // The tool validates BOTH audit keys at startup now, so a fixture supplying only one
            // builds a host that refuses to start -- which has nothing to do with what this asserts.
            ["Audit:AnchorKey"] = new string('a', 40),
        }).Build();

    private static IHostEnvironment Environment()
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns("Production");
        return environment.Object;
    }

    private static bool RetriesOnFailure(IServiceCollection services)
    {
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        return context.Database.CreateExecutionStrategy().RetriesOnFailure;
    }

    [Fact]
    public void TheVerifiersContextDoesNotRetry_SoItsWalkActuallyStreams()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddVerifierServices(Configuration(), Environment());

        RetriesOnFailure(services).Should().BeFalse(
            "a retrying strategy makes EF pre-buffer the whole audit table -- measured at 34 MB "
            + "against 3 MB for 40,006 rows -- which is the cost this tool exists to avoid");
    }

    [Fact]
    public void EverythingThatWRITESStillRetries()
    {
        /*
          The negative control, and the reason the parameter is opt-out rather than a global change.
          Writers make small saves where buffering costs nothing and a transient fault is worth
          retrying; ADR-0034 and the transfer retry proofs depend on that. Turning retry off for
          everyone to make one read-only tool stream would trade a memory problem for a correctness
          one.
        */
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(Configuration(), Environment());

        RetriesOnFailure(services).Should().BeTrue(
            "the API and the seeder must keep the retrying strategy the money path is built on");
    }
}
