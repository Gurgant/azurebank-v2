using AzureBank.Api.Services;
using AzureBank.AuditVerifier.Commands;
using AzureBank.Infrastructure.Data;
using AzureBank.Infrastructure.Notices;
using AzureBank.Shared.Constants;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Shared.Options;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AzureBank.Tests.Unit.Services;

/// <summary>
/// The relay's sweep and loop, on InMemory (ADR-0048): what a sweep claims, delivers, marks and
/// logs; what it leaves owed; and that the loop ticks, survives a failed sweep, and steps aside for
/// every runner value that is not this process.
/// </summary>
/// <remarks>
/// <para>
/// Hand-built provider, the <c>NotifyCommandTests</c> idiom: the test's own context is the
/// singleton every scope resolves, so every sweep here shares ONE tracked context — unlike the API,
/// where each sweep opens a scope of its own. The claim runs InMemory's load-and-save fallback,
/// never the set-based statement; that path is covered only by <c>NoticeRelaySqlServerTests</c>,
/// and a bug in one is invisible to the other. What InMemory cannot prove — that two runners cannot
/// hold one row — is that file's too.
/// </para>
/// </remarks>
public sealed class NoticeRelayServiceTests : IDisposable
{
    private const string ChainKey = "notice-relay-tests-chain-key-0123456789abcd";
    private const string AnchorKey = "notice-relay-tests-anchor-key-9876543210wxyz";
    private const string Contact = "security@your-bank.example, +00 000 0000";
    private const string Address = "owner@example.com";

