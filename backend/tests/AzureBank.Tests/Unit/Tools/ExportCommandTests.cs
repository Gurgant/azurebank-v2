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
    private readonly string _store = Guid.NewGuid().ToString();

    public ExportCommandTests()
    {
        var options = Options.Create(new AuditOptions { ChainKey = ChainKey, AnchorKey = AnchorKey });
        var chain = new AuditChain(options, NullLogger<AuditChain>.Instance);

        _context = new AzureBankDbContext(
            new DbContextOptionsBuilder<AzureBankDbContext>()
                .UseInMemoryDatabase(_store)
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
        catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
        {
            /*
              BOTH TYPES, because the comment below used to promise something the catch did not
              deliver. MEASURED: UnauthorizedAccessException.IsSubclassOf(typeof(IOException)) is
              FALSE -- it derives from SystemException -- so a read-only or still-held file inside
              the directory would have escaped Dispose and failed a run that had already gone green,
              from the cleanup path, for a reason with nothing to do with the tests.

              (DirectoryNotFoundException IS an IOException, measured in the same run, which is why
              ExportCommand needs no separate arm for it.)

              A leaked temp directory is not worth failing a green run over.
            */
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

    /// <summary>
    /// A provider over the SAME store holding a DIFFERENT anchor key, so the stored records no
    /// longer authenticate and <c>VerifyChainAsync</c> reports the chain broken. It is how an
    /// operator actually reaches that state — a key rotated, a wrong environment, a second machine —
    /// and it is the technique <c>AnchorCommandTests</c> already uses for the same purpose.
    /// </summary>
    private ServiceProvider Stranger()
    {
        var stranger = Options.Create(new AuditOptions
        {
            ChainKey = ChainKey,
            AnchorKey = "a-completely-different-anchor-key-0123456789",
        });
        var chain = new AuditChain(stranger, NullLogger<AuditChain>.Instance);
        var collection = new ServiceCollection();
        collection.AddSingleton<IOptions<AuditOptions>>(stranger);
        collection.AddSingleton<IAuditChain>(chain);
        collection.AddSingleton<IAuditAnchorChain>(new AuditAnchorChain(stranger));
        collection.AddSingleton(new AzureBankDbContext(
            new DbContextOptionsBuilder<AzureBankDbContext>().UseInMemoryDatabase(_store).Options,
            timeProvider: null,
            auditChain: chain));
        return collection.BuildServiceProvider();
    }

    [Fact]
    public async Task ABrokenChainStillExportsACopy_AndTheVerdictIsTheCHAINVerdict()
    {
        /*
          COPYING ASSERTS NOTHING ABOUT WHAT IT COPIED, so a chain that has stopped verifying still
          gets its copy -- that is the moment an off-machine copy is worth the most, and refusing
          here would withhold the artefact exactly when it matters. But the exit code is the CHAIN's,
          because that is the more serious of the two things this run learned.

          FALSIFIED by returning VerifyCommand.Intact unconditionally on the success path.
        */
        await WriteRowsAsync(3);
        await AnchorAsync();

        using var stranger = Stranger();
        var path = Path_("broken-but-copied.jsonl");
        var (exitCode, lines) = await ExportCommand.RunAsync(stranger, path, CancellationToken.None);

        exitCode.Should().Be(VerifyCommand.Broken, "the verdict about the chain is still the verdict");
        File.Exists(path).Should().BeTrue("a chain that stopped verifying is when a copy is worth most");
        string.Join(" ", lines).Should().Contain("THE ANCHOR CHAIN DID NOT VERIFY");
    }

    [Fact]
    public async Task AWriteFailureNEVERBuriesACompletedBrokenVerdict()
    {
        /*
          ⚠️ THE ONE THIS VERB HAD WRONG. The walk finishes and reports the chain broken; the write
          then fails, and every failure branch returned NotRecorded (6) while printing "the chain was
          read and nothing is wrong with it that this can see". Both halves false: something IS wrong
          with it, and the more serious half was the one being swallowed. An operator whose chain has
          stopped verifying, who exports to a path that already exists, was told the chain is fine.

          The rule is not invented here. AnchoringABrokenChain_RecordsAGapMarkerAndSaysTheChainIsBroken
          gives it in one line -- "the verdict about the chain is still the verdict" -- which is why
          `anchor` exits 1 over a broken chain even when it did record a marker. A write problem is
          fixed by naming another path in a second; a chain that does not verify is an incident.

          ⚠️ A MISSING PARENT DIRECTORY, NOT AN OCCUPIED PATH, and the first draft of this test got
          that wrong in a way worth keeping. An occupied path is caught by the PRE-CHECK, which runs
          BEFORE the walk -- so there is no verdict yet, 6 is the honest answer there, and the test
          failed at "expected 1, found 6" while the code was right. The branches this is about are
          the ones AFTER the walk, and a missing parent reaches one deterministically on every
          platform: it passes File.Exists, the chain is read, and only then does FileStream raise
          DirectoryNotFoundException. The collision-at-create branch shares the same routing through
          NotWritten but needs a genuine race, which no seam here can produce.

          FALSIFIED by returning AnchorCommand.NotRecorded directly from the write-failure branch
          instead of routing it through NotWritten: this reddens with 6.
        */
        await WriteRowsAsync(3);
        await AnchorAsync();

        var path = Path.Combine(_directory, "no-such-dir", "copy.jsonl");

        using var stranger = Stranger();
        var (exitCode, lines) = await ExportCommand.RunAsync(stranger, path, CancellationToken.None);
        var text = string.Join(" ", lines);

        exitCode.Should().Be(
            VerifyCommand.Broken,
            "a failed write must not be able to downgrade a verdict the walk had already reached");
        exitCode.Should().NotBe(AnchorCommand.NotRecorded);

        text.Should().Contain("CHAIN BROKEN", "the more serious half is stated first");
        text.Should().Contain("NOT EXPORTED", "and the writing failure is kept, not dropped");
        text.Should().NotContain(
            "nothing is wrong with it", "that sentence is what made this a false report");
        File.Exists(path).Should().BeFalse("nothing was written, which is the other half");
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


    /// <summary>
    /// A second provider over the SAME in-memory store, because EF does not support concurrent use
    /// of one DbContext and the concurrency under test is on the FILE rather than on the database.
    /// </summary>
    private ServiceProvider Sibling()
    {
        var options = Options.Create(new AuditOptions { ChainKey = ChainKey, AnchorKey = AnchorKey });
        var chain = new AuditChain(options, NullLogger<AuditChain>.Instance);
        var collection = new ServiceCollection();
        collection.AddSingleton<IOptions<AuditOptions>>(options);
        collection.AddSingleton<IAuditChain>(chain);
        collection.AddSingleton<IAuditAnchorChain>(new AuditAnchorChain(options));
        collection.AddSingleton(new AzureBankDbContext(
            new DbContextOptionsBuilder<AzureBankDbContext>().UseInMemoryDatabase(_store).Options,
            timeProvider: null,
            auditChain: chain));
        return collection.BuildServiceProvider();
    }

    [Fact]
    public async Task ConcurrentExportsToOnePath_ProduceExactlyONEFile_AndNoneIsTruncated()
    {
        /*
          THE PRE-CHECK HAS A WINDOW AND THE WRITE IS WHAT CLOSES IT. File.Exists returning false
          says nothing about the moment of the write, and File.WriteAllTextAsync opens with
          FileMode.Create, which TRUNCATES -- measured: a 29-byte file written over with 2 bytes is
          2 bytes afterwards. So two runs aimed at one path could both pass the check and the second
          could destroy the first, which is the exact thing this verb must never be able to do.
          FileMode.CreateNew makes that the operating system's decision instead.

          ⚠️ WHAT THIS TEST PROVES AND WHAT IT DOES NOT, with the numbers rather than a claim. The
          INVARIANT -- exactly one writer, and the surviving file is a whole export rather than a
          fragment -- is asserted deterministically. Whether the eight runs actually interleave is
          NOT controlled, so the regression is caught probabilistically.

          FALSIFIED, and measured twice because the first run is what corrected this paragraph.
          With FileMode.Create restored and the pre-check removed, run in isolation it reddens at
          "expected 1, found 3" -- and in the same mutation across the WHOLE suite it passed, because
          nothing forced those eight to overlap. So a green here is weaker evidence than a red one,
          and an earlier draft of this comment claiming "every run then reports success" was wrong on
          both counts: not every run, and not every execution.
        */
        await WriteRowsAsync(4);
        await AnchorAsync();
        await WriteRowsAsync(3);
        await AnchorAsync();

        var path = Path_("contended.jsonl");
        var siblings = Enumerable.Range(0, 8).Select(_ => Sibling()).ToArray();

        try
        {
            var results = await Task.WhenAll(siblings.Select(s =>
                ExportCommand.RunAsync(s, path, CancellationToken.None)));

            results.Count(r => r.ExitCode == VerifyCommand.Intact).Should().Be(
                1, "exactly one run may create the path; the rest must refuse rather than overwrite");
            results.Where(r => r.ExitCode != VerifyCommand.Intact)
                .Should().OnlyContain(r => r.ExitCode == AnchorCommand.NotRecorded,
                    "a refusal is 'there was a verdict and no file came of it', never a chain verdict");

            var records = (await File.ReadAllTextAsync(path))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);
            records.Should().HaveCount(2, "the surviving file is a whole export, not a fragment");
            foreach (var record in records)
            {
                JsonDocument.Parse(record).RootElement.GetProperty("payloadHash").GetString()
                    .Should().NotBeNullOrWhiteSpace();
            }
        }
        finally
        {
            foreach (var sibling in siblings)
            {
                sibling.Dispose();
            }
        }
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

    [Fact]
    public async Task AnEmptyPathIsAUSAGEERROR_NotAStoreFailure()
    {
        /*
          `export ""` is reachable: the parser accepts an empty string, so the handler gets one, and
          File.Exists("") returns FALSE rather than throwing -- so the pre-check waves it through,
          the chain is read, and only FileStream objects, with an ArgumentException that used to land
          in the catch-all. The operator was then told "the audit store could not be read": a
          database outage, over a typo, at the end of a walk that had already succeeded.

          UsageError rather than NotRecorded because nothing has been read when this fires, so there
          is no verdict for 6 to be about -- and because `export` with NO argument already exits 4
          through the parser, so the same mistake keeps the same number.

          FALSIFIED by deleting the IsNullOrWhiteSpace guard: this reddens with 3, which is the exact
          bug.
        */
        await WriteRowsAsync(2);
        await AnchorAsync();

        foreach (var path in new[] { string.Empty, "   " })
        {
            var (exitCode, lines) = await ExportCommand.RunAsync(
                _services, path, CancellationToken.None);

            exitCode.Should().Be(
                VerifyCommand.UsageError,
                "an empty path is a wrong command line, not an unreadable database");
            exitCode.Should().NotBe(VerifyCommand.Misconfigured);
            string.Join(" ", lines).Should().Contain("not a usable path");
        }
    }

    [Fact]
    public async Task NoBadPathIsEVERReportedAsAStoreFailure()
    {
        /*
          THE CLASS, NOT THE ONE INPUT THE REVIEW NAMED. Whatever is wrong with the PATH, the
          operator is never sent to check the DATABASE. Exit 3 is the one answer none of these may
          give, because 3 is the code the runbook tells you to wire an alert on.

          ⚠️ THIS LIST WAS WRONG TWICE, BOTH TIMES BY MEASURING ON WINDOWS AND WRITING THE RESULT
          AS A UNIVERSAL PROPERTY, and both times CI on ubuntu-latest was what said so.

          First it carried "a<b>c.jsonl" as an invalid character, measured on Windows throwing
          IOException. Angle brackets are an ordinary filename on Linux: the export SUCCEEDED and the
          assertion read "expected one of {4, 6}, but found 0". Path.GetInvalidFileNameChars()
          returns 41 entries on Windows and 2 on Linux, so "an invalid character" is not a portable
          idea at all.

          Then the guard moved to Path.GetFullPath, measured on Windows to reject empty, whitespace
          AND an embedded NUL. Same failure one layer down: `export "   "` succeeded on Linux and
          wrote a file named three spaces. Confirmed outside .NET too -- `touch "   "` on Ubuntu
          creates it, `ls -b` shows it. That run also exposed a second defect in this file: a
          whitespace path is RELATIVE, so it resolved against the shared process working directory
          rather than the per-test one, and the file this test created made the sibling test see
          "already exists" instead of a usage error.

          So the guard is pure string logic now -- empty, blank, or containing a NUL -- which cannot
          vary by platform because it never asks the platform anything. Everything else is left to
          the filesystem to judge and lands in the write-failure branches at exit 6.

          Every shape below is portable by construction: the first three are rejected before any
          filesystem call, a missing parent raises DirectoryNotFoundException (an IOException)
          everywhere, and a directory raises that or UnauthorizedAccessException -- both handled. No
          relative path reaches the filesystem any more, which is what makes the tests independent.

          FALSIFIED two ways: removing any single write-failure catch drops that shape to the
          catch-all and reddens here with 3, and dropping either half of the guard lets its shape
          through -- IsNullOrWhiteSpace alone misses the NUL, Contains('\0') alone misses the blank.
        */
        await WriteRowsAsync(2);
        await AnchorAsync();

        var shapes = new (string Name, string Path)[]
        {
            ("empty", string.Empty),
            ("whitespace", "   "),
            ("an embedded NUL", Path.Combine(_directory, "a\0b.jsonl")),
            ("a directory", _directory),
            ("a missing parent", Path.Combine(_directory, "no-such-dir", "f.jsonl")),
        };

        foreach (var (name, path) in shapes)
        {
            var (exitCode, _) = await ExportCommand.RunAsync(_services, path, CancellationToken.None);

            exitCode.Should().NotBe(
                VerifyCommand.Misconfigured,
                $"{name} is wrong with the path, and 3 sends the operator to a database that is fine");
            exitCode.Should().BeOneOf(
                [VerifyCommand.UsageError, AnchorCommand.NotRecorded],
                $"{name} is either a wrong command line or a verdict nothing came of");
        }
    }
}
