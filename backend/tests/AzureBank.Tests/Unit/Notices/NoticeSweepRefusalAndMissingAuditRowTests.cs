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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AzureBank.Tests.Unit.Notices;

/// <summary>
/// Two things the sweep itself says, which no test read: its own refusal of a configuration with
/// no pickup directory or no contact, and the finding it logs when a notice goes out with no audit
/// row behind it.
/// </summary>
/// <remarks>
/// <para>
/// THE REFUSAL is the second guard (the first is the host's validation of its options). A host
/// that skipped that validation would otherwise deliver a notice with no repudiation contact in
/// it, or resolve an empty directory against the working directory.
/// </para>
/// <para>
/// THE FINDING is read from the verifier's <c>notify</c> verb by its own tests, which print it on
/// a console. The relay has no console: its only word is this log line, and the line has to say
/// WHICH question found nothing (ADR-0052). The sweep is the real <see cref="NoticeSweep"/> on
/// InMemory, the idiom of <c>NoticeRelayServiceTests</c>, with its limits.
/// </para>
/// </remarks>
public sealed class NoticeSweepRefusalAndMissingAuditRowTests : IDisposable
{
    private const string Contact = "security@your-bank.example, +00 000 0000";
    private const string Runner = "api/HOST/1/0badc0de";

    private readonly AzureBankDbContext _context;
    private readonly RecordingTransport _transport = new();
    private readonly RecordingLoggerProvider _logs = new();
    private readonly ILoggerFactory _loggers;
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 12, 9, 0, 0, TimeSpan.Zero));
    private readonly string _directory;

    public NoticeSweepRefusalAndMissingAuditRowTests()
    {
        var chain = new AuditChain(
            Options.Create(new AuditOptions
            {
                ChainKey = "notice-sweep-tests-chain-key-0123456789abcdef",
                AnchorKey = "notice-sweep-tests-anchor-key-fedcba9876543210",
            }),
            NullLogger<AuditChain>.Instance);
        _context = new AzureBankDbContext(
            new DbContextOptionsBuilder<AzureBankDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options,
            timeProvider: null,
            auditChain: chain);
        _loggers = LoggerFactory.Create(builder => builder.AddProvider(_logs));
        _directory = Path.Combine(Path.GetTempPath(), "azurebank-sweep-unit-" + Guid.NewGuid().ToString("N"));
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

    private NoticeSweep Sweep(string? pickupDirectory, string? contact) =>
        new(
            _context,
            _transport,
            new NoticeRelayOptions
            {
                Runner = NoticeRunner.Api,
                PickupDirectory = pickupDirectory!,
                Contact = contact!,
            },
            _loggers.CreateLogger("sweep"),
            _clock);

    private async Task<Guid> OwnerAsync()
    {
        var id = Guid.CreateVersion7();
        _context.Users.Add(new ApplicationUser
        {
            Id = id,
            UserName = id.ToString(),
            Email = "owner@example.com",
            AzureTag = "owner_" + id.ToString("N")[..8],
            FirstName = "Owner",
            LastName = "Sentinel",
            PinHash = "pin-hash-sentinel",
            PasswordHash = "password-hash-sentinel",
        });
        await _context.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> AuditRowAsync(Guid userId, string kind)
    {
        var row = new AuditEvent
        {
            Id = Guid.CreateVersion7(),
            OccurredAt = DateTime.UtcNow,
            Event = kind,
            Outcome = AuditOutcome.Succeeded,
            ActorUserId = userId,
            RowHash = string.Empty,
        };
        _context.AuditEvents.Add(row);
        await _context.SaveChangesAsync();
        return row.Id;
    }

    private async Task<SubscriberNotice> OwedAsync(Guid userId, string kind, Guid? auditEventId)
    {
        var notice = new SubscriberNotice
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Event = kind,
            OccurredAt = new DateTime(2026, 10, 12, 8, 42, 9, DateTimeKind.Utc),
            AuditEventId = auditEventId,
        };
        _context.SubscriberNotices.Add(notice);
        await _context.SaveChangesAsync();
        return notice;
    }

    private IEnumerable<string> Findings() =>
        _logs.Lines
            .Where(line => line.Level == LogLevel.Warning && line.Message.Contains("NO AUDIT ROW backs notice"))
            .Select(line => line.Message);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WithNoPickupDirectory_TheSweepIsNotBuilt(string? pickupDirectory)
    {
        var build = () => Sweep(pickupDirectory, Contact);

        build.Should().Throw<InvalidOperationException>()
            .WithMessage("The relay has no pickup directory: Notices:PickupDirectory is not set.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WithNoContact_TheSweepIsNotBuilt(string? contact)
    {
        var build = () => Sweep(_directory, contact);

        build.Should().Throw<InvalidOperationException>()
            .WithMessage("The relay has no repudiation contact: Notices:Contact is not set.*");
    }

    [Fact]
    public async Task ANoticeWhoseNamedAuditRowIsNotThere_IsDelivered_AndTheSweepSaysTheRowItNamesIsGone()
    {
        // Another row of the same kind for the same user survives: the weaker question would be
        // answered by it, and the finding would go quiet (ADR-0052).
        var owner = await OwnerAsync();
        await AuditRowAsync(owner, SecurityEvents.PinChanged);
        var notice = await OwedAsync(owner, SecurityEvents.PinChanged, auditEventId: Guid.CreateVersion7());

        var summary = await Sweep(_directory, Contact).RunAsync(Runner, CancellationToken.None);

        using var all = new AssertionScope();
        summary.Should().Be(new NoticeSweepSummary(Claimed: 1, Delivered: 1, Owed: 0), "the absence is reported, and the holder is still told");
        _transport.Envelopes.Should().ContainSingle();
        Findings().Should().ContainSingle()
            .Which.Should().Contain(notice.Id.ToString("N"))
            .And.Contain(SecurityEvents.PinChanged)
            .And.Contain("the row it names is gone, or is not this notice's")
            .And.NotContain("names no row");
    }

    [Fact]
    public async Task ANoticeThatNamesNoRow_WithNoRowOfItsKind_IsDelivered_AndTheSweepSaysItAskedTheWeakerQuestion()
    {
        var owner = await OwnerAsync();
        var notice = await OwedAsync(owner, SecurityEvents.PinEnrolled, auditEventId: null);

        var summary = await Sweep(_directory, Contact).RunAsync(Runner, CancellationToken.None);

        using var all = new AssertionScope();
        summary.Should().Be(new NoticeSweepSummary(Claimed: 1, Delivered: 1, Owed: 0));
        _transport.Envelopes.Should().ContainSingle();
        Findings().Should().ContainSingle()
            .Which.Should().Contain(notice.Id.ToString("N"))
            .And.Contain("this notice names no row")
            .And.Contain("the weaker question (ADR-0052)")
            .And.NotContain("the row it names is gone");
    }

    [Fact]
    public async Task ANoticeWithItsAuditRow_IsDelivered_AndNothingIsReported()
    {
        // CONTROL: the finding is the absence's, not every delivery's.
        var owner = await OwnerAsync();
        var row = await AuditRowAsync(owner, SecurityEvents.PinEnrolled);
        await OwedAsync(owner, SecurityEvents.PinEnrolled, auditEventId: row);

        var summary = await Sweep(_directory, Contact).RunAsync(Runner, CancellationToken.None);

        using var all = new AssertionScope();
        summary.Should().Be(new NoticeSweepSummary(Claimed: 1, Delivered: 1, Owed: 0));
        Findings().Should().BeEmpty();
    }
}
