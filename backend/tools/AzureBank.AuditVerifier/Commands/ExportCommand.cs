using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AzureBank.AuditVerifier.Commands;

/// <summary>
/// Writes the anchor chain to a file outside the database, one record per line.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>THE EXPORTED COPY IS THE ANCHOR; THIS TABLE IS A LOCAL CACHE OF IT.</b> That is the way
/// round it has to be read, and the other way round is the one that reads as "done". An anchor that
/// has never left the machine constrains nobody: whoever can truncate <c>AuditEvents</c> can delete
/// the records covering past the cut, and both chains then verify perfectly, because each links
/// backwards only. The copy is what a later run has to disagree with.
/// </para>
/// <para>
/// ⚠️ <b>AND WRITING A FILE IS NOT THE SAME AS BEING SEEN.</b>
/// <c>docs/deferred/anchoring-the-audit-trail.md</c> states the property this buys and states it
/// narrowly: <i>"For every anchor a third party has SEEN, the operator loses the ability to tell a
/// different story about the rows that anchor covers."</i> Seen, not stored safely. A file this
/// machine wrote to this machine's disk has been seen by nobody, so on its own it moves nothing —
/// the move happens when the copy reaches somewhere its author cannot quietly revise. This command
/// produces the artefact; the operator supplies the elsewhere, and the printed advice says so
/// rather than implying the write finished the job.
/// </para>
/// <para>
/// It is a DEMONSTRATION rather than a control, in the deferred document's own words: a control that
/// depends on somebody choosing to run it does not constrain that person, and nothing here runs
/// unattended. What it demonstrates is real and runnable on a laptop — export, truncate the tail,
/// export again to a second path, and <c>diff</c> the two files.
/// </para>
/// <para>
/// JSON LINES, AND THE FORMAT IS THE COMPARISON. One record per line, appended in counter order and
/// never rewritten, so a copy under version control shows a new anchor as a pure addition and shows
/// any revision of history as a deletion beside it. A single JSON array would rewrite the previous
/// last line on every append — a comma — and drown the one signal the file exists to carry. It also
/// means the comparison needs no code and no verb of its own: <c>diff</c> is the comparison, and
/// <c>git diff</c> is the comparison for a published copy.
/// </para>
/// </remarks>
public static class ExportCommand
{
    /// <summary>
    /// Every column of <see cref="AuditAnchor"/>, verbatim, in a fixed order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>THIS SERIALISES STORED VALUES AND DERIVES NOTHING.</b> There are already two canonical
    /// renderings of an anchor — the MACed payload and the unkeyed anchored value, both rendered by
    /// <see cref="AuditAnchorChain"/> — and a third one that recomputed anything here would be a
    /// third place for writing and verification to disagree about bytes. So each field is copied out
    /// as it is stored, and a reader who wants to check the record recomputes from these values
    /// using the chain's renderer rather than trusting this file's arithmetic.
    /// </para>
    /// <para>
    /// ⚠️ <b>NULLS STAY NULL.</b> A gap marker's coverage fields are null BY CONSTRUCTION — it
    /// asserts coverage of nothing — so serialising them as zeros would produce a file in which a
    /// marker reads as an anchor covering sequence 0, which is a claim no run ever made. <c>Kind</c>
    /// is exported for the same reason: it is what separates the two shapes, and it is inside the
    /// authenticated payload precisely because flipping it costs one UPDATE and no key.
    /// </para>
    /// </remarks>
    private sealed record ExportedAnchor
    {
        [JsonPropertyOrder(0)] public required long AnchorSequence { get; init; }
        [JsonPropertyOrder(1)] public required string PayloadVersion { get; init; }
        [JsonPropertyOrder(2)] public required string Kind { get; init; }
        [JsonPropertyOrder(3)] public required string AnchorKeyId { get; init; }
        [JsonPropertyOrder(4)] public required string VerifiedUnderChainKeyId { get; init; }
        [JsonPropertyOrder(5)] public required long? LowestCoveredSequence { get; init; }
        [JsonPropertyOrder(6)] public required long? CoveredThroughSequence { get; init; }
        [JsonPropertyOrder(7)] public required long? CoveredRowCount { get; init; }
        [JsonPropertyOrder(8)] public required string? TailRowHash { get; init; }
        [JsonPropertyOrder(9)] public required string? AnchoredValue { get; init; }
        [JsonPropertyOrder(10)] public required string? PreviousAnchorPayloadHash { get; init; }
        [JsonPropertyOrder(11)] public required string PayloadHash { get; init; }
        [JsonPropertyOrder(12)] public required string Mac { get; init; }
        [JsonPropertyOrder(13)] public required string CreatedAt { get; init; }
    }

