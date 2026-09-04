using AzureBank.Api.Extensions;
using AzureBank.Shared.Options;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace AzureBank.Tests.Integration;

/// <summary>
/// What the real composition root does with the <c>Notices</c> section (ADR-0048): off by default,
/// and a partial configuration is refused by the options validator before any sweep can run.
/// </summary>
/// <remarks>
/// <para>
/// The refusals go through <c>AddApplicationServices</c> on a bare service collection — the
/// <c>RealCompositionRootRefusalTests</c> idiom — and read the <see cref="OptionsValidationException"/>
/// the first resolution throws. Not through a started host: a WebApplicationFactory whose host fails
/// to start surfaces an ObjectDisposedException in place of the validator's message (measured), and
/// the message is the thing under test, because it is the only thing an operator sees.
/// </para>
/// <para>
/// The two cases that expect a host to START do use the factory, because that is the claim.
/// </para>
/// </remarks>
public sealed class NoticeRelayStartupTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "azurebank-relay-" + Guid.NewGuid().ToString("N"));

    public NoticeRelayStartupTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] notices)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] =
                @"Server=(localdb)\MSSQLLocalDB;Database=Unreached;Trusted_Connection=True",
            ["Audit:ChainKey"] = CustomWebApplicationFactory.AuditChainKey,
            ["Audit:AnchorKey"] = CustomWebApplicationFactory.AuditAnchorKey,
        };
        foreach (var (key, value) in notices)
        {
            values[key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    /// <summary>The real root's answer to this Notices section: the validator's failures, joined.</summary>
    private static string RefusalOf(params (string Key, string Value)[] notices)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApplicationServices(Configuration(notices));
        using var provider = services.BuildServiceProvider();

        var resolve = () => provider.GetRequiredService<IOptions<NoticeRelayOptions>>().Value;
        var refusal = resolve.Should().Throw<OptionsValidationException>(
            "a partial Notices section must be refused by the options validator the real root registers").Which;
        return string.Join("\n", refusal.Failures);
    }

    private static WebApplicationFactory<Program> Host(params (string Key, string Value)[] settings)
    {
        var factory = new CustomWebApplicationFactory();
        return factory.WithWebHostBuilder(builder =>
        {
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }
        });
    }

    [Fact]
    public void ByDefault_NoRunnerIsLive_AndTheHostStarts()
    {
        // The default under test is appsettings.json's explicit "Runner": "None", which the Testing
        // environment inherits; the class initialiser says the same, and a default of Function
        // would also start, so the value is asserted rather than the start alone.
        using var factory = Host();
        using var client = factory.CreateClient();
        var options = factory.Services.GetRequiredService<IOptions<NoticeRelayOptions>>().Value;
        options.Runner.Should().Be(NoticeRunner.None, "nothing delivers unless somebody names a runner");
    }

    [Fact]
    public void RunnerApi_WithoutAContact_IsRefused_AndTheMessageNamesTheKey()
    {
        RefusalOf(("Notices:Runner", "Api"), ("Notices:PickupDirectory", _directory))
            .Should().Contain("Notices:Contact").And.Contain("800-63B-4");
    }

    [Fact]
    public void RunnerApi_WithoutADirectory_IsRefused()
    {
        RefusalOf(("Notices:Runner", "Api"), ("Notices:Contact", "security@your-bank.example"))
            .Should().Contain("Notices:PickupDirectory").And.Contain("EXISTING");
    }

    [Fact]
    public void RunnerApi_WithADirectoryInsideAGitRepository_IsRefused()
    {
        // The repository's own tree: the one place a spool of addresses must never land.
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null
               && !Directory.Exists(Path.Combine(repo.FullName, ".git"))
               && !File.Exists(Path.Combine(repo.FullName, ".git")))
        {
            repo = repo.Parent;
        }

        repo.Should().NotBeNull(
            "this test runs from a checkout, as its sibling in NotifyCommandTests asserts; a run that "
            + "found no .git would prove nothing and must not pass green");

        var inside = Path.Combine(AppContext.BaseDirectory, "relay-inside-repo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(inside);
        try
        {
            RefusalOf(
                ("Notices:Runner", "Api"),
                ("Notices:PickupDirectory", inside),
                ("Notices:Contact", "security@your-bank.example"))
                .Should().Contain("inside a git repository");
        }
        finally
        {
            Directory.Delete(inside, recursive: true);
        }
    }

    [Fact]
    public void RunnerApi_WithALeaseNoLongerThanThePeriod_IsRefused()
    {
        RefusalOf(
            ("Notices:Runner", "Api"),
            ("Notices:PickupDirectory", _directory),
            ("Notices:Contact", "security@your-bank.example"),
            ("Notices:PeriodSeconds", "60"),
            ("Notices:LeaseSeconds", "100"))
            .Should().Contain("LeaseSeconds").And.Contain("exceed").And.Contain("twice");
    }

    [Fact]
    public void APeriodBelowTheRange_IsRefused_WhateverTheRunner()
    {
        // Pins that ValidateDataAnnotations() is actually chained: the [Range] alone is decoration.
        RefusalOf(("Notices:Runner", "None"), ("Notices:PeriodSeconds", "4"))
            .Should().Contain("PeriodSeconds");
    }

    [Fact]
    public void RunnerNone_NeedsNothingElse()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApplicationServices(Configuration(("Notices:Runner", "None")));
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<NoticeRelayOptions>>().Value;
        options.Runner.Should().Be(NoticeRunner.None);
        options.PickupDirectory.Should().BeNull("no directory is asked for when nobody delivers");
    }

    [Fact]
    public void RunnerApi_FullyConfigured_Starts()
    {
        using var factory = Host(
            ("Notices:Runner", "Api"),
            ("Notices:PickupDirectory", _directory),
            ("Notices:Contact", "security@your-bank.example, +00 000 0000"));
        var start = () => factory.CreateClient();
        start.Should().NotThrow(
            "a directory outside git, a contact and a lease of at least twice the period is the whole of what the relay needs");
    }
}
