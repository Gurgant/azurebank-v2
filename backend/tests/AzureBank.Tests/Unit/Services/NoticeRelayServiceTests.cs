using System.Reflection;
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
using FluentAssertions.Execution;
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

    // The store's name, kept so a test can open a SECOND context over it. That is the only way to
    // be another runner here: the sweep resolves this fixture's own context, so a row marked
    // through it would be the same tracked entity and its concurrency token could never conflict.
    private readonly string _database;

    public NoticeRelayServiceTests()
    {
        var options = Options.Create(new AuditOptions { ChainKey = ChainKey, AnchorKey = AnchorKey });
        var chain = new AuditChain(options, NullLogger<AuditChain>.Instance);
        _database = Guid.NewGuid().ToString();
        _context = new AzureBankDbContext(
            new DbContextOptionsBuilder<AzureBankDbContext>()
                .UseInMemoryDatabase(_database)
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

    private async Task<SubscriberNotice> OwedAsync(
        Guid userId, DateTime? leasedUntil = null, string? leasedBy = null, string? @event = null)
    {
        var kind = @event ?? SecurityEvents.PinEnrolled;
        _context.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.CreateVersion7(),
            OccurredAt = DateTime.UtcNow,
            Event = kind,
            Outcome = AuditOutcome.Succeeded,
            ActorUserId = userId,
            RowHash = string.Empty,
        });
        var notice = new SubscriberNotice
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Event = kind,
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
    [InlineData(NoticeRunner.None)]
    [InlineData(NoticeRunner.Function)]
    public async Task WhenTheRunnerIsNotThisProcess_TheLoopReturns_AndTouchesNothing(NoticeRunner runner)
    {
        /*
          The flag is the seam that keeps two KINDS of runner from both sending, so every value that
          is not Api must step aside.

          BOTH AT INFORMATION SINCE ADR-0051, and `Function` is the row that moved. It used to be a
          WARNING, because the value named a runner nothing implemented and an operator who set it
          believed something was delivering. `AzureBank.Functions.NoticeRelay` is that runner, so the
          warning's premise is gone: `Runner=Function` is now a correct configuration in which this
          process is simply not the one delivering — the same fact `None` states, and a Warning for a
          correct configuration is the kind of line that teaches an operator to ignore warnings.

          The message is asserted NOT to carry the old claim, because a level can be changed while
          the sentence that justified it survives.
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
        _logs.Lines.Should().Contain(
            l => l.Level == LogLevel.Information
                 && l.Message.Contains("delivers nothing")
                 && l.Message.Contains(runner.ToString()));
        _logs.Lines.Should().NotContain(
            l => l.Level >= LogLevel.Warning,
            "stepping aside is not a fault: since ADR-0051 every runner value this process is not "
            + "names a runner that exists, so there is nothing to warn about. GREATER-OR-EQUAL, not "
            + "equal: == LogLevel.Warning would let an Error through a guard whose whole claim is "
            + "that nothing above Information is logged, and the Function's twin in "
            + "DeliverOwedNoticesTests already reads >=");
        _logs.Lines.Should().NotContain(
            l => l.Message.Contains("nothing in this repository implements"),
            "the sentence that justified the old Warning is false now that the Function ships");
        await relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task TheLineThatANNOUNCESTheLoop_CarriesTheKindPrefixedNameAndEveryNumberItClaims()
    {
        /*
          A DOCUMENT ASSERTED THIS LINE AND NOTHING PINNED IT, which is this repository's own rule
          about comments applied to a transcript. ADR-0048's Consequences pasted an API log line the
          format string could not emit: no `api/` on the runner name and no `batch` field at all.
          It was not drift — `git log -S` puts the transcript, the kind prefix and the
          `batch {BatchSize}` field in ONE commit, 9cc6c4e. It was wrong on the day it was written
          and stayed wrong for FOUR DAYS — 2026-09-05 to 2026-09-09 — because nothing re-derived it.
          (This said "four months" when it was written. The repo's first commit is 2026-07-12, so
          four months was impossible: a number written for its ring rather than derived.)

          It matters now rather than then: ADR-0051 D7 makes the kind the thing a person matches
          `LeasedBy` against during an incident, and that ADR line is the only place showing a
          reader a WHOLE rendered API log line. (Not "the only place showing what an API runner
          name looks like" — `NoticeRelayService`'s XML doc carries `api/{host}/{pid}/{8 hex}` and
          is on main, and three literals in this file spell out `api/HOST/1/…`.)

          The poll is not decoration. BackgroundService.StartAsync returns before ExecuteAsync has
          reached this log call — measured: asserting straight after StartAsync captured ZERO lines,
          not a wrong one.
        */
        await using var provider = Provider();
        var relay = Relay(provider, periodSeconds: 5);

        await relay.StartAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && !_logs.Lines.Any(l => l.Message.Contains("live as")))
        {
            await Task.Delay(25);
        }

        await relay.StopAsync(CancellationToken.None);

        _logs.Lines.Should().ContainSingle(
            l => l.Level == LogLevel.Information && l.Message.Contains("live as"),
            "the loop announces itself once, before its first period").Which.Message
            .Should().MatchRegex(
                @"^Notice relay: live as api/[^/]+/\d+/[0-9a-f]{8}, every 5s, lease 120s, batch 100, into ",
                "the name carries the KIND a reader matches against LeasedBy (ADR-0051 D7), and "
                + "every number the line claims is one the loop actually runs on");
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
    public async Task AKindThisBuildCannotRender_StaysOWED_AndTheSweepNamesTheEvent()
    {
        /*
          THE Unrenderable ARM, WHICH NOTHING REACHED. Six outcomes leave this switch and two of them
          had no test anywhere in the suite — measured: `git grep MarkedByAnother\|Unrenderable --
          backend/tests` returned nothing. The verb's own suite covers the kind at ITS level
          (AKindThisBuildCannotRender_StaysOwed_AndIsNamed); the SWEEP's arm, which is what both
          hosted runners execute, did not.

          The property that matters is `owed++`: a row this build cannot render must come back, and a
          row marked delivered by mistake is a notice nobody will ever receive.
        */
        var owner = await OwnerAsync();
        var notice = await OwedAsync(owner, @event: "PinAbdicated");
        await using var provider = Provider();
        var relay = Relay(provider);

        var summary = await relay.SweepAsync(CancellationToken.None);

        summary.Should().Be(new NoticeSweepSummary(Claimed: 1, Delivered: 0, Owed: 1));
        (await StoredAsync(notice.Id)).DeliveredAt.Should().BeNull(
            "a kind with no renderer arm stays owed rather than being marked delivered");
        _transport.Envelopes.Should().BeEmpty("nothing may be handed to a transport unrendered");
        _logs.Lines.Should().Contain(
            l => l.Level == LogLevel.Warning
                 && l.Message.Contains("cannot render")
                 && l.Message.Contains("PinAbdicated"),
            "the operator needs the EVENT to know which kind shipped half-built");
    }

    [Fact]
    public async Task ARowANOTHERRunnerMarkedFirst_IsNotOwed_AndTheDuplicateIsNamed()
    {
        /*
          THE MarkedByAnother ARM, AND THE ASYMMETRY NOTHING PINNED. It is the only arm of the six
          that deliberately does NOT do `owed++`: the arm above it counts a delivery and the three
          below it all count the row back as owed, so an edit that added `owed++` here out of
          mechanical symmetry would look right and be wrong. The row IS marked — by somebody — and
          owing it again would have the next sweep chase a notice that is already accounted for.

          At-least-once, named rather than hidden (ADR-0048): this runner's artefact is a second
          copy, and the operator is told so instead of the duplicate passing as a delivery.

          Another runner here is a SECOND context over the same store. Through the fixture's own
          context it would be the same tracked entity and the concurrency token could never conflict.
        */
        var owner = await OwnerAsync();
        var notice = await OwedAsync(owner);

        await using var elsewhere = new AzureBankDbContext(
            new DbContextOptionsBuilder<AzureBankDbContext>().UseInMemoryDatabase(_database).Options,
            timeProvider: null,
            auditChain: new AuditChain(
                Options.Create(new AuditOptions { ChainKey = ChainKey, AnchorKey = AnchorKey }),
                NullLogger<AuditChain>.Instance));

        var transport = new MarksTheRowMidDelivery(async () =>
        {
            var theirs = await elsewhere.SubscriberNotices.SingleAsync(n => n.Id == notice.Id);
            theirs.DeliveredAt = DateTime.UtcNow;
            theirs.DeliveryReceipt = "the-other-runner.eml";
            theirs.LeasedUntil = null;
            theirs.LeasedBy = null;
            await elsewhere.SaveChangesAsync();
        });

        await using var provider = Provider(transport);
        var relay = Relay(provider);

        var summary = await relay.SweepAsync(CancellationToken.None);

        summary.Should().Be(
            new NoticeSweepSummary(Claimed: 1, Delivered: 0, Owed: 0),
            "the row is marked, by somebody, so it is neither this runner's delivery NOR still owed "
            + "— the one arm of the six that counts nothing, and the asymmetry this test exists for");
        (await StoredAsync(notice.Id)).DeliveryReceipt.Should().Be(
            "the-other-runner.eml", "the first mark stands; this runner's write lost the token");
        _logs.Lines.Should().Contain(
            l => l.Level == LogLevel.Warning
                 && l.Message.Contains("marked by another runner")
                 && l.Message.Contains("duplicate"),
            "the artefact this runner produced is a second copy and the line says so");
    }

    /// <summary>A transport that lets another runner mark the row before this one writes.</summary>
    private sealed class MarksTheRowMidDelivery(Func<Task> markFirst) : INoticeTransport
    {
        public async Task<string> DeliverAsync(
            RenderedNotice notice, string toAddress, string directory, CancellationToken cancellationToken)
        {
            await markFirst();
            return "this-runner.eml";
        }
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
    public async Task TheBatchCapsWhatARunnerHolds_FailedRowsIncluded()
    {
        /*
          A runner whose deliveries keep failing holds its rows until the lease lapses. If every
          sweep then claimed a full batch on top, the holdings would grow past what one lease can
          deliver. The batch caps LIVE work: held rows are retried first, and only the remaining
          capacity is claimed afresh.
        */
        var owner = await OwnerAsync();
        await OwedAsync(owner);
        await OwedAsync(owner);
        await OwedAsync(owner);
        await using var provider = Provider();
        var relay = Relay(provider, batchSize: 2);

        _transport.Fails = new IOException("refused");
        (await relay.SweepAsync(CancellationToken.None)).Should().Be(new NoticeSweepSummary(Claimed: 2, Delivered: 0, Owed: 2));

        _clock.Advance(TimeSpan.FromSeconds(7));
        (await relay.SweepAsync(CancellationToken.None)).Should().Be(
            new NoticeSweepSummary(Claimed: 0, Delivered: 0, Owed: 2), "two held rows fill a batch of two; nothing more is claimed");
        (await _context.SubscriberNotices.AsNoTracking().CountAsync(n => n.LeasedBy == relay.RunnerName))
            .Should().Be(2, "the third row stays free for another runner");

        _transport.Fails = null;
        _clock.Advance(TimeSpan.FromSeconds(7));
        (await relay.SweepAsync(CancellationToken.None)).Should().Be(new NoticeSweepSummary(Claimed: 0, Delivered: 2, Owed: 0));
        (await relay.SweepAsync(CancellationToken.None)).Should().Be(new NoticeSweepSummary(Claimed: 1, Delivered: 1, Owed: 0));
    }

    [Fact]
    public async Task AHeldRowIsRenewedToTheNewSweepsLease_BeforeItIsRetried()
    {
        /*
          A row held from an earlier sweep carries that sweep's lease end. The per-row check
          compares against THIS sweep's end, so without a renewal the row could lapse in the middle
          of its retry and another runner claim it while this one was still writing it.
        */
        var owner = await OwnerAsync();
        var notice = await OwedAsync(owner);
        await using var provider = Provider();
        var relay = Relay(provider);

        _transport.Fails = new IOException("refused");
        await relay.SweepAsync(CancellationToken.None);
        var firstEnd = (await StoredAsync(notice.Id)).LeasedUntil!.Value;
        firstEnd.Should().Be(Now.AddSeconds(120));

        _clock.Advance(TimeSpan.FromSeconds(100));
        await relay.SweepAsync(CancellationToken.None);

        var stored = await StoredAsync(notice.Id);
        stored.LeasedBy.Should().Be(relay.RunnerName, "still held: the transport refused again");
        stored.LeasedUntil.Should().Be(Now.AddSeconds(120), "renewed to the second sweep's lease end, not left at the first's");
        stored.LeasedUntil.Should().BeAfter(firstEnd);
    }

    [Fact]
    public async Task TheVerbAttemptsEachRowOncePerRun_AcrossBatches()
    {
        // Batch of one, two rows, the first refused: the second batch's re-read by name would also
        // return the failed first row — still held, still owed — and must not attempt it again.
        var owner = await OwnerAsync();
        await OwedAsync(owner);
        await OwedAsync(owner);
        await using var provider = Provider(new FailingOnceTransport(_transport));
        var previous = NotifyCommand.VerbBatch;
        NotifyCommand.VerbBatch = 1;
        try
        {
            var (exitCode, lines) = await NotifyCommand.RunAsync(provider, _directory, Contact, CancellationToken.None);

            exitCode.Should().Be(AnchorCommand.NotRecorded);
            string.Join("\n", lines).Should().Contain("NOTIFIED 1 of 2");
            _transport.Envelopes.Should().ContainSingle("the refused row was not attempted a second time in the same run");
        }
        finally
        {
            NotifyCommand.VerbBatch = previous;
        }
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
    public void TheThreeRunnerKinds_AreTheLITERALSTHATLANDINLeasedBy()
    {
        /*
          The constants exist so three projects stop writing three string literals (ADR-0051), and
          this test exists because a constant makes its VALUE invisible. `LeasedBy` is read by a
          person during an incident and matched by `HeldByOthersAsync`; renaming `FunctionKind`
          from "func" to something else would change what a running deployment writes while every
          test that compares two constants stayed green.

          They must also be DISTINCT: two kinds sharing a prefix would make every log line and
          every held-by-another count ambiguous, and nothing would fail.
        */
        /*
          THE SCOPE IS LOAD-BEARING, AND IT HAS TO OPEN HERE RATHER THAN LOWER DOWN.
          FluentAssertions throws on the FIRST failure, so without it the three literals below
          short-circuit every mutant that CHANGES a kind, and `HaveCount(3)` short-circuits every
          mutant that ADDS one — which between them is every mutant there is, leaving the
          uniqueness and slash rules unreachable with a bad array. They read as independent guards
          and were not any. Placing the scope after the literals, which is where it went first, does
          not fix it: MEASURED, `VerbKind = "api"` still reported only the literal.
        */
        using var scope = new AssertionScope();

        NoticeClaim.ApiKind.Should().Be("api");
        NoticeClaim.VerbKind.Should().Be("verb");
        NoticeClaim.FunctionKind.Should().Be("func");

        /*
          READ OFF THE TYPE, NOT RESTATED. Building this array from the same three constants the
          three assertions above have just pinned made both checks below THEOREMS: once
          ApiKind == "api" and VerbKind == "verb" and FunctionKind == "func" have passed, the array
          is provably {"api","verb","func"} and neither uniqueness nor the slash rule can fail.
          They read as an independent guard and were not one — the same shape as the joined-string
          assertions this PR removed from NoticeFunctionStartupTests.

          Reflected over the type instead, they bite on the case they are FOR: a fourth kind added
          later, colliding with one of these or carrying a slash.
        */
        var kinds = typeof(NoticeClaim)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.Name.EndsWith("Kind", StringComparison.Ordinal))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToArray();

        kinds.Should().HaveCount(3,
            "a fourth kind is a fourth runner, and every count in ADR-0051 D7 would need re-taking");
        kinds.Should().OnlyHaveUniqueItems();
        kinds.Should().OnlyContain(
            k => k.Length > 0 && !k.Contains('/'),
            "the kind is the head of a slash-separated name, so it cannot carry a slash of its own");
    }

    [Theory]
    [InlineData("api/HOST/1/abcdef12", "`api`")]
    [InlineData("func/HOST/1/abcdef12", "`func`")]
    [InlineData("verb/HOST/1/abcdef12", "`verb`")]
    public async Task TheVerbLeavesARowHeldUnderAnothersLiveLease_AndNAMESTheKind(string heldBy, string named)
    {
        /*
          NAMED, NOT GUESSED. This line said "The API's relay is delivering them" until ADR-0051,
          which was true while the API was the only runner and a guess the moment the Function
          shipped: HeldByOthersAsync counts rows and does not say who holds them. The kind prefix in
          LeasedBy is what D7 exists for, and the operator reading this under pressure needs to know
          WHICH process to go and look at.

          Three rows rather than one, because a message that named the holder correctly for `api`
          and wrongly for `func` is exactly the defect being fixed.
        */
        var owner = await OwnerAsync();
        var notice = await OwedAsync(owner, leasedUntil: DateTime.UtcNow.AddMinutes(2), leasedBy: heldBy);
        await using var provider = Provider();

        var (exitCode, lines) = await NotifyCommand.RunAsync(provider, _directory, Contact, CancellationToken.None);
        var printed = string.Join("\n", lines);

        exitCode.Should().Be(VerifyCommand.NothingToVerify, "nothing was FREE for this run; not a success and not a failure");
        printed.Should().Contain("held under another runner's live lease");
        printed.Should().Contain($"recognises: {named}",
            "the holder is in the column; printing a guess instead is what ADR-0051 D7's prefix exists "
            + "to prevent. RECOGNISES, not \"taken by\": the count covers every holder and this clause "
            + "covers only the readable ones, so it must not read as an inventory of who holds the rows");
        printed.Should().NotContain("The API's relay",
            "the sentence that named one runner for all of them must not come back");
        printed.Should().NotContain("is delivering them",
            "a live LEASE is not a live PROCESS: LeasedUntil > now cannot tell a holder mid-delivery "
            + "from one that claimed and died, and the verb must not assert the difference");
        printed.Should().Contain("lease lapses",
            "the operator still needs to know that waiting is the remedy");
        _transport.Envelopes.Should().BeEmpty("the verb must not render what another runner holds");
        (await StoredAsync(notice.Id)).DeliveredAt.Should().BeNull();
    }

    [Fact]
    public async Task ARowHeldUNTILATIMEBYNOBODY_IsExcluded_RatherThanBlamedOnTheStore()
    {
        /*
          A STATE THE STORE FORBIDS AND THIS QUERY MUST NOT ASSUME AWAY.
          CK_SubscriberNotices_Lease pairs the halves — "(both NULL) OR (both NOT NULL)" — and
          SubscriberNoticeSqlServerTests watches SQL Server refuse a half-set lease. So this row
          cannot exist there. It can exist HERE, because the InMemory provider enforces no check
          constraint, and that is the point: `null != runner` is TRUE, so the holder query would
          admit the row and then dereference a null name.

          What made it worth a term in the predicate rather than a shrug is where the failure would
          land. NotifyCommand catches everything and prints "the store could not be read or written
          ({Type})" — so a null-reference bug would arrive at the operator as an accusation against
          the database, during exactly the incident where they are deciding whether the store is
          sound. The count is unaffected either way, which is why nothing else caught this.
        */
        var owner = await OwnerAsync();
        await OwedAsync(owner, leasedUntil: DateTime.UtcNow.AddMinutes(2), leasedBy: null);
        await using var provider = Provider();

        var (exitCode, lines) = await NotifyCommand.RunAsync(provider, _directory, Contact, CancellationToken.None);
        var printed = string.Join("\n", lines);

        printed.Should().NotContain("could not be read or written",
            "a row the store would have refused must not be reported as the store failing");
        printed.Should().NotContain("NullReferenceException");
        printed.Should().NotContain("held under another runner's live lease",
            "no runner holds it — counting it as held made the verb name a holder that does not exist, "
            + "which is why HeldByOthersAsync carries the same null term as the holder query. ⚠️ This "
            + "string must track the message: a NotContain left pointing at wording the verb no longer "
            + "prints passes vacuously and stops guarding, which is the one assertion in this file "
            + "that a message change does not turn red");
        printed.Should().Contain("NOTHING TO NOTIFY: no notice is owed.");
        exitCode.Should().NotBe(VerifyCommand.Misconfigured);

        /*
          ⚠️ THE LIMIT OF THAT LAST LINE, said rather than left for a reader to find: the row IS owed
          (DeliveredAt is null) and the verb reports that nothing is. Both of the verb's answers are
          wrong for this row, because its model has two states — free, or held by a runner — and a
          row leased until a time by nobody is in neither. The state is impossible on the real store,
          so the choice is between two wrong sentences about something that cannot happen; this one
          at least does not invent a runner. Making the verb describe it properly would mean a third
          state in the protocol for a row the database refuses to store.
        */
    }

    [Fact]
    public async Task AHolderWhoseNameIsNotOneOfTheThreeKinds_IsNotEchoedBackAtTheOperator()
    {
        /*
          LeasedBy is written by this protocol, so a name outside the three kinds means somebody
          edited the row. An operator message is the wrong place to echo an unvalidated string, and
          the count is unaffected — so the sentence has to work with nothing to name, and this is the
          row that proves it does.
        */
        var owner = await OwnerAsync();
        await OwedAsync(owner, leasedUntil: DateTime.UtcNow.AddMinutes(2), leasedBy: "'; DROP TABLE --/x/1/abcdef12");
        await using var provider = Provider();

        var (_, lines) = await NotifyCommand.RunAsync(provider, _directory, Contact, CancellationToken.None);
        var printed = string.Join("\n", lines);

        printed.Should().Contain("under no name this build recognises",
            "the fallback wording carries the same guidance and says plainly that nothing was readable");
        printed.Should().NotContain("recognises:",
            "there was no usable name to print. ⚠️ This string must track the message: pointed at "
            + "wording the verb no longer prints, a NotContain passes vacuously and guards nothing");
        printed.Should().NotContain("DROP TABLE");
        printed.Should().Contain("held under another runner's live lease", "the COUNT is still right; only the name was unusable");
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
        text.Should().Contain("NOTIFIED 1 of 1").And.Contain("1 more owed notice(s) are held under another runner's live lease");
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

    /// <summary>Refuses the first delivery only, then hands the rest to the inner transport.</summary>
    private sealed class FailingOnceTransport(INoticeTransport inner) : INoticeTransport
    {
        private int _calls;

        public Task<string> DeliverAsync(RenderedNotice notice, string toAddress, string directory, CancellationToken cancellationToken) =>
            Interlocked.Increment(ref _calls) == 1
                ? throw new IOException("refused once")
                : inner.DeliverAsync(notice, toAddress, directory, cancellationToken);
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