    /*
      CAMEL CASE AND NO INDENTATION, because the unit of this file is the LINE. Indented JSON would
      spread one record across fifteen lines and make an append look like fifteen changes, which is
      the property the format was chosen for. WriteIndented stays off for that reason and not for
      size.
    */
    private static readonly JsonSerializerOptions Format = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static Command Create(IServiceProvider services)
    {
        var command = new Command(
            "export",
            "Write the anchor chain to a file outside the database, one record per line.");

        /*
          THE FIRST ARGUMENT ANY VERB IN THIS TOOL TAKES. Both existing verbs are bare, so there is no
          local precedent for what a missing one does -- measured instead: System.CommandLine reports
          a missing required argument through the parser, and CombineExitCodes maps every non-zero
          parser result to UsageError. So "you forgot the path" is already 4 and needs nothing here.
        */
        var pathArgument = new Argument<string>(
            "path",
            "Where to write the copy. Refused if the file already exists.");
        command.AddArgument(pathArgument);

        command.SetHandler(async (InvocationContext invocation) =>
        {
            // The real token, for the reason both siblings record: the framework's
            // CancelOnProcessTermination middleware only engages for a handler that asks for it.
            var path = invocation.ParseResult.GetValueForArgument(pathArgument);
            var (exitCode, lines) = await RunAsync(services, path, invocation.GetCancellationToken());

            // Recorded before printed. Writing to a closed stdout throws, an escaping exception
            // becomes exit 1, and 1 in this tool means CHAIN BROKEN.
            Environment.ExitCode = exitCode;

            try
            {
                foreach (var line in lines)
                {
                    Console.WriteLine(line);
                }
            }
            catch (IOException)
            {
                // A broken pipe costs the operator the text, never the answer.
            }
        });

        return command;
    }

