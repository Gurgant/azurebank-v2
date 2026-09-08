using AzureBank.Functions.NoticeRelay;
using AzureBank.Infrastructure.Data;
using AzureBank.Infrastructure.Notices;
using AzureBank.Shared.Constants;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Shared.Options;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using System.Reflection;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AzureBank.Tests.Unit.Functions;

/// <summary>
/// The Function runner's trigger (ADR-0051): it sweeps when the flag names it, steps aside once
/// when it does not, claims under a <c>func/</c> name, and says when its lease is too short for the
/// interval it is really ticking at.
/// </summary>
/// <remarks>
/// <para>
/// NO FUNCTIONS HOST HERE, and none is needed. A Function class in the isolated worker is an
/// ordinary class the host resolves and calls; constructing it and calling
/// <see cref="DeliverOwedNotices.RunAsync"/> exercises exactly the code a tick exercises. What this
/// CANNOT prove is the wiring — that the trigger binds, that <c>%Notices:Schedule%</c> resolves,
/// that the host starts at all — and nothing here pretends otherwise: that is what
/// <c>func start</c> against Azurite showed, and the ADR carries the transcript.
/// </para>
/// <para>
/// The InMemory context is <c>NoticeRelayServiceTests</c>' idiom, and inherits its limit: the claim
/// runs the load-and-save fallback, never the set-based statement. The sweep those tests drive and
/// the sweep this one drives are the SAME <see cref="NoticeSweep"/>, which is the point of ADR-0051
/// D1 — so what is asserted here is what the Function ADDS around it, not the protocol again.
/// </para>
/// </remarks>
public sealed class DeliverOwedNoticesTests : IDisposable
{
    private const string ChainKey = "notice-function-tests-chain-key-0123456789";
    private const string AnchorKey = "notice-function-tests-anchor-key-987654321";
    private const string Contact = "security@your-bank.example, +00 000 0000";
    private const string Address = "owner@example.com";

