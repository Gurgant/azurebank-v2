using System.Text;
using System.Text.Json;
using AzureBank.AuditVerifier.Commands;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Shared.Options;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AzureBank.Tests.Unit.Tools;

/// <summary>
/// The export verb: the copy that leaves the machine, and the guards around producing it.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ THESE ARE THE FIRST TESTS IN THIS SUITE THAT TOUCH THE FILESYSTEM. A grep for
/// <c>File.</c>, <c>StreamWriter</c> or <c>Directory.</c> across both test projects returned nothing
/// before this file, so the convention below is being set rather than followed: every test writes
/// under its own <see cref="Path.GetTempPath"/> directory named for a fresh GUID, and deletes it in
/// <see cref="Dispose"/> whatever the outcome. Nothing is written inside the repository — the
/// root <c>.gitignore</c> would hide an escape under <c>artifacts/</c>, but not anywhere else.
/// </para>
/// <para>
/// The command under test is what makes an anchor mean anything: the record in the database is a
/// local cache, and a copy that never left the machine can be deleted in the same breath as the rows
/// it counts.
/// </para>
/// </remarks>
public class ExportCommandTests : IDisposable
{
    private const string ChainKey = "export-command-tests-chain-key-0123456789ab";
    private const string AnchorKey = "export-command-tests-anchor-key-9876543210xy";

    private readonly ServiceProvider _services;
    private readonly AzureBankDbContext _context;
    private readonly string _directory;

    public ExportCommandTests()
    {
        var options = Options.Create(new AuditOptions { ChainKey = ChainKey, AnchorKey = AnchorKey });
        var chain = new AuditChain(options, NullLogger<AuditChain>.Instance);

        _context = new AzureBankDbContext(
            new DbContextOptionsBuilder<AzureBankDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options,
            timeProvider: null,
            auditChain: chain);

        var collection = new ServiceCollection();
        collection.AddSingleton<IOptions<AuditOptions>>(options);
        collection.AddSingleton<IAuditChain>(chain);
        collection.AddSingleton<IAuditAnchorChain>(new AuditAnchorChain(options));
        collection.AddSingleton(_context);
        _services = collection.BuildServiceProvider();

        _directory = Path.Combine(Path.GetTempPath(), "azurebank-export-" + Guid.NewGuid().ToString("N"));
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
        catch (IOException)
        {
            // A leaked temp directory is not worth failing a green run over.
        }

        GC.SuppressFinalize(this);
    }

    private string Path_(string name) => Path.Combine(_directory, name);

    private async Task WriteRowsAsync(int count)
    {
        for (var i = 0; i < count; i++)
        {
            _context.AuditEvents.Add(new AuditEvent
            {
                Id = Guid.CreateVersion7(),
                OccurredAt = DateTime.UtcNow,
                Event = $"ExportEvent{i}",
                Outcome = AuditOutcome.Succeeded,
                ActorUserId = Guid.NewGuid(),
                RowHash = string.Empty,
            });
            await _context.SaveChangesAsync();
        }
    }

    private async Task AnchorAsync() =>
        await AnchorCommand.RunAsync(_services, CancellationToken.None);

    [Fact]
    public async Task AnExportWritesOneRecordPerLine_AndEveryLineParsesOnItsOwn()
    {
        /*
          THE FORMAT IS THE COMPARISON, so the shape is the assertion. One JSON object per line means
          a later export differs from an earlier one by whole lines: a new anchor is one added line,
          a rewritten history is a removed one, and `diff` is the comparison with no code behind it.

          FALSIFIED by setting WriteIndented = true: each record spans fifteen lines, the count
          assertion fails, and per-line parsing fails on the first fragment.
        */
        await WriteRowsAsync(3);
        await AnchorAsync();
        await WriteRowsAsync(2);
        await AnchorAsync();

        var path = Path_("anchors.jsonl");
        var (exitCode, lines) = await ExportCommand.RunAsync(_services, path, CancellationToken.None);

        exitCode.Should().Be(VerifyCommand.Intact);
        string.Join(" ", lines).Should().Contain("EXPORTED 2 anchor records");

        var text = await File.ReadAllTextAsync(path);
        var records = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        records.Should().HaveCount(2, "one line per anchor record, in counter order");

        foreach (var record in records)
        {
            var parsed = JsonDocument.Parse(record).RootElement;
            parsed.GetProperty("anchorSequence").GetInt64().Should().BePositive();
            parsed.GetProperty("mac").GetString().Should().NotBeNullOrWhiteSpace();
        }

        JsonDocument.Parse(records[0]).RootElement.GetProperty("anchorSequence").GetInt64()
            .Should().Be(1, "the file is ordered by the counter, so a reader can follow the chain");
    }