    private readonly AzureBankDbContext _context;
    private readonly RecordingTransport _transport = new();
    private readonly RecordingLoggerProvider _logs = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 4, 19, 0, 0, TimeSpan.Zero));
    private readonly string _directory;

    public NoticeRelayServiceTests()
    {
        var options = Options.Create(new AuditOptions { ChainKey = ChainKey, AnchorKey = AnchorKey });
        var chain = new AuditChain(options, NullLogger<AuditChain>.Instance);
        _context = new AzureBankDbContext(
            new DbContextOptionsBuilder<AzureBankDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options,
            timeProvider: null,
            auditChain: chain);
        _directory = Path.Combine(Path.GetTempPath(), "azurebank-relay-unit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        _context.Dispose();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
        {
        }
    }

    private ServiceProvider Provider(INoticeTransport? transport = null)
    {
        var collection = new ServiceCollection();
        collection.AddSingleton<IOptions<AuditOptions>>(
            Options.Create(new AuditOptions { ChainKey = ChainKey, AnchorKey = AnchorKey }));
        collection.AddSingleton(_context);
        collection.AddSingleton(transport ?? _transport);
        collection.AddLogging(b => b.AddProvider(_logs));
        return collection.BuildServiceProvider();
    }

    private NoticeRelayService Relay(
        ServiceProvider provider,
        NoticeRunner runner = NoticeRunner.Api,
        int periodSeconds = 5,
        int batchSize = 100,
        IServiceScopeFactory? scopes = null) =>
        new(
            scopes ?? provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new NoticeRelayOptions
            {
                Runner = runner,
                PickupDirectory = _directory,
                Contact = Contact,
                PeriodSeconds = periodSeconds,
                LeaseSeconds = 120,
                BatchSize = batchSize,
            }),
            provider.GetRequiredService<ILogger<NoticeRelayService>>(),
            _clock);

    private async Task<Guid> OwnerAsync(string? email = Address)
    {
        var id = Guid.CreateVersion7();
        _context.Users.Add(new ApplicationUser
        {
            Id = id,
            UserName = id.ToString(),
            Email = email,
            AzureTag = "owner_" + id.ToString("N")[..8],
            FirstName = "Owner",
            LastName = "Sentinel",
            PinHash = "pin-hash-sentinel",
            PasswordHash = "password-hash-sentinel",
        });
        await _context.SaveChangesAsync();
        return id;
    }

    private async Task<SubscriberNotice> OwedAsync(Guid userId, DateTime? leasedUntil = null, string? leasedBy = null)
    {
        _context.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.CreateVersion7(),
            OccurredAt = DateTime.UtcNow,
            Event = SecurityEvents.PinEnrolled,
            Outcome = AuditOutcome.Succeeded,
            ActorUserId = userId,
            RowHash = string.Empty,
        });
        var notice = new SubscriberNotice
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Event = SecurityEvents.PinEnrolled,
            OccurredAt = new DateTime(2026, 9, 4, 18, 42, 9, DateTimeKind.Utc),
            LeasedUntil = leasedUntil,
            LeasedBy = leasedBy,
        };
        _context.SubscriberNotices.Add(notice);
        await _context.SaveChangesAsync();
        return notice;
    }

    private async Task<SubscriberNotice> StoredAsync(Guid id) =>
        await _context.SubscriberNotices.AsNoTracking().SingleAsync(n => n.Id == id);

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    [Fact]
    public async Task OneSweep_ClaimsDeliversAndMarks_AndClearsTheLease()
    {
        var owner = await OwnerAsync();
        var notice = await OwedAsync(owner);
        await using var provider = Provider();
        var relay = Relay(provider);

        var summary = await relay.SweepAsync(CancellationToken.None);

        summary.Should().Be(new NoticeSweepSummary(Claimed: 1, Delivered: 1, Owed: 0));
        _transport.Envelopes.Should().ContainSingle().Which.ToAddress.Should().Be(Address);

        var stored = await StoredAsync(notice.Id);
        stored.DeliveredAt.Should().NotBeNull();
        stored.DeliveryReceipt.Should().Be(RecordingTransport.Receipt);
        stored.LeasedUntil.Should().BeNull("a delivered row never reads as held");
        stored.LeasedBy.Should().BeNull();

        _logs.Lines.Should().Contain(l => l.Level == LogLevel.Information && l.Message.Contains("delivered notice") && l.Message.Contains(RecordingTransport.Receipt));
    }

    [Fact]
    public async Task TheAddressNeverReachesALogLine_NotEvenInsideAFailure()
    {
        var owner = await OwnerAsync();
        await OwedAsync(owner);
        var failing = await OwnerAsync(email: "bad\r\nBcc: attacker@example.com");
        await OwedAsync(failing);
        await using var provider = Provider();

        await Relay(provider).SweepAsync(CancellationToken.None);

        // A refusal whose MESSAGE carries the address, the verb's own test shape: the type is logged, not it.
        var third = await OwnerAsync();
        await OwedAsync(third);
        _transport.Fails = new IOException($"disk full while writing to {Address}");
        await Relay(provider).SweepAsync(CancellationToken.None);

        _logs.Lines.Should().Contain(l => l.Level == LogLevel.Warning && l.Message.Contains("UnusableAddress"),
            "the refused row must be named, or a sweep that logs nothing for it would pass this test");
        _logs.Lines.Should().Contain(l => l.Level == LogLevel.Warning && l.Message.Contains("IOException"));
        _logs.Lines.Should().OnlyContain(
            l => !l.Message.Contains(Address) && !l.Message.Contains("attacker@") && !l.Message.Contains("disk full"),
            "the address is read for the To: header and printed nowhere — not in a receipt, not in a "
            + "failure's type, not in a warning about an unusable one");
    }

    [Theory]
    [InlineData(NoticeRunner.None, LogLevel.Information)]
    [InlineData(NoticeRunner.Function, LogLevel.Warning)]
    public async Task WhenTheRunnerIsNotThisProcess_TheLoopReturns_AndTouchesNothing(NoticeRunner runner, LogLevel level)
    {
        /*
          The flag is the seam that keeps two KINDS of runner from both sending, so every value that
          is not Api must step aside — Function loudly, because nothing implements it yet and an
          operator who set it believes something is delivering.
        */
        var owner = await OwnerAsync();
        var notice = await OwedAsync(owner);
        await using var provider = Provider();
        var relay = Relay(provider, runner: runner);

        await relay.StartAsync(CancellationToken.None);
        var finished = await Task.WhenAny(relay.ExecuteTask!, Task.Delay(TimeSpan.FromSeconds(5)));

        finished.Should().BeSameAs(relay.ExecuteTask, "with Runner={0} the loop must return at once, not wait a period", runner);
        _transport.Envelopes.Should().BeEmpty();
        var stored = await StoredAsync(notice.Id);
        stored.DeliveredAt.Should().BeNull();
        stored.LeasedBy.Should().BeNull("nothing was claimed");
        _logs.Lines.Should().Contain(l => l.Level == level && l.Message.Contains("delivers nothing") && l.Message.Contains(runner.ToString()));
        await relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task TheLoop_SweepsOncePerPeriod_AndStopsOnCancellation()
    {
        var owner = await OwnerAsync();
        var notice = await OwedAsync(owner);
        await using var provider = Provider();
        var relay = Relay(provider, periodSeconds: 5);

        await relay.StartAsync(CancellationToken.None);
        (await StoredAsync(notice.Id)).DeliveredAt.Should().BeNull("the first look is one full period after start");

        var ticks = await AdvanceUntilAsync(async () => (await StoredAsync(notice.Id)).DeliveredAt is not null);
        ticks.Should().BeGreaterThanOrEqualTo(5, "nothing is delivered before one full period has passed on the clock");

        await relay.StopAsync(CancellationToken.None);
        relay.ExecuteTask!.IsCompleted.Should().BeTrue("cancellation ends the loop");
        _logs.Lines.Should().NotContain(l => l.Level == LogLevel.Error);
    }

    [Fact]
    public async Task OneFailingSweep_IsLoggedAtError_AndTheNextTickStillDelivers()
    {
        /*
          A failure BEFORE any notice is reached — here the scope itself, the shape of a store that
          cannot be opened — escapes to the loop's catch-all: logged at Error, and the loop survives
          to deliver on the next tick. A transport failure never gets here; it is contained per
          notice (the theory below).
        */
        var owner = await OwnerAsync();
        var notice = await OwedAsync(owner);
        await using var provider = Provider();
        var scopes = new ThrowingOnceScopeFactory(provider.GetRequiredService<IServiceScopeFactory>());
        var relay = Relay(provider, periodSeconds: 5, scopes: scopes);

        await relay.StartAsync(CancellationToken.None);
        await AdvanceUntilAsync(() => Task.FromResult(_logs.Lines.Any(l => l.Level == LogLevel.Error && l.Message.Contains("sweep failed"))));
        (await StoredAsync(notice.Id)).LeasedBy.Should().BeNull("the sweep failed before it could claim");

        await AdvanceUntilAsync(async () => (await StoredAsync(notice.Id)).DeliveredAt is not null);

        await relay.StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(InvalidOperationException))]
    public async Task ATransportFailureOfAnyType_IsContainedPerNotice_AndOnlyTheTypeIsLogged(Type failure)
    {
        // Whatever a transport throws short of cancellation — an I/O refusal today, a provider's
        // refusal with the recipient in its message tomorrow — stays with the notice: owed, held, typed.
        var owner = await OwnerAsync();
        var notice = await OwedAsync(owner);
        await using var provider = Provider();
        var relay = Relay(provider);
        _transport.Fails = (Exception)Activator.CreateInstance(failure, $"refused for {Address}")!;

        var summary = await relay.SweepAsync(CancellationToken.None);

        summary.Should().Be(new NoticeSweepSummary(Claimed: 1, Delivered: 0, Owed: 1));
        (await StoredAsync(notice.Id)).LeasedBy.Should().Be(relay.RunnerName);
        _logs.Lines.Should().Contain(l => l.Level == LogLevel.Warning && l.Message.Contains(failure.Name));
        _logs.Lines.Should().NotContain(l => l.Level == LogLevel.Error, "a transport failure is never a failed sweep");
        _logs.Lines.Should().OnlyContain(l => !l.Message.Contains(Address));
    }

    [Fact]
    public async Task AClaimIsBoundedToTheBatch_AndTheNextSweepTakesTheRest()
    {
        var owner = await OwnerAsync();
        await OwedAsync(owner);
        await OwedAsync(owner);
        await OwedAsync(owner);
        await using var provider = Provider();
        var relay = Relay(provider, batchSize: 2);

        (await relay.SweepAsync(CancellationToken.None)).Should().Be(new NoticeSweepSummary(Claimed: 2, Delivered: 2, Owed: 0));
        (await relay.SweepAsync(CancellationToken.None)).Should().Be(new NoticeSweepSummary(Claimed: 1, Delivered: 1, Owed: 0));
        (await relay.SweepAsync(CancellationToken.None)).Claimed.Should().Be(0);
    }

    [Fact]
    public async Task TheVerbStopsWhenItsLeaseLapses_AndNamesWhatItLeft()
    {
        var owner = await OwnerAsync();
        await OwedAsync(owner);
        await OwedAsync(owner);
        var collection = new ServiceCollection();
        collection.AddSingleton<IOptions<AuditOptions>>(Options.Create(new AuditOptions { ChainKey = ChainKey, AnchorKey = AnchorKey }));
        collection.AddSingleton(_context);
        collection.AddSingleton<INoticeTransport>(new LeaseLapsingTransport(_transport, _clock, TimeSpan.FromMinutes(3)));
        collection.AddSingleton<TimeProvider>(_clock);
        await using var provider = collection.BuildServiceProvider();

        var (exitCode, lines) = await NotifyCommand.RunAsync(provider, _directory, Contact, CancellationToken.None);

        exitCode.Should().Be(AnchorCommand.NotRecorded, "one notice is still owed after the run");
        var text = string.Join("\n", lines);
        text.Should().Contain("NOTIFIED 1 of 2").And.Contain("LEASE LAPSED");
        _transport.Envelopes.Should().ContainSingle("the second row was not delivered under a lease the verb no longer held");
    }

    [Fact]
    public async Task ATransportFailure_LeavesTheRowOwedAndHeld_RetriesItByName_AndFreesItWhenTheLeaseLapses()
    {
        /*
          AT-LEAST-ONCE, PINNED, in the shape a recording transport can show: the lease is not
          cleared on failure, the same runner retries what it still holds, and once the lease lapses
          the row is free to any claim. With the pickup directory a retry after a completed write is
          refused by the exclusive create; with a sending transport it would go out twice.
        */
        var owner = await OwnerAsync();
        var notice = await OwedAsync(owner);
        await using var provider = Provider();
        var relay = Relay(provider);

        _transport.Fails = new IOException("disk full — and this message must not surface");
        var first = await relay.SweepAsync(CancellationToken.None);

        first.Should().Be(new NoticeSweepSummary(Claimed: 1, Delivered: 0, Owed: 1));
        var afterFailure = await StoredAsync(notice.Id);
        afterFailure.DeliveredAt.Should().BeNull("nothing was marked");
        afterFailure.LeasedBy.Should().Be(relay.RunnerName, "the lease stands until it lapses");

        // The same runner, seconds later and before the lease lapses: nothing new is free, but what
        // it holds is retried — found by NAME. The clock moves so that a re-read keyed on the instant
        // the first claim wrote would find nothing; that is the bug the name-keyed re-read exists for.
        _transport.Fails = null;
        _clock.Advance(TimeSpan.FromSeconds(7));
        var second = await relay.SweepAsync(CancellationToken.None);
        second.Should().Be(new NoticeSweepSummary(Claimed: 0, Delivered: 1, Owed: 0),
            "a row this runner still holds is delivered by name without waiting out the lease");
        (await StoredAsync(notice.Id)).DeliveredAt.Should().NotBeNull();

        // A second row, failed and then lapsed: free to a different runner's claim.
        var other = await OwedAsync(owner);
        _transport.Fails = new IOException("again");
        await relay.SweepAsync(CancellationToken.None);
        _transport.Fails = null;
        _clock.Advance(TimeSpan.FromSeconds(121));
        var stranger = Relay(provider);
        (await stranger.SweepAsync(CancellationToken.None)).Should().Be(new NoticeSweepSummary(Claimed: 1, Delivered: 1, Owed: 0));
        (await StoredAsync(other.Id)).DeliveredAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ASweepThatOutlivesItsLease_StopsDelivering()
    {
        var owner = await OwnerAsync();
        await OwedAsync(owner);
        await OwedAsync(owner);
        await using var provider = Provider(new LeaseLapsingTransport(_transport, _clock, TimeSpan.FromSeconds(121)));
        var relay = Relay(provider);

        var summary = await relay.SweepAsync(CancellationToken.None);

        summary.Claimed.Should().Be(2);
        summary.Delivered.Should().Be(1, "the first row went out; by the second the lease had lapsed");
        summary.Owed.Should().Be(1, "the rest are free to the next claim, not delivered under a lease this runner no longer holds");
        _logs.Lines.Should().Contain(l => l.Level == LogLevel.Warning && l.Message.Contains("lease lapsed mid-sweep"));
    }

    [Fact]
    public async Task WhenTheDirectoryVanishesAfterStart_TheRowStaysOwedAndHeld_AndOnlyTheTypeIsLogged()
    {
        // Through the REAL transport: validation saw the directory at start; it is gone by the sweep.
        var owner = await OwnerAsync();
        var notice = await OwedAsync(owner);
        await using var provider = Provider(new PickupDirectoryTransport());
        var relay = Relay(provider);
        Directory.Delete(_directory, recursive: true);

        var summary = await relay.SweepAsync(CancellationToken.None);

        summary.Should().Be(new NoticeSweepSummary(Claimed: 1, Delivered: 0, Owed: 1));
        var stored = await StoredAsync(notice.Id);
        stored.DeliveredAt.Should().BeNull();
        stored.LeasedBy.Should().Be(relay.RunnerName);
        _logs.Lines.Should().Contain(l => l.Level == LogLevel.Warning && l.Message.Contains("DirectoryNotFoundException"));
        _logs.Lines.Should().OnlyContain(l => !l.Message.Contains(Address));
    }

    [Theory]
    [InlineData("GURGANT")]
    [InlineData("a-hostname-so-long-it-would-push-the-suffix-off-the-end-of-the-column-if-nothing-cut-it-first")]
    public void TheRunnerName_FitsTheColumn_AndKeepsItsSuffix(string host)
    {
        var id = Guid.NewGuid();
        var name = NoticeClaim.RunnerNameFor("api", host, 32384, id);

        name.Length.Should().BeLessThanOrEqualTo(NoticeClaim.NameWidth);
        name.Should().EndWith($"/32384/{id.ToString("N")[..8]}", "the disambiguating suffix survives; the host is what gets cut");
        name.Should().StartWith("api/");
    }

    [Fact]
    public async Task TheVerbLeavesARowALiveRunnerHolds_AndSaysSo()
    {
        var owner = await OwnerAsync();
        var notice = await OwedAsync(owner, leasedUntil: DateTime.UtcNow.AddMinutes(2), leasedBy: "api/HOST/1/abcdef12");
        await using var provider = Provider();

        var (exitCode, lines) = await NotifyCommand.RunAsync(provider, _directory, Contact, CancellationToken.None);

        exitCode.Should().Be(VerifyCommand.NothingToVerify, "nothing was FREE for this run; not a success and not a failure");
        string.Join("\n", lines).Should().Contain("leased by a live runner");
        _transport.Envelopes.Should().BeEmpty("the verb must not render what the relay is delivering");
        (await StoredAsync(notice.Id)).DeliveredAt.Should().BeNull();
    }

    [Fact]
    public async Task TheVerbClaimsWhatItDelivers_TakesALapsedLease_AndNamesWhatItLeft()
    {
        var owner = await OwnerAsync();
        var lapsed = await OwedAsync(owner, leasedUntil: DateTime.UtcNow.AddSeconds(-1), leasedBy: "api/HOST/1/dead0000");
        var held = await OwedAsync(owner, leasedUntil: DateTime.UtcNow.AddMinutes(2), leasedBy: "api/HOST/1/live0000");
        await using var provider = Provider();

        var (exitCode, lines) = await NotifyCommand.RunAsync(provider, _directory, Contact, CancellationToken.None);

        exitCode.Should().Be(VerifyCommand.Intact);
        var text = string.Join("\n", lines);
        text.Should().Contain("NOTIFIED 1 of 1").And.Contain("1 more owed notice(s) are leased by a live runner");
        _transport.Envelopes.Should().ContainSingle();
        var stored = await StoredAsync(lapsed.Id);
        stored.DeliveredAt.Should().NotBeNull();
        stored.LeasedBy.Should().BeNull("the mark clears the verb's own lease too");
        (await StoredAsync(held.Id)).LeasedBy.Should().Be("api/HOST/1/live0000", "the other runner's row is untouched");
    }

    /// <summary>
    /// Advances the fake clock one second per poll until the condition holds, and returns how many
    /// seconds it took. One-second steps rather than one jump of a period: StartAsync returns before
    /// ExecuteAsync has built its PeriodicTimer (measured on .NET 10 — a single Advance issued
    /// straight after StartAsync landed before the timer existed and it never fired), so the clock
    /// is walked forward while the loop catches up, and a tick lands within a period of the timer's
    /// creation whichever thread got there first.
    /// </summary>
    private async Task<int> AdvanceUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        var ticks = 0;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return ticks;
            }

            _clock.Advance(TimeSpan.FromSeconds(1));
            ticks++;
            await Task.Delay(25);
        }

        throw new TimeoutException(
            "the condition did not become true within 10 s of wall clock; log so far: "
            + string.Join(" || ", _logs.Lines.Select(l => $"[{l.Level}] {l.Message}")));
    }

    /// <summary>Throws on the first scope only: a store that could not be opened once.</summary>
    private sealed class ThrowingOnceScopeFactory(IServiceScopeFactory inner) : IServiceScopeFactory
    {
        private int _calls;

        public IServiceScope CreateScope() =>
            Interlocked.Increment(ref _calls) == 1
                ? throw new InvalidOperationException("the store could not be opened — and this message carries nothing sensitive")
                : inner.CreateScope();
    }

    /// <summary>Delivers through the inner transport, then lapses the fake clock past the lease.</summary>
    private sealed class LeaseLapsingTransport(INoticeTransport inner, FakeTimeProvider clock, TimeSpan advance) : INoticeTransport
    {
        public async Task<string> DeliverAsync(RenderedNotice notice, string toAddress, string directory, CancellationToken cancellationToken)
        {
            var receipt = await inner.DeliverAsync(notice, toAddress, directory, cancellationToken);
            clock.Advance(advance);
            return receipt;
        }
    }
}
