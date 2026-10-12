using AzureBank.AuditVerifier.Commands;
using AzureBank.Infrastructure.Data;
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
/// A record put in the place of another one, by somebody who holds the anchor key: what the
/// anchor chain says of it, and what <c>anchor</c> and <c>export</c> then do.
/// </summary>
/// <remarks>
/// <para>
/// The authentication code stops whoever holds the database and not the key. The link is what is
/// left against whoever holds both: a record written again keeps its number and gets a genuine
/// code, and the record above it still names the payload hash of the one that was there. So the
/// two links do not meet, and that is the only thing in the table that says so.
/// </para>
/// <para>
/// BUILT WITH THE REAL <see cref="AuditAnchorChain.Build"/>, not by editing a stored row: the
/// context refuses every update of an anchor, and an edited row would fail its own code first and
/// never reach the check of the link.
/// </para>
/// </remarks>
public sealed class AnchorCommandRefusalTests : IDisposable
{
    private const string ChainKey = "anchor-refusal-tests-chain-key-0123456789abc";
    private const string AnchorKey = "anchor-refusal-tests-anchor-key-9876543210xyz";

    private readonly IOptions<AuditOptions> _options =
        Options.Create(new AuditOptions { ChainKey = ChainKey, AnchorKey = AnchorKey });

    private readonly AuditChain _chain;
    private readonly AuditAnchorChain _anchors;
    private readonly AzureBankDbContext _context;
    private readonly ServiceProvider _services;
    private readonly string _directory;

    public AnchorCommandRefusalTests()
    {
        _chain = new AuditChain(_options, NullLogger<AuditChain>.Instance);
        _anchors = new AuditAnchorChain(_options);
        _context = new AzureBankDbContext(
            new DbContextOptionsBuilder<AzureBankDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            timeProvider: null,
            auditChain: _chain);

        var collection = new ServiceCollection();
        collection.AddSingleton(_options);
        collection.AddSingleton<IAuditChain>(_chain);
        collection.AddSingleton<IAuditAnchorChain>(_anchors);
        collection.AddSingleton(_context);
        _services = collection.BuildServiceProvider();

        _directory = Path.Combine(Path.GetTempPath(), "azurebank-anchor-refusal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        _services.Dispose();
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

    /// <summary>
    /// Three audit rows, anchor 1 over them, and an anchor 2 that follows ANOTHER first record:
    /// one built over the same walk at another instant and never stored. Both stored records carry
    /// a genuine code. It is the table as it stands after anchor 1 was written again.
    /// </summary>
    private async Task ARecordWasPutInThePlaceOfTheFirstAsync()
    {
        for (var i = 0; i < 3; i++)
        {
            _context.AuditEvents.Add(new AuditEvent
            {
                Id = Guid.CreateVersion7(),
                OccurredAt = DateTime.UtcNow,
                Event = $"AnchorRefusalEvent{i}",
                Outcome = AuditOutcome.Succeeded,
                ActorUserId = Guid.NewGuid(),
                RowHash = string.Empty,
            });
            await _context.SaveChangesAsync();
        }

        var walk = await _chain.VerifyAsync(_context);
        var stored = _anchors.Build(walk, tail: null, new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc));
        var theOneThatWasThere = _anchors.Build(walk, tail: null, new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc));
        var second = _anchors.Build(walk, theOneThatWasThere, new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc));

        _context.Set<AuditAnchor>().AddRange(stored, second);
        await _context.SaveChangesAsync();

        _anchors.Check(stored).Should().Be(AuditAnchorCheck.Authentic, "ARRANGE: the first record carries a genuine code");
        _anchors.Check(second).Should().Be(AuditAnchorCheck.Authentic, "ARRANGE: and so does the second");
        second.PreviousAnchorPayloadHash.Should().NotBe(stored.PayloadHash, "ARRANGE: the links do not meet");
    }

    [Fact]
    public async Task TwoGenuineRecordsWhoseLinksDoNotMeet_AreABrokenLink_AtTheSecond()
    {
        // Measured 2026-10-12: one record verified, the break at anchor 2, kind LinkBroken.
        await ARecordWasPutInThePlaceOfTheFirstAsync();

        var state = await _anchors.VerifyChainAsync(_context);

        using var all = new AssertionScope();
        state.IsIntact.Should().BeFalse("each record authenticates, and the chain still does not hold");
        state.Kind.Should().Be(AuditAnchorChainBreakKind.LinkBroken);
        state.FirstBrokenSequence.Should().Be(2);
        state.Verified.Should().Be(1, "the first record is what it says; the second does not follow it");
        state.Reason.Should().Contain("The links do not meet");
    }

    [Fact]
    public async Task Anchor_OverABrokenLink_RecordsNothing_AndSaysToPreserveTheTable()
    {
        // Appending would make the new record's link say that everything beneath it was there.
        // Measured 2026-10-12: exit 6, two records before and two after.
        await ARecordWasPutInThePlaceOfTheFirstAsync();

        var (exitCode, lines) = await AnchorCommand.RunAsync(_services, CancellationToken.None);
        var text = string.Join("\n", lines);

        using var all = new AssertionScope();
        exitCode.Should().Be(AnchorCommand.NotRecorded);
        lines[0].Should().Be("NOT RECORDED: the existing anchor chain did not verify, so nothing was appended.");
        text.Should().Contain("Records verified before the break: 1");
        text.Should().Contain("Broke at anchor: 2");
        text.Should().Contain("The links do not meet");
        text.Should().Contain("Preserve the table and escalate. Nothing was written");
        text.Should().NotContain(
            "re-running with", "that advice is for a key this run does not hold, and here the key is not in question");
        (await _context.Set<AuditAnchor>().AsNoTracking().CountAsync()).Should().Be(2, "nothing was appended");
    }

    [Fact]
    public async Task Export_OverABrokenLink_StillWritesTheCopy_AndExitsBroken()
    {
        // Copying asserts nothing about what it copied, and the exit code is the chain's.
        // Measured 2026-10-12: exit 1, two lines in the file, "LinkBroken at anchor 2" printed.
        await ARecordWasPutInThePlaceOfTheFirstAsync();
        var path = Path.Combine(_directory, "broken-link.jsonl");

        var (exitCode, lines) = await ExportCommand.RunAsync(_services, path, CancellationToken.None);

        using var all = new AssertionScope();
        exitCode.Should().Be(VerifyCommand.Broken);
        string.Join("\n", lines).Should().Contain("LinkBroken at anchor 2");
        (await File.ReadAllLinesAsync(path)).Should().HaveCount(2, "both records are in the copy");
    }
}