    [Fact]
    public async Task AnExistingFileIsREFUSED_BecauseOverwritingIsTheMoveTheCopyExistsToDetect()
    {
        /*
          THE LOAD-BEARING GUARD IN THIS COMMAND. Truncate the table, re-export over yesterday's file,
          and every trace that anything was ever different is gone -- destroyed by the tool that
          produced the evidence, in one command, with no warning. An earlier copy is the only thing a
          later one can be compared against, so the earlier one is never at risk from this verb.

          It also has to be checked BEFORE anything is read, or a refusal would report something
          about the chain when it is reporting about a path.

          FALSIFIED by removing the File.Exists guard: the export succeeds, the sentinel content is
          gone, and both assertions below fail.
        */
        await WriteRowsAsync(2);
        await AnchorAsync();

        var path = Path_("already-here.jsonl");
        const string sentinel = "{\"anchorSequence\":1,\"this\":\"is yesterday's copy\"}\n";
        await File.WriteAllTextAsync(path, sentinel);

        var (exitCode, lines) = await ExportCommand.RunAsync(_services, path, CancellationToken.None);

        exitCode.Should().Be(
            AnchorCommand.NotRecorded,
            "the chain was fine and nothing came of the run, which is neither success nor no-verdict");
        string.Join(" ", lines).Should().Contain("NOT EXPORTED");
        (await File.ReadAllTextAsync(path)).Should().Be(
            sentinel, "the earlier copy is the reference; this verb must never be able to destroy it");
    }

    [Fact]
    public async Task AnEmptyAnchorTableWritesNOFILE_BecauseAnEmptyFileWouldBeAClaim()
    {
        /*
          An empty file and no file are different statements. An empty file says a run happened and
          found nothing; no file says nothing happened. This command cannot tell those apart, so it
          declines to say either -- and leaving a zero-byte file behind would quietly become the
          reference a later comparison is measured against.

          FALSIFIED by writing the file before the count check: File.Exists comes back true.
        */
        var path = Path_("empty.jsonl");
        var (exitCode, lines) = await ExportCommand.RunAsync(_services, path, CancellationToken.None);

        exitCode.Should().Be(VerifyCommand.NothingToVerify);
        string.Join(" ", lines).Should().Contain("NOTHING TO EXPORT");
        File.Exists(path).Should().BeFalse("no file at all, rather than a file asserting emptiness");
    }