    internal static async Task<(int ExitCode, string[] Lines)> RunAsync(
        IServiceProvider services,
        string path,
        CancellationToken cancellationToken)
    {
        /*
          THE PATH IS CHECKED BEFORE ANYTHING ELSE, INCLUDING THE KEY, because it is the thing the
          operator just typed and the cheapest mistake to name. `export ""` is the reachable shape:
          the parser accepts an empty string, so the handler receives one.

          ⚠️ MEASURED, ACROSS THE WHOLE CLASS RATHER THAN THE ONE INPUT. Five bad-path shapes produce
          FOUR different exceptions from FileStream, and File.Exists returns false for every one of
          them: empty and whitespace throw ArgumentException, a directory throws
          UnauthorizedAccessException, a missing parent throws DirectoryNotFoundException, an invalid
          character throws IOException. The last three already land in the write-failure branches
          below and report exit 6. ArgumentException did not -- it fell through to the catch-all and
          reported "the audit store could not be read", which is a database outage, over a typo, at
          the end of a walk that had already succeeded. That is the same defect `anchor` shipped once
          and the same one the cancellation guard fixed: a wrong sentence is worse than a loud
          failure, because an escape is loud and a sentence is not.

          UsageError rather than NotRecorded, and the tool's own definitions decide it. 6 means
          "there WAS a verdict but nothing could be recorded from it" -- and nothing has been read
          here yet, so there is no verdict to have. 4 means the command line was wrong, which is what
          this is. It also matches what the parser already does: `export` with no argument at all
          exits 4, measured, so the same mistake gets the same number whether the path is missing or
          empty.

          ⚠️ Path.GetFullPath RATHER THAN IsNullOrWhiteSpace ALONE, and the difference is a shape the
          first version missed. An embedded NUL is invalid on every platform, and
          IsNullOrWhiteSpace returns FALSE for it -- so "a b.jsonl" walked past the guard, read the
          chain, and hit the same ArgumentException in FileStream that this whole check exists to
          stop. Measured: GetFullPath throws ArgumentException for empty, for whitespace AND for the
          NUL, and succeeds for everything a filesystem will actually consider. One call classifies
          the malformed-path class instead of enumerating members of it, which is what a check
          written from a list of examples always ends up doing.

          The try is around this ONE call, deliberately. A catch wide enough to cover the whole
          operation would swallow real defects to improve one message.
        */
        try
        {
            _ = Path.GetFullPath(path);
        }
        catch (Exception failure) when (failure is ArgumentException or NotSupportedException)
        {
            return (VerifyCommand.UsageError, new[]
            {
                "NOT EXPORTED: that is not a usable path.",
                "  `export` needs somewhere to write the copy, and this argument cannot name a file",
                $"  on this system -- {failure.GetType().Name}: {failure.Message}",
                "  Nothing was read and nothing was written. Name a file that does not exist yet:",
                "    export ./anchors-2026-08-27.jsonl",
            });
        }

        using var scope = services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<AuditOptions>>();

        /*
          THE SAME PRECONDITION AS `anchor`, DELIBERATELY, because these two verbs write and read the
          same table and a copy of unknown provenance is worse than no copy. The exported file is
          about to become the authority a later run is measured against; installing one this run
          cannot authenticate would make "the copy disagrees" unreadable -- it would not say whether
          the table changed or the copy was never trustworthy.

          Validated at the point of use, exactly where the other two validate, for the reason
          measured on a3e31a7: doing it during host construction meant an unconfigured machine could
          not even print --help.
        */
        if (string.IsNullOrWhiteSpace(options.Value.AnchorKey) || options.Value.AnchorKey.Length < 32)
        {
            return (VerifyCommand.Misconfigured, new[]
            {
                "NO VERDICT: Audit:AnchorKey is not configured, so nothing was exported.",
                "  The copy this writes becomes the reference a later run is compared against, and a",
                "  reference this run cannot authenticate would make a future disagreement",
                "  unreadable. Set it the way the other five secrets are set -- user-secrets in",
                "  development, environment or vault elsewhere.",
            });
        }

        var anchors = scope.ServiceProvider.GetRequiredService<IAuditAnchorChain>();
        var context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();

        try
        {
            /*
              REFUSED BEFORE ANYTHING IS READ. Overwriting an existing copy with the current state
              is precisely the move the copy exists to detect: truncate the table, re-export over
              yesterday's file, and the evidence that anything was ever different is gone --
              destroyed by the tool that produced it, in one command, with no warning. So an existing
              path is refused and the operator names a new one; keeping both files is what makes
              `diff` the comparison.

              It is checked FIRST so a refusal costs nothing and reports nothing about the chain.

              ⚠️ THIS CHECK IS THE MESSAGE, NOT THE GUARANTEE, and the first version of this comment
              called it "the one guard worth the most here" without noticing the window underneath
              it. Between File.Exists returning false and the write, another run can create the file
              -- and File.WriteAllTextAsync opens with FileMode.Create, which TRUNCATES. Measured: a
              29-byte file written over with 2 bytes is 2 bytes afterwards, not 29. So the guarantee
              is FileMode.CreateNew at the write below, which is the operating system's job and has
              no window; this check exists to say something useful before the database is read.
            */
            if (File.Exists(path))
            {
                return (AnchorCommand.NotRecorded, new[]
                {
                    $"NOT EXPORTED: {path} already exists, and this will not overwrite it.",
                    "  An earlier copy is the only thing a later one can be compared against.",
                    "  Overwriting it with the current state is the exact move an export exists to",
                    "  make visible, so it is refused rather than confirmed. Write to a new path and",
                    "  keep both: `diff` between two exports is the comparison.",
                });
            }

            var records = await context.Set<AuditAnchor>()
                .AsNoTracking()
                .OrderBy(a => a.AnchorSequence)
                .ToListAsync(cancellationToken);

            if (records.Count == 0)
            {
                return (VerifyCommand.NothingToVerify, new[]
                {
                    "NOTHING TO EXPORT: the anchor table is empty.",
                    "  No file was written. An empty file would be a claim that a run happened and",
                    "  found nothing, which is not the same as no run having happened -- and this",
                    "  cannot tell those apart, so it says neither. Run `anchor` first.",
                });
            }

            var state = await anchors.VerifyChainAsync(context, cancellationToken);

            var payload = new StringBuilder();
            foreach (var record in records)
            {
                payload.Append(JsonSerializer.Serialize(
                    new ExportedAnchor
                    {
                        AnchorSequence = record.AnchorSequence,
                        PayloadVersion = record.PayloadVersion,
                        Kind = record.Kind.ToString(),
                        AnchorKeyId = record.AnchorKeyId,
                        VerifiedUnderChainKeyId = record.VerifiedUnderChainKeyId,
                        LowestCoveredSequence = record.LowestCoveredSequence,
                        CoveredThroughSequence = record.CoveredThroughSequence,
                        CoveredRowCount = record.CoveredRowCount,
                        TailRowHash = record.TailRowHash,
                        AnchoredValue = record.AnchoredValue,
                        PreviousAnchorPayloadHash = record.PreviousAnchorPayloadHash,
                        PayloadHash = record.PayloadHash,
                        Mac = record.Mac,

                        /*
                          ROUND-TRIP "O", NOT THE DEFAULT, and the reason is the same one that put
                          Ticks rather than a formatted string inside the hashed payload: the default
                          format drops sub-second precision, so a re-import would carry a CreatedAt
                          that no longer reproduces the payload hash. "O" emits seven fractional
                          digits, which is a tick exactly.
                        */
                        CreatedAt = record.CreatedAt.ToString("O"),
                    },
                    Format));

                /*
                  "\n" EXPLICITLY, NEVER Environment.NewLine. This clone has core.autocrlf=true and
                  the repository has no root .gitattributes, so a file written with the platform's
                  separator is CRLF here and LF in CI -- which would make the committed sample differ
                  from a freshly produced one for a reason that has nothing to do with the audit
                  trail. The copy has to be byte-identical wherever it was produced, because
                  comparing copies is its whole job.
                */
                payload.Append('\n');
            }

            /*
              CREATED EXCLUSIVELY, WHICH IS WHERE THE REFUSAL ACTUALLY LIVES. FileMode.CreateNew
              fails if anything is already at the path, atomically, so two runs aimed at one path
              cannot both write it and the earlier copy cannot be truncated by the later one.
              FileShare.None keeps a reader from seeing the file half-written.

              NO BOM. Encoding.UTF8 emits one; new UTF8Encoding(false) does not. A leading BOM would
              put three bytes in front of the first record that no other producer of this format
              writes, and the first line of a JSON Lines file is expected to start with '{'.
            */
            var bytes = new UTF8Encoding(false).GetBytes(payload.ToString());
            await using (var file = new FileStream(
                path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await file.WriteAsync(bytes, cancellationToken);
            }

            var lines = new List<string>
            {
                $"EXPORTED {records.Count:N0} anchor records to {path}",
                $"  Covering through sequence "
                    + $"{records[^1].CoveredThroughSequence?.ToString("N0") ?? "(nothing -- the last record is a gap marker)"}.",
                string.Empty,
            };

            if (!state.IsIntact)
            {
                /*
                  THE COPY IS STILL WRITTEN WHEN THE CHAIN DOES NOT VERIFY, and that is deliberate
                  rather than an oversight. `anchor` refuses to APPEND to a chain it cannot vouch
                  for, because appending would make the new record's link assert that everything
                  beneath it was fine. Copying asserts nothing. A chain that has stopped verifying is
                  the moment an off-machine copy is worth the most, so refusing here would withhold
                  the artefact exactly when it matters -- but saying nothing about it would file a
                  broken state as the reference, so the verdict is printed and the exit code carries
                  it.
                */
                lines.Add("⚠️ THE ANCHOR CHAIN DID NOT VERIFY, and this file is a copy of that.");
                lines.Add($"  {state.Kind} at anchor "
                    + $"{state.FirstBrokenSequence?.ToString("N0") ?? "(unknown)"}: {state.Reason}");
                lines.Add("  The copy was written anyway: a chain that has stopped verifying is when an");
                lines.Add("  off-machine copy is worth the most, and copying asserts nothing about what");
                lines.Add("  it copied. Do not install this as the reference for a healthy chain.");
                lines.Add(string.Empty);
            }

            lines.Add("  THIS FILE IS NOT EVIDENCE UNTIL IT IS SOMEWHERE THIS MACHINE CANNOT REACH.");
            lines.Add("  The property an anchor buys is scoped to what somebody else has SEEN, not to");
            lines.Add("  what was written down: a copy on this disk was seen by nobody, and whoever can");
            lines.Add("  truncate the table can delete it in the same breath. Push it, mail it, print");
            lines.Add("  it -- anywhere its author cannot quietly revise it.");
            lines.Add(string.Empty);
            lines.Add("  To compare later, export to a DIFFERENT path and `diff` the two. One record");
            lines.Add("  per line means a new anchor is one added line and a rewritten history is a");
            lines.Add("  removed one. Nothing here reads a copy back: that comparison is yours to run.");

            return (state.IsIntact ? VerifyCommand.Intact : VerifyCommand.Broken, [.. lines]);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            /*
              INTERRUPTION IS DECIDED BY THE TOKEN, NOT BY THE EXCEPTION TYPE, and this guard comes
              FIRST for the reason the sibling commands record at length: cancel a walk genuinely in
              flight and Microsoft.Data.SqlClient sends an attention, the server aborts the batch, and
              the task completes FAULTED with a SqlException carrying no cancellation type at all.
              Keying on the type catches only what a pre-cancelled token produces, which is what a
              unit test manufactures.
            */
            return (VerifyCommand.Interrupted, new[]
            {
                "INTERRUPTED: the export was stopped.",
                $"  {path} may exist and be incomplete. Delete it and re-run, or export to a new",
                "  path -- a partial copy installed as a reference is worse than none, because a",
                "  later comparison would report a difference this machine created.",
            });
        }
        catch (IOException) when (File.Exists(path))
        {
            /*
              THE WINDOW THE PRE-CHECK CANNOT CLOSE, reported as what it is rather than as a disk
              failure. Something arrived at this path while the chain was being read -- another run,
              or somebody else -- and FileMode.CreateNew refused rather than truncating it. Measured
              shape: IOException, HResult 0x80070050, "The file ... already exists", and File.Exists
              is true from inside this filter, which is what makes the test portable rather than a
              check on an HResult that differs per platform.
            */
            return (AnchorCommand.NotRecorded, new[]
            {
                $"NOT EXPORTED: {path} appeared while this run was reading the chain.",
                "  Something else created it between the check and the write, and it was refused",
                "  rather than overwritten -- an earlier copy is the only thing a later one can be",
                "  compared against. Nothing was changed here. Export to a new path and keep both.",
            });
        }
        catch (IOException failure)
        {
            /*
              A FAILED WRITE IS "NOTHING CAME OF IT", NOT "NO VERDICT". The chain was read and it
              said something; what failed was the recording. That is exactly the distinction exit 6
              was added for in `anchor`, and reusing it here is why this verb needs no code of its
              own -- a full disk and a read-only directory are the same class of failure as a
              refused append.
            */
            return (AnchorCommand.NotRecorded, new[]
            {
                $"NOT EXPORTED: {path} could not be written.",
                $"  {failure.GetType().Name}: {failure.Message}",
                "  The chain was read and nothing is wrong with it that this can see. Nothing was",
                "  changed in the database. Check the path, the permissions and the free space.",
            });
        }
        catch (UnauthorizedAccessException failure)
        {
            // Not an IOException -- .NET raises this one for permissions and for a directory in the
            // way, and it is the same class of failure as the branch above.
            return (AnchorCommand.NotRecorded, new[]
            {
                $"NOT EXPORTED: {path} could not be written.",
                $"  {failure.GetType().Name}: {failure.Message}",
                "  The chain was read and nothing is wrong with it that this can see. Nothing was",
                "  changed in the database. Check the path and the permissions.",
            });
        }
        catch (Exception failure)
        {
            /*
              A STORE THAT CANNOT BE READ IS NOT A USAGE ERROR, and `anchor` shipped once without
              this: the exception escaped, System.CommandLine turned it into 1, and CombineExitCodes
              mapped that to UsageError -- so a database outage reported that the command line was
              wrong. Misconfigured matches what `verify` says for the same outage.
            */
            return (VerifyCommand.Misconfigured, new[]
            {
                "NO VERDICT: the audit store could not be read, so nothing was exported.",
                $"  {failure.GetType().Name}: {failure.Message}",
                "  This is NOT a statement about the chain, and no file was written. Check the",
                "  connection string and the keys first. If they are right, preserve the database",
                "  and escalate.",
            });
        }
    }
}
