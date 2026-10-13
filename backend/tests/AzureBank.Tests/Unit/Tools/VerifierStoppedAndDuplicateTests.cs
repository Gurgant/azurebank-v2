using AzureBank.AuditVerifier.Commands;
using AzureBank.Infrastructure.Data;
using AzureBank.Infrastructure.Notices;
using AzureBank.Shared.Constants;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Shared.Options;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AzureBank.Tests.Unit.Tools;

/// <summary>
/// Two answers of the verifier that no test held: <c>evidence</c> when the operator stops it, and
/// <c>notify</c> when another run marks a notice between this run's read and its own mark.
/// </summary>
public sealed class VerifierStoppedAndDuplicateTests : IDisposable
{
    private const string ChainKey = "stopped-and-duplicate-tests-chain-key-0123456";
    private const string AnchorKey = "stopped-and-duplicate-tests-anchor-key-98765";
    private const string Contact = "security@your-bank.example, +00 000 0000";

    private readonly string _store = Guid.NewGuid().ToString();
    private readonly IOptions<AuditOptions> _options =
        Options.Create(new AuditOptions { ChainKey = ChainKey, AnchorKey = AnchorKey });

    private readonly AzureBankDbContext _context;
    private readonly string _directory;

    public VerifierStoppedAndDuplicateTests()
    {
        _context = Context();
        _directory = Path.Combine(Path.GetTempPath(), "azurebank-duplicate-" + Guid.NewGuid().ToString("N"));
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
            // A leaked temp directory is not worth failing a green run over (ExportCommandTests).
        }
    }

    private AzureBankDbContext Context() =>
        new(
            new DbContextOptionsBuilder<AzureBankDbContext>().UseInMemoryDatabase(_store).Options,
            timeProvider: null,
            auditChain: new AuditChain(_options, NullLogger<AuditChain>.Instance));

    [Fact]
    public async Task Evidence_StoppedByTheOperator_IsInterrupted_WhateverTheFailureLooksLike()
    {
        // Decided by the token and not by the type of the exception, as `verify` measured: a read
        // cancelled in flight on SQL Server faults with a SqlException that names no cancellation.
        // Here the token is signalled and the store answers with something else: a context that is
        // already disposed throws ObjectDisposedException before it looks at any token.
        // Measured 2026-10-12: exit 5.
        var collection = new ServiceCollection();
        collection.AddSingleton(_options);
        collection.AddSingleton<IAuditChain>(new AuditChain(_options, NullLogger<AuditChain>.Instance));
        collection.AddSingleton<IAuditAnchorChain>(new AuditAnchorChain(_options));
        var disposed = Context();
        await disposed.DisposeAsync();
        collection.AddSingleton(disposed);
        await using var provider = collection.BuildServiceProvider();

        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();

        var (exitCode, lines) = await EvidenceCommand.RunAsync(provider, "TXN-20260101-0000000000Z", stop.Token);

        using var all = new AssertionScope();
        exitCode.Should().Be(VerifyCommand.Interrupted);
        exitCode.Should().NotBe(
            VerifyCommand.Misconfigured, "the operator stopped it: 3 sends them to a store that is fine");
        lines[0].Should().Be("INTERRUPTED: the evidence was not assembled.");
        string.Join("\n", lines).Should().Contain("says nothing about the store, the key or the transaction");
    }

    [Fact]
    public async Task Notify_WhenAnotherRunMarksTheNoticeFirst_NamesTheDuplicate_AndOwesNothing()
    {
        // At least once, and named: the file this run wrote is a second copy, and the operator is
        // told which. The row is marked, by the other run, so it is not owed and the run ends 0;
        // the first mark stands. The relay holds the same arm in NoticeRelayServiceTests; the verb
        // prints its own sentence and had no test of it.
        // Another run is a SECOND context over the same store: through one context it would be the
        // same tracked entity and the concurrency token could never conflict.
        // Measured 2026-10-12: exit 0, "NOTIFIED 0 of 1", the receipt of the other run kept.
        var owner = Guid.CreateVersion7();
        _context.Users.Add(new ApplicationUser
        {
            Id = owner,
            UserName = owner.ToString(),
            Email = "owner@example.com",
            AzureTag = "owner_" + owner.ToString("N")[..8],
            FirstName = "Owner",
            LastName = "Sentinel",
            PinHash = "pin-hash-sentinel",
            PasswordHash = "password-hash-sentinel",
        });
        var row = new AuditEvent
        {
            Id = Guid.CreateVersion7(),
            OccurredAt = DateTime.UtcNow,
            Event = SecurityEvents.PinEnrolled,
            Outcome = AuditOutcome.Succeeded,
            ActorUserId = owner,
            RowHash = string.Empty,
        };
        _context.AuditEvents.Add(row);
        var notice = new SubscriberNotice
        {
            Id = Guid.CreateVersion7(),
            UserId = owner,
            Event = SecurityEvents.PinEnrolled,
            OccurredAt = new DateTime(2026, 9, 3, 10, 15, 0, DateTimeKind.Utc),
            AuditEventId = row.Id,
        };
        _context.SubscriberNotices.Add(notice);
        await _context.SaveChangesAsync();

        await using var elsewhere = Context();
        var transport = new MarksTheRowMidDelivery(async () =>
        {
            var theirs = await elsewhere.SubscriberNotices.SingleAsync(n => n.Id == notice.Id);
            theirs.DeliveredAt = DateTime.UtcNow;
            theirs.DeliveryReceipt = "the-other-run.eml";
            theirs.LeasedUntil = null;
            theirs.LeasedBy = null;
            await elsewhere.SaveChangesAsync();
        });

        var collection = new ServiceCollection();
        collection.AddSingleton(_options);
        collection.AddSingleton(_context);
        collection.AddSingleton<INoticeTransport>(transport);
        await using var provider = collection.BuildServiceProvider();

        var (exitCode, lines) = await NotifyCommand.RunAsync(provider, _directory, Contact, CancellationToken.None);
        var text = string.Join("\n", lines);

        using var all = new AssertionScope();
        transport.Calls.Should().Be(1, "ARRANGE: this run did hand the notice to the transport");
        exitCode.Should().Be(VerifyCommand.Intact, "the notice is marked, by somebody: nothing is owed");
        lines[0].Should().StartWith("NOTIFIED 0 of 1 waiting notices", "the delivery is the other run's, not this one's");
        text.Should().Contain(
            $"NOT NOTIFIED by this run: notice {notice.Id:N} was marked by another run while this one wrote "
            + "this-run.eml; that file is a duplicate.");
        text.Should().NotContain("still owed");

        await using var reader = Context();
        var stored = await reader.SubscriberNotices.AsNoTracking().SingleAsync(n => n.Id == notice.Id);
        stored.DeliveryReceipt.Should().Be("the-other-run.eml", "the first mark stands; a marked notice is never rewritten");
    }

    /// <summary>A transport that lets another run mark the row before this one does.</summary>
    private sealed class MarksTheRowMidDelivery(Func<Task> markFirst) : INoticeTransport
    {
        public int Calls { get; private set; }

        public async Task<string> DeliverAsync(
            RenderedNotice notice, string toAddress, string directory, CancellationToken cancellationToken)
        {
            Calls++;
            await markFirst();
            return "this-run.eml";
        }
    }
}