    [Fact]
    public async Task AGapMarkersCoverageStaysNULL_AndIsNeverSerialisedAsZero()
    {
        /*
          A gap marker asserts coverage of NOTHING, and its coverage columns are null by
          construction. Serialised as zeros, the file would show a record covering "sequence 0" --
          a claim no run ever made, and one that reads as an anchor rather than as a marker. Kind
          travels with it for the same reason: it is what separates the two shapes.

          FALSIFIED by giving the coverage fields a `long` type rather than `long?` in
          ExportedAnchor: they serialise as 0 and the null assertions below fail.
        */
        await AnchorAsync();   // an empty table produces a gap marker

        var path = Path_("marker.jsonl");
        var (exitCode, _) = await ExportCommand.RunAsync(_services, path, CancellationToken.None);
        exitCode.Should().Be(VerifyCommand.Intact);

        var line = (await File.ReadAllTextAsync(path)).Split('\n')[0];
        var parsed = JsonDocument.Parse(line).RootElement;

        parsed.GetProperty("kind").GetString().Should().Be(nameof(AuditAnchorKind.GapMarker));
        parsed.GetProperty("coveredThroughSequence").ValueKind.Should().Be(JsonValueKind.Null);
        parsed.GetProperty("coveredRowCount").ValueKind.Should().Be(JsonValueKind.Null);
        parsed.GetProperty("lowestCoveredSequence").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task TheBytesAreTheSameEverywhere_LFAndNoBOM()
    {
        /*
          THE COPY HAS TO BE BYTE-IDENTICAL WHEREVER IT WAS PRODUCED, because comparing copies is its
          whole job. This clone has core.autocrlf=true and the repository has no root .gitattributes,
          so a file written with Environment.NewLine is CRLF here and LF in CI -- a difference on
          every line, in a file whose only signal is which lines differ. Encoding.UTF8 would add a
          three-byte BOM in front of the first record for good measure.

          FALSIFIED by swapping '\n' for Environment.NewLine on Windows (CR appears), or
          `new UTF8Encoding(false)` for Encoding.UTF8 (the BOM appears).
        */
        await WriteRowsAsync(2);
        await AnchorAsync();

        var path = Path_("bytes.jsonl");
        await ExportCommand.RunAsync(_services, path, CancellationToken.None);

        var bytes = await File.ReadAllBytesAsync(path);

        bytes.Take(3).Should().NotEqual(
            new byte[] { 0xEF, 0xBB, 0xBF }, "a JSON Lines file starts at its first '{'");
        bytes.Should().NotContain((byte)'\r', "CRLF would make every line differ across platforms");
        bytes[^1].Should().Be((byte)'\n', "every record is terminated, including the last");
        Encoding.UTF8.GetString(bytes).Should().StartWith("{");
    }

    [Fact]
    public async Task AMissingAnchorKeyIsRefusedBeforeAnythingIsRead()
    {
        /*
          The same precondition as `anchor`, and the reason is what the file becomes: the reference a
          later run is measured against. A copy this run cannot authenticate would make a future
          disagreement unreadable -- it would not say whether the table changed or the reference was
          never trustworthy.

          FALSIFIED by dropping the key check: the export succeeds and writes a file nothing vouches
          for.
        */
        await WriteRowsAsync(2);
        await AnchorAsync();

        var stranger = Options.Create(new AuditOptions { ChainKey = ChainKey, AnchorKey = "  " });
        var collection = new ServiceCollection();
        collection.AddSingleton<IOptions<AuditOptions>>(stranger);
        collection.AddSingleton<IAuditChain>(new AuditChain(stranger, NullLogger<AuditChain>.Instance));
        collection.AddSingleton<IAuditAnchorChain>(new AuditAnchorChain(stranger));
        collection.AddSingleton(_context);
        using var services = collection.BuildServiceProvider();

        var path = Path_("unkeyed.jsonl");
        var (exitCode, lines) = await ExportCommand.RunAsync(services, path, CancellationToken.None);

        exitCode.Should().Be(VerifyCommand.Misconfigured);
        string.Join(" ", lines).Should().Contain("NO VERDICT");
        File.Exists(path).Should().BeFalse("nothing is read, so nothing is written");
    }

    [Fact]
    public async Task TheAdviceSaysTheFileIsNotEvidenceUntilItLeavesTheMachine()
    {
        /*
          "Seen, not stored safely" is the property the deferred document scopes this to, and a file
          on the same disk has been seen by nobody -- whoever can truncate the table can delete it in
          the same breath. Printing a green "exported" line and stopping would be the whole defect,
          in the same way `anchor` refuses to print an affirmative sentence it cannot support.

          FALSIFIED by trimming the closing advice to the count: this reddens, and the operator is
          left believing the write finished the job.
        */
        await WriteRowsAsync(2);
        await AnchorAsync();

        var (_, lines) = await ExportCommand.RunAsync(
            _services, Path_("advice.jsonl"), CancellationToken.None);
        var text = string.Join(" ", lines);

        text.Should().Contain("NOT EVIDENCE UNTIL IT IS SOMEWHERE THIS MACHINE CANNOT REACH");
        text.Should().Contain("diff", "the comparison is the operator's to run, and it needs naming");
    }
}