    private readonly AzureBankDbContext _context;
    private readonly RecordingTransport _transport = new();
    private readonly RecordingLoggerProvider _logs = new();
    private readonly ILoggerFactory _loggers;
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 8, 10, 0, 0, TimeSpan.Zero));
    private readonly string _directory;

    public DeliverOwedNoticesTests()
    {
        var options = Options.Create(new AuditOptions { ChainKey = ChainKey, AnchorKey = AnchorKey });
        var chain = new AuditChain(options, NullLogger<AuditChain>.Instance);
        _context = new AzureBankDbContext(
            new DbContextOptionsBuilder<AzureBankDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options,
            timeProvider: null,
            auditChain: chain);
        _loggers = LoggerFactory.Create(b => b.AddProvider(_logs));
        _directory = Path.Combine(Path.GetTempPath(), "azurebank-function-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        _context.Dispose();
        _loggers.Dispose();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task WhenTheFlagNamesTheFunction_ItSweeps_AndClaimsUnderAFuncName()
    {
        var owner = await OwnerAsync();
        var notice = await OwedAsync(owner);
        var function = Function(out var host);

        await function.RunAsync(new TimerInfo(), CancellationToken.None);

        var stored = await StoredAsync(notice.Id);
        stored.DeliveredAt.Should().NotBeNull("the flag names this runner, so it delivers");
        stored.LeasedBy.Should().BeNull("the mark clears the lease (ADR-0048 D2)");
        _transport.Envelopes.Should().ContainSingle();

        host.RunnerName.Should().StartWith(NoticeClaim.FunctionKind + "/",
            "the kind prefix is how a person reading LeasedBy knows which runner held a row");
        _logs.Lines.Should().Contain(
            l => l.Level == LogLevel.Information
                 && l.Message.Contains("sweep as " + host.RunnerName)
                 && l.Message.Contains("claimed 1")
                 && l.Message.Contains("delivered 1"));
    }

    [Theory]
    [InlineData(NoticeRunner.None)]
    [InlineData(NoticeRunner.Api)]
    public async Task WhenTheFlagNamesAnotherRunner_ItTouchesNothing(NoticeRunner runner)
    {
        /*
          THE SYMMETRY IS THE POINT. The API's loop delivers only when the flag says Api; this
          delivers only when it says Function. Both step aside otherwise, so a configuration naming
          neither delivers NOTHING — which is the recoverable failure. The alternative shape, where
          each host assumes it is live unless told otherwise, sends every notice twice on the day
          somebody starts both.
        */
        var owner = await OwnerAsync();
        var notice = await OwedAsync(owner);
        var function = Function(out _, flag: runner);

        await function.RunAsync(new TimerInfo(), CancellationToken.None);

        _transport.Envelopes.Should().BeEmpty();
        var stored = await StoredAsync(notice.Id);
        stored.DeliveredAt.Should().BeNull();
        stored.LeasedBy.Should().BeNull("nothing was claimed");
        _logs.Lines.Should().Contain(
            l => l.Level == LogLevel.Information
                 && l.Message.Contains("delivers nothing")
                 && l.Message.Contains(runner.ToString()));
        _logs.Lines.Should().NotContain(l => l.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task ASecondTickReusesTheSameName_SoAHeldRowIsItsOwnToRetry()
    {
        /*
          The runner name is a SINGLETON, and this is why. A row whose delivery failed stays owed and
          HELD under the name that claimed it; the next sweep renews and retries it by reading
          LeasedBy back. A name generated per invocation would leave every failure stranded until its
          lease lapsed and then claim it as a stranger — the duplicate the lease exists to prevent,
          arrived at from the other side.
        */
        var owner = await OwnerAsync();
        var notice = await OwedAsync(owner);
        var function = Function(out var host);

        _transport.Fails = new IOException("the spool is full");
        await function.RunAsync(new TimerInfo(), CancellationToken.None);

        var held = await StoredAsync(notice.Id);
        held.DeliveredAt.Should().BeNull("the transport refused");
        held.LeasedBy.Should().Be(host.RunnerName, "a failed delivery stays held by the runner that claimed it");

        _transport.Fails = null;
        _clock.Advance(TimeSpan.FromSeconds(30));
        await function.RunAsync(new TimerInfo(), CancellationToken.None);

        var delivered = await StoredAsync(notice.Id);
        delivered.DeliveredAt.Should().NotBeNull("the same runner recognised its own held row and retried it");
        _logs.Lines.Should().Contain(l => l.Message.Contains("claimed 0") && l.Message.Contains("delivered 1"),
            "nothing new was claimed: the row was already this runner's");
    }

    [Fact]
    public async Task ALeaseShorterThanTwoTicks_IsAWarningNamingBothNumbers()
    {
        /*
          ADR-0048 D6's rule, checked against the interval this host is ACTUALLY ticking at. The API
          validates LeaseSeconds >= 2 * PeriodSeconds at start and refuses to start; the Function's
          cadence is a trigger expression, not an integer the options carry, so there is no number to
          compare at start. The host measures the gap between its own ticks instead
          (NoticeRelayHostState.ObserveTick) — NOT TimerInfo.ScheduleStatus, which the trigger's
          UseMonitor = false guarantees is never populated. The cost is a running host that is
          misconfigured rather than one that refused to start.
        */
        var owner = await OwnerAsync();
        await OwedAsync(owner);
        var function = Function(out _, leaseSeconds: 30);

        // The first tick measures nothing; the second is 20 s after it, against a 30 s lease.
        await function.RunAsync(new TimerInfo(), CancellationToken.None);
        _logs.Lines.Should().NotContain(l => l.Level == LogLevel.Warning, "the first tick has no interval");

        _clock.Advance(TimeSpan.FromSeconds(20));
        await function.RunAsync(new TimerInfo(), CancellationToken.None);

        _logs.Lines.Should().Contain(
            l => l.Level == LogLevel.Warning
                 && l.Message.Contains("LeaseSeconds is 30s")
                 && l.Message.Contains("ticking every 20s"),
            "the operator needs both numbers IN THEIR ROLES: asserting the bare substrings \"30s\" and "
            + "\"20s\" stays green if the two format arguments are exchanged, which would tell the "
            + "operator to lengthen the wrong one");
    }

    [Fact]
    public async Task ALeaseOfTwiceTheInterval_SaysNothing()
    {
        // The boundary the rule states, from the passing side: exactly twice is enough.
        var owner = await OwnerAsync();
        await OwedAsync(owner);
        var function = Function(out _, leaseSeconds: 40);

        await function.RunAsync(new TimerInfo(), CancellationToken.None);
        _clock.Advance(TimeSpan.FromSeconds(20));
        await function.RunAsync(new TimerInfo(), CancellationToken.None);

        _logs.Lines.Should().NotContain(l => l.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task TheColdStartTick_StillDELIVERS_AndSaysNothingAboutALeaseItCannotJudgeYet()
    {
        /*
          THE HALF THAT IS TESTABLE, and the half that is not — said plainly, because an earlier
          version of this test claimed the second.

          Not testable: the difference between returning null on the first tick and returning
          TimeSpan.Zero. `Lease` is at least 30 s by its own [Range], so `Lease >= 2 * Zero` can
          never be false; a zero would be swallowed by the same guard the null short-circuits, and
          the two are behaviourally identical. Measured: the mutant `interval ?? TimeSpan.Zero`
          leaves this whole suite green. No test can refuse it, so no test here pretends to — the
          reason to prefer null is that a zero would make EVERY lease look sufficient the day the
          guard's shape changes, which is a design argument and not a behaviour.

          Testable, and what this asserts: a cold start still DELIVERS. The lease check runs before
          the sweep, so a first tick that fell into the warning path and returned early would be a
          silent host, and the silence would look exactly like a correctly configured one.
        */
        var owner = await OwnerAsync();
        var notice = await OwedAsync(owner);
        var function = Function(out _, leaseSeconds: 30);

        await function.RunAsync(new TimerInfo(), CancellationToken.None);

        (await StoredAsync(notice.Id)).DeliveredAt.Should().NotBeNull(
            "the first tick of a process delivers; it only declines to judge a lease it has nothing to compare against");
        _logs.Lines.Should().NotContain(l => l.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task WhenItStandsASIDE_ItSaysSoOnceRatherThanOnEveryTick()
    {
        /*
          The API states this fact once, because its loop reads the flag once and returns from
          ExecuteAsync. A Function is re-entered every tick, so the identical sentence would arrive
          on a schedule forever — a standing configuration fact logged as if it were an event, on a
          host that by definition nobody is watching closely. The lease warning repeats deliberately
          because it is a fault that can be fixed while the host runs; this one cannot change
          without a restart.
        */
        var owner = await OwnerAsync();
        await OwedAsync(owner);
        var function = Function(out _, flag: NoticeRunner.Api);

        await function.RunAsync(new TimerInfo(), CancellationToken.None);
        await function.RunAsync(new TimerInfo(), CancellationToken.None);
        await function.RunAsync(new TimerInfo(), CancellationToken.None);

        _logs.Lines.Where(l => l.Message.Contains("delivers nothing")).Should().HaveCount(
            1, "three ticks, one sentence: the flag cannot change under a running host");
        _transport.Envelopes.Should().BeEmpty();
    }

    [Fact]
    public void TheRunnerName_IsStablePerHost_AndFitsTheColumn()
    {
        var host = new NoticeRelayHostState();

        host.RunnerName.Should().StartWith(NoticeClaim.FunctionKind + "/");
        host.RunnerName.Length.Should().BeLessThanOrEqualTo(NoticeClaim.NameWidth, "LeasedBy is nvarchar(64)");
        host.RunnerName.Should().Be(host.RunnerName, "the name is computed once and read many times");
        new NoticeRelayHostState().RunnerName.Should().NotBe(host.RunnerName,
            "two hosts on one machine must differ, or the lease cannot tell them apart");
    }

    [Fact]
    public void TheTriggerPinsUseMonitorFALSE_SoNoCadenceCanGiveItAStartupSweep()
    {
        /*
          THE ONE PROPERTY IN THIS FILE THAT NO BEHAVIOUR CAN REACH, and it is a major one, so it is
          asserted on the metadata instead.

          TimerTriggerAttribute.UseMonitor defaults to TRUE and the generated functions.metadata
          emits no useMonitor key, so the default stands unless the SCHEDULE clears it —
          TimerSchedule.Create forces it false only for expressions that fire more than once a
          minute. At the sample sub-minute cadence it is cleared by luck. At a five-minute schedule,
          which the project README explicitly permits, the ScheduleMonitor attaches and the listener's
          past-due check can invoke this function ON STARTUP: the RunOnStartup behaviour ADR-0051's
          "Alternatives declined" rejects, reached by another door. With a per-process runner name
          (D8) that means every restart claims a fresh batch while the previous one stays leased
          under a dead name.

          Measured: with the attribute argument removed the whole suite stays green, which is why
          this exists. The generated metadata is the other half of the proof and is not reachable
          from a test — `bin/.../functions.metadata` carries "useMonitor": false only because of
          this argument.
        */
        var parameter = typeof(DeliverOwedNotices)
            .GetMethod(nameof(DeliverOwedNotices.RunAsync))!
            .GetParameters()
            .Single(p => p.ParameterType == typeof(TimerInfo));

        var trigger = parameter.GetCustomAttribute<TimerTriggerAttribute>();

        trigger.Should().NotBeNull("the timer trigger is what makes this a Function at all");
        trigger!.Schedule.Should().Be("%Notices:Schedule%",
            "the schedule is bound by the host before any of this code runs, so it must stay a setting reference");
        trigger.UseMonitor.Should().BeFalse(
            "the default is true, and a schedule of a minute or more would then attach the ScheduleMonitor "
            + "and let a restart sweep a past-due tick immediately — the RunOnStartup behaviour ADR-0051 declines");
        trigger.RunOnStartup.Should().BeFalse("the first tick is one interval after start, as the API's loop is");
    }

    [Fact]
    public async Task TheReportedIntervalIsROUNDED_SoTheOperatorIsNotToldToFixTheWrongNumber()
    {
        /*
          `(int)gap.TotalSeconds` truncates toward zero, so a real gap of 19.6 s prints as 19. The
          COMPARISON uses the exact TimeSpan and is unaffected — only the number the operator reads
          is wrong, and that number is the entire point of the warning. It also mattered to the
          record: ADR-0051 D5 quoted "19s then 20s" from a 20-second schedule as evidence that the
          host's real cadence differs from its configured one, when part of that gap was the
          truncation.
        */
        var owner = await OwnerAsync();
        await OwedAsync(owner);
        var function = Function(out _, leaseSeconds: 30);

        await function.RunAsync(new TimerInfo(), CancellationToken.None);
        _clock.Advance(TimeSpan.FromMilliseconds(19_600));
        await function.RunAsync(new TimerInfo(), CancellationToken.None);

        _logs.Lines.Should().Contain(
            l => l.Level == LogLevel.Warning && l.Message.Contains("ticking every 20s"),
            "19.6 seconds is 20 to one decimal place and 19 to a cast; the operator gets the one that is true");
        _logs.Lines.Should().NotContain(
            l => l.Message.Contains("ticking every 19s"));
    }

    [Fact]
    public void StandingAsideIsAnnouncedOnce_HoweverManyTimesItIsAsked()
    {
        var host = new NoticeRelayHostState();

        host.AnnounceStandingAside().Should().BeTrue("the first tick is the one that tells the operator");
        host.AnnounceStandingAside().Should().BeFalse();
        host.AnnounceStandingAside().Should().BeFalse();
    }

    [Fact]
    public void TheFirstTickHasNoIntervalToReport_AndEveryTickAfterItDoes()
    {
        /*
          The host measures its own cadence because the obvious source is empty: TimerInfo's
          ScheduleStatus arrives null in the isolated worker here — six consecutive live ticks across
          two configurations, one with timers.useMonitor unset and one with it true, every one with a
          lease short enough that a warning was due, and not one warning appeared. So the interval
          comes from this host's clock instead.
        */
        var host = new NoticeRelayHostState();
        var t0 = new DateTime(2026, 9, 8, 10, 0, 0, DateTimeKind.Utc);

        host.ObserveTick(t0).Should().BeNull("there is nothing to measure a first tick against");
        host.ObserveTick(t0.AddSeconds(20)).Should().Be(TimeSpan.FromSeconds(20));
        host.ObserveTick(t0.AddSeconds(35)).Should().Be(TimeSpan.FromSeconds(15),
            "each tick is measured against the one before it, not against the first");
        host.ObserveTick(t0.AddSeconds(35)).Should().BeNull(
            "two ticks at the same instant is not an interval of zero; a zero would make every lease "
            + "look sufficient, which is the wrong way for this guard to fail");
    }

    private DeliverOwedNotices Function(
        out NoticeRelayHostState host,
        NoticeRunner flag = NoticeRunner.Function,
        int leaseSeconds = 120)
    {
        host = new NoticeRelayHostState();
        return new DeliverOwedNotices(
            _context,
            _transport,
            Options.Create(new NoticeRelayOptions
            {
                Runner = flag,
                PickupDirectory = _directory,
                Contact = Contact,
                LeaseSeconds = leaseSeconds,
                BatchSize = 100,
            }),
            host,
            _loggers.CreateLogger<DeliverOwedNotices>(),
            _clock);
    }

    private async Task<Guid> OwnerAsync()
    {
        var id = Guid.CreateVersion7();
        _context.Users.Add(new ApplicationUser
        {
            Id = id,
            UserName = id.ToString(),
            Email = Address,
            AzureTag = "owner_" + id.ToString("N")[..8],
            FirstName = "Owner",
            LastName = "Sentinel",
            PinHash = "pin-hash-sentinel",
            PasswordHash = "password-hash-sentinel",
        });
        await _context.SaveChangesAsync();
        return id;
    }

    /// <summary>
    /// An owed notice AND the audit row it belongs to. Both, because a notice with no audit row is
    /// delivered anyway with a Warning (ADR-0047), and a test that skipped the row would be
    /// asserting the absence of warnings against a warning it had caused itself.
    /// </summary>
    private async Task<SubscriberNotice> OwedAsync(Guid owner)
    {
        _context.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.CreateVersion7(),
            OccurredAt = _clock.GetUtcNow().UtcDateTime,
            Event = SecurityEvents.PinEnrolled,
            Outcome = AuditOutcome.Succeeded,
            ActorUserId = owner,
            RowHash = string.Empty,
        });
        var notice = new SubscriberNotice
        {
            Id = Guid.CreateVersion7(),
            UserId = owner,
            Event = SecurityEvents.PinEnrolled,
            OccurredAt = _clock.GetUtcNow().UtcDateTime,
        };
        _context.SubscriberNotices.Add(notice);
        await _context.SaveChangesAsync();
        return notice;
    }

    private async Task<SubscriberNotice> StoredAsync(Guid id) =>
        await _context.SubscriberNotices.AsNoTracking().SingleAsync(n => n.Id == id);
}
