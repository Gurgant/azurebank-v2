using AzureBank.Api.Extensions;
using AzureBank.AuditVerifier.Extensions;
using AzureBank.Infrastructure.Notices;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Xunit;
// A BARE `Program` IS THE API'S — see NoticeFunctionStartupTests for the compiler's word on it.
using FunctionHost = AzureBank.Functions.NoticeRelay.Program;

namespace AzureBank.Tests.Integration;

/// <summary>
/// The last hop of a notice is registered by hand in three composition roots — the API's, the
/// audit tool's and the Function's — and they must agree: each registers exactly one
/// <see cref="INoticeTransport"/>, with the same lifetime and implementation as the other two.
/// </summary>
/// <remarks>
/// <para>
/// NOTHING PINNED TWO OF THE THREE. Measured 2026-09-11 by deleting each line and running the whole
/// suite with SQL Server enabled: without the tool's line or the Function's, all 1195 tests passed;
/// without the API's, only six <c>NoticeRelaySqlServerTests</c> failed, which CI runs in its SQL
/// Server job alone. And the tool's line is not decorative. Without it, <c>notify</c> on a correct
/// command line exited 4, the usage-error code, with "No service for type
/// 'AzureBank.Infrastructure.Notices.INoticeTransport' has been registered"; with it, the same
/// command exited 0 and wrote four notices.
/// </para>
/// <para>
/// A test, not a shared registration extension: the day a sending transport arrives it must replace
/// the pickup directory in all three roots, and this is what fails if it replaces it in two.
/// Asserted on the DESCRIPTORS, so no root is built into a provider and nothing is resolved — which
/// is also why no database, directory or secret is needed.
/// </para>
/// </remarks>
public sealed class NoticeTransportRegistrationTests
{
    private static IConfiguration Configuration() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] =
                @"Server=(localdb)\MSSQLLocalDB;Database=Unreached;Trusted_Connection=True",
            ["Notices:Runner"] = "None",
        }).Build();

    private static IHostEnvironment Production()
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns(Environments.Production);
        return environment.Object;
    }

    /// <summary>Each root, as its host runs it, on a collection of its own.</summary>
    private static IEnumerable<(string Root, IServiceCollection Services)> Roots()
    {
        var api = new ServiceCollection();
        api.AddApplicationServices(Configuration());
        yield return ("the API's AddApplicationServices", api);

        var tool = new ServiceCollection();
        tool.AddVerifierServices(Configuration(), Production());
        yield return ("the audit tool's AddVerifierServices", tool);

        var function = new ServiceCollection();
        FunctionHost.Register(Configuration(), Production(), function);
        yield return ("the Function's Program.Register", function);
    }

    [Fact]
    public void EachRoot_RegistersExactlyOneTransport()
    {
        foreach (var (root, services) in Roots())
        {
            services.Where(d => d.ServiceType == typeof(INoticeTransport)).Should().ContainSingle(
                $"{root} is the only thing that gives its host a transport, and a second "
                + "registration would silently replace the first");
        }
    }

    [Fact]
    public void TheThreeRoots_AgreeOnTheTransportsLifetimeAndImplementation()
    {
        var registrations = Roots()
            .Select(r => (
                r.Root,
                Descriptor: r.Services.Single(d => d.ServiceType == typeof(INoticeTransport))))
            .ToList();

        registrations.Should().HaveCount(
            3, "there are three hosts, and every one of them can deliver");

        var (firstRoot, first) = registrations[0];
        foreach (var (root, descriptor) in registrations.Skip(1))
        {
            descriptor.Lifetime.Should().Be(
                first.Lifetime, $"{root} must hold its transport the way {firstRoot} does");
            descriptor.ImplementationType.Should().Be(
                first.ImplementationType,
                $"{root} must deliver where {firstRoot} delivers, or one host quietly keeps "
                + "writing files the day another starts sending");
        }
    }
}
