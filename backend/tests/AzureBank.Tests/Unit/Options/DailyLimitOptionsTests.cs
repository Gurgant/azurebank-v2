using AzureBank.Api.Extensions;
using AzureBank.Shared.Options;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

// The folder's existing namespace: a namespace literally named "Options" under Unit would shadow
// `Options.Create(...)` in every sibling test file.
namespace AzureBank.Tests.Unit.Configuration;

/// <summary>
/// The day's ceiling (ADR-0050) is validated at STARTUP, not on the first transfer.
/// </summary>
/// <remarks>
/// Driven through <see cref="IStartupValidator"/>, which is what <c>ValidateOnStart()</c> registers
/// and what the host runs before it listens — so these prove the registration, not merely the two
/// predicates. A rule on <c>IOptions&lt;T&gt;.Value</c> alone would pass with
/// <c>ValidateOnStart</c> deleted, and the misconfiguration would surface as a 500 on the first
/// external transfer.
/// </remarks>
public class DailyLimitOptionsTests
{
    private static IServiceProvider Root(string? amount, string? lockTimeoutSeconds = null)
    {
        var values = new Dictionary<string, string?>();
        if (amount is not null)
        {
            values["DailyLimit:Amount"] = amount;
        }

        if (lockTimeoutSeconds is not null)
        {
            values["DailyLimit:LockTimeoutSeconds"] = lockTimeoutSeconds;
        }

        var services = new ServiceCollection();
        services.AddDailyLimit(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData("0", "must be positive")]
    [InlineData("-1", "must be positive")]
    [InlineData("5000.001", "at most 2 decimals")]
    public void AValueTheCeilingCannotBe_StopsTheHostAtStart(string amount, string rule)
    {
        var root = Root(amount);

        var act = () => root.GetRequiredService<IStartupValidator>().Validate();

        act.Should().Throw<OptionsValidationException>()
            .Which.Message.Should().Contain(rule);
    }

    [Theory]
    [InlineData("5000", 5000)]
    [InlineData("499.99", 499.99)]
    [InlineData(null, 5000)] // no section: the default is valid, so a host without one starts
    public void AValidValue_PassesStartupValidation_AndBinds(string? amount, double expected)
    {
        var root = Root(amount);

        var act = () => root.GetRequiredService<IStartupValidator>().Validate();

        act.Should().NotThrow();
        root.GetRequiredService<IOptions<DailyLimitOptions>>().Value.Amount
            .Should().Be((decimal)expected);
    }

    [Theory]
    [InlineData("0")]   // sp_getapplock reads 0 as DO NOT WAIT: every same-payer overlap faults
    [InlineData("-1")]  // sp_getapplock's own "wait forever" — the bug the option exists to remove
    [InlineData("30")]  // the CommandTimeout itself: the statement would expire, not the wait
    [InlineData("300")]
    public void AWaitBoundTheApplockCannotTake_StopsTheHostAtStart(string seconds)
    {
        /*
          THE [Range] IS ONLY ENFORCED BECAUSE AddDailyLimit CALLS ValidateDataAnnotations, and that
          is the whole point of this test — it is the lesson Audit:TailTimeoutSeconds records one
          registration above: without the call the attribute is decoration, the bad value binds
          happily, and the failure arrives as a fault on a busy transfer rather than at startup.
          Delete the line and every case here goes red.
        */
        var root = Root(amount: null, lockTimeoutSeconds: seconds);

        var act = () => root.GetRequiredService<IStartupValidator>().Validate();

        act.Should().Throw<OptionsValidationException>()
            .Which.Message.Should().Contain("DailyLimit:LockTimeoutSeconds must be between 1 and 29");
    }

    [Theory]
    [InlineData(null, 10)] // no section: the default is valid, so a host without one starts
    [InlineData("2", 2)]   // what the SQL Server proof of the bound sets through UseSetting
    [InlineData("29", 29)] // the largest whole second strictly below the 30s CommandTimeout
    public void AValidWaitBound_PassesStartupValidation_AndBinds(string? seconds, int expected)
    {
        var root = Root(amount: null, lockTimeoutSeconds: seconds);

        var act = () => root.GetRequiredService<IStartupValidator>().Validate();

        act.Should().NotThrow();
        var options = root.GetRequiredService<IOptions<DailyLimitOptions>>().Value;
        options.LockTimeoutSeconds.Should().Be(expected);
        options.LockTimeoutMilliseconds.Should().Be(
            expected * 1_000, "sp_getapplock's @LockTimeout is in milliseconds, and the conversion "
            + "lives beside the range rather than at the call site");
    }

    [Fact]
    public void TheClock_IsRegisteredBesideTheOption_AndIsTheSystemOne()
    {
        // NOT the first registration, and the sentence this comment first carried — the one
        // AzureBankDbContext's comment "deferred" — was wrong: TheFrameworkRegistersTheClockFirst
        // below pins that AddAuthentication already put TimeProvider.System in the container. What
        // this asserts is that AddDailyLimit ALONE, on a bare collection with no authentication,
        // still resolves the system clock — one singleton, so the context's stamp and the
        // helper's window read the same instant.
        var root = Root(null);

        root.GetRequiredService<TimeProvider>().Should().BeSameAs(TimeProvider.System);
    }

    /// <summary>The real root — <c>AddApplicationServices</c> — with this one value set.</summary>
    private static IServiceProvider RealRoot(string amount)
    {
        // Every secret the root validates at start, so the ONE failure left is the ceiling's —
        // IStartupValidator aggregates, and an aggregate hides which rule this test is about.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Idempotency:HashKey"] = CustomWebApplicationFactory.IdempotencyHashKey,
            ["StepUp:BindingKey"] = CustomWebApplicationFactory.StepUpBindingKey,
            ["Security:PinPepper"] = CustomWebApplicationFactory.PinPepper,
            ["Audit:ChainKey"] = CustomWebApplicationFactory.AuditChainKey,
            ["Audit:AnchorKey"] = CustomWebApplicationFactory.AuditAnchorKey,
            ["DailyLimit:Amount"] = amount,
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApplicationServices(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void ThroughTheRealRoot_AZeroCeiling_IsRefused_ByTheValidatorTheHostRunsAtStart()
    {
        // The NoticeRelayStartupTests idiom: the real composition root, resolved. The registration
        // AddApplicationServices makes is the one the host validates before it listens.
        using var root = (ServiceProvider)RealRoot("0");

        var act = () => root.GetRequiredService<IStartupValidator>().Validate();

        act.Should().Throw<OptionsValidationException>()
            .Which.Message.Should().Contain("DailyLimit:Amount must be positive");
    }

    [Fact]
    public void ThroughTheCompositionRoot_AZeroCeiling_LeavesNoHostToServe()
    {
        /*
          The real host, the real ValidateOnStart, the same UseSetting path an appsettings value
          takes. The TYPE cannot be asserted here, and the reason is worth knowing: Program.cs wraps
          the run in a catch that logs "Application terminated unexpectedly" and lets the process
          end — which is the intended production outcome — so WebApplicationFactory finds a host
          that never came up and reports a disposed provider. The type is pinned by the test above,
          on the same registration; this one pins that the host really does not serve.

          Throw<Exception> ALONE would be green for any host-start fault at all — a missing test
          key, a provider collision — so the refusal is read back out of the log instead. The line
          that carries it is the Host's own "Hosting failed to start", logged at Error through
          ILogger<Host> from DI, which is where CustomWebApplicationFactory.CaptureLog's sink sits
          (registered in DI and picked up by ReadFrom.Services). Program.cs's own
          Log.Fatal("Application terminated unexpectedly") is NOT the line to look for: Program.cs
          builds a bootstrap logger and passes preserveStaticLogger: true, so the static logger
          never joins the DI pipeline and that Fatal reaches the console only.

          The line the sink actually receives, observed while writing this test (the assertion below
          was inverted once so FluentAssertions would print the whole queue):

              [Error] Hosting failed to start | OptionsValidationException: DailyLimit:Amount must
              be positive

          — one line, wrapped here. The only other entry in the queue is EF's global-query-filter
          warning, which is why the catch-all was worth replacing.
        */
        using var factory = new CustomWebApplicationFactory();
        factory.SetDailyLimit(0m);
        factory.CaptureLog();

        var act = () => factory.CreateClient();

        act.Should().Throw<Exception>("a host with an impossible ceiling must not come up");
        factory.CapturedLog.Should().Contain(
            line => line.Contains("Hosting failed to start", StringComparison.Ordinal)
                && line.Contains("DailyLimit:Amount must be positive", StringComparison.Ordinal),
            "the refusal must be the ceiling's own rule, not any other host-start fault");
    }

    [Fact]
    public void TheFrameworkRegistersTheClockFirst()
    {
        // What AddDailyLimit's clock comment claims, pinned: Program.cs runs AddIdentityServices()
        // (→ AddIdentity → AddAuthentication) and AddJwtAuthentication() BEFORE
        // AddApplicationServices(), and AddAuthentication does
        // TryAddSingleton(TimeProvider.System), so in the real host AddDailyLimit's own TryAdd
        // registers nothing. Descriptors only — nothing is built here, so the bare collection
        // needs no DbContext, no configuration and no secret. The FIRST of Program.cs's two paths
        // is enough to pin it: whichever runs first is the one that wins the TryAdd.
        var services = new ServiceCollection();
        services.AddIdentityServices();

        var framework = services.Should().ContainSingle(d => d.ServiceType == typeof(TimeProvider))
            .Which;
        framework.ImplementationInstance.Should().BeSameAs(
            TimeProvider.System,
            "AddAuthentication registers the system clock, so the host already carries it when "
            + "AddApplicationServices runs");

        services.AddDailyLimit(new ConfigurationBuilder().Build());

        services.Should().ContainSingle(d => d.ServiceType == typeof(TimeProvider))
            .Which.Should().BeSameAs(
                framework,
                "AddDailyLimit's TryAddSingleton keeps the framework's descriptor rather than "
                + "adding one — which is why AzureBankDbContext was already receiving "
                + "TimeProvider.System on main, before this change registered it explicitly");
    }
}
