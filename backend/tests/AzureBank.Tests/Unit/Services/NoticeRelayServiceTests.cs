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
using Xunit;

namespace AzureBank.Tests.Unit.Services;

/// <summary>
/// The relay's one sweep, on InMemory (ADR-0048): what it claims, delivers, marks and logs, and
/// what it leaves owed. What InMemory cannot prove — that two runners cannot hold one row — is
/// <c>NoticeRelaySqlServerTests</c>.
/// </summary>
public sealed class NoticeRelayServiceTests : IDisposable
{
    private const string ChainKey = "notice-relay-tests-chain-key-0123456789abcd";
    private const string AnchorKey = "notice-relay-tests-anchor-key-9876543210wxyz";
    private const string Contact = "security@your-bank.example, +00 000 0000";
    private const string Address = "owner@example.com";

    private readonly AzureBankDbContext _context;
    private readonly RecordingTransport _transport = new();
    private readonly RecordingLoggerProvider _logs = new();
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

    private ServiceProvider Provider()
    {
        var collection = new ServiceCollection();
        collection.AddSingleton<IOptions<AuditOptions>>(
            Options.Create(new AuditOptions { ChainKey = ChainKey, AnchorKey = AnchorKey }));
        collection.AddSingleton(_context);
        collection.AddSingleton<INoticeTransport>(_transport);
        collection.AddLogging(b => b.AddProvider(_logs));
        return collection.BuildServiceProvider();
    }

    private NoticeRelayService Relay(ServiceProvider provider, NoticeRunner runner = NoticeRunner.Api) =>
        new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new NoticeRelayOptions
            {
                Runner = runner,
                PickupDirectory = _directory,
                Contact = Contact,
                PeriodSeconds = 5,
                LeaseSeconds = 120,
            }),
            provider.GetRequiredService<ILogger<NoticeRelayService>>());

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

    [Fact]
    public async Task OneSweep_ClaimsDeliversAndMarks_AndClearsTheLease()
    {
        var owner = await OwnerAsync();
        var notice = await OwedAsync(owner);
        await using var provider = Provider();
        var relay = Relay(provider);

        var summary = await relay.SweepAsync(_directory, CancellationToken.None);

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
    public async Task TheAddressNeverReachesALogLine()
    {
        var owner = await OwnerAsync();
        await OwedAsync(owner);
        var failing = await OwnerAsync(email: "bad\r\nBcc: attacker@example.com");
        await OwedAsync(failing);
        await using var provider = Provider();

        await Relay(provider).SweepAsync(_directory, CancellationToken.None);

        _logs.Lines.Should().NotBeEmpty();
        _logs.Lines.Should().OnlyContain(
            l => !l.Message.Contains(Address) && !l.Message.Contains("attacker@"),
            "the address is read for the To: header and printed nowhere — not in a receipt, not in a "
            + "failure, not in a warning about an unusable one");
    }

    [Fact]
    public async Task WhenTheRunnerIsNotThisProcess_TheLoopReturns_AndTouchesNothing()
    {
        var owner = await OwnerAsync();
        var notice = await OwedAsync(owner);
        await using var provider = Provider();
        var relay = Relay(provider, runner: NoticeRunner.None);

        await relay.StartAsync(CancellationToken.None);
        var finished = await Task.WhenAny(relay.ExecuteTask!, Task.Delay(TimeSpan.FromSeconds(5)));

        finished.Should().BeSameAs(relay.ExecuteTask, "with Runner=None the loop must return at once, not wait a period");
        _transport.Envelopes.Should().BeEmpty();
        var stored = await StoredAsync(notice.Id);
        stored.DeliveredAt.Should().BeNull();
        stored.LeasedBy.Should().BeNull("nothing was claimed");
        _logs.Lines.Should().Contain(l => l.Message.Contains("delivers nothing"));
        await relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ATransportFailure_LeavesTheRowOwedAndLeased_AndItIsRetriedAfterTheLease()
    {
        /*
          AT-LEAST-ONCE, PINNED. The lease is not cleared on failure: the row stays this runner's
          until the lease lapses, so the next runner — or this one — takes it again. A row that had
          been handed to the transport and whose mark then failed would go out twice; the ADR says
          so, and this is the mechanism it says it about.
        */
        var owner = await OwnerAsync();
        var notice = await OwedAsync(owner);
        await using var provider = Provider();
        var relay = Relay(provider);

        _transport.Fails = new IOException("disk full — and this message must not surface");
        var first = await relay.SweepAsync(_directory, CancellationToken.None);

        first.Should().Be(new NoticeSweepSummary(Claimed: 1, Delivered: 0, Owed: 1));
        var afterFailure = await StoredAsync(notice.Id);
        afterFailure.DeliveredAt.Should().BeNull("nothing was marked");
        afterFailure.LeasedBy.Should().Be(relay.RunnerName, "the lease stands until it lapses");
        _logs.Lines.Should().Contain(l => l.Level == LogLevel.Warning && l.Message.Contains("IOException") && !l.Message.Contains("disk full"));

        // The same runner, before the lease lapses: nothing free.
        _transport.Fails = null;
        (await relay.SweepAsync(_directory, CancellationToken.None)).Claimed.Should().Be(0, "its own live lease is not free either");

        // The lease lapses.
        var tracked = await _context.SubscriberNotices.SingleAsync(n => n.Id == notice.Id);
        tracked.LeasedUntil = DateTime.UtcNow.AddSeconds(-1);
        await _context.SaveChangesAsync();

        var second = await relay.SweepAsync(_directory, CancellationToken.None);
        second.Should().Be(new NoticeSweepSummary(Claimed: 1, Delivered: 1, Owed: 0));
        (await StoredAsync(notice.Id)).DeliveredAt.Should().NotBeNull();
    }

    [Fact]
    public async Task TheVerbLeavesARowALiveRunnerHolds_AndSaysSo()
    {
        var owner = await OwnerAsync();
        var notice = await OwedAsync(owner, leasedUntil: DateTime.UtcNow.AddMinutes(2), leasedBy: "HOST/1/abcdef12");
        await using var provider = Provider();

        var (exitCode, lines) = await NotifyCommand.RunAsync(provider, _directory, Contact, CancellationToken.None);

        exitCode.Should().Be(VerifyCommand.NothingToVerify, "nothing was FREE for this run; not a success and not a failure");
        string.Join("\n", lines).Should().Contain("leased by a live runner");
        _transport.Envelopes.Should().BeEmpty("the verb must not render what the relay is delivering");
        (await StoredAsync(notice.Id)).DeliveredAt.Should().BeNull();
    }

    [Fact]
    public async Task TheVerbTakesARowWhoseLeaseHasLapsed()
    {
        var owner = await OwnerAsync();
        var notice = await OwedAsync(owner, leasedUntil: DateTime.UtcNow.AddSeconds(-1), leasedBy: "HOST/1/dead0000");
        await using var provider = Provider();

        var (exitCode, lines) = await NotifyCommand.RunAsync(provider, _directory, Contact, CancellationToken.None);

        exitCode.Should().Be(VerifyCommand.Intact);
        string.Join("\n", lines).Should().Contain("NOTIFIED 1 of 1");
        var stored = await StoredAsync(notice.Id);
        stored.DeliveredAt.Should().NotBeNull();
        stored.LeasedBy.Should().BeNull("the mark clears a lapsed lease too");
    }
}
