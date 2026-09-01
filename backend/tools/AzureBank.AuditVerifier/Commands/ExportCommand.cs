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

    /// <summary>
    /// What to exit with when the copy was not written, given whatever the walk had already found.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>THE VERDICT ABOUT THE CHAIN IS STILL THE VERDICT.</b> That is not a new rule invented
    /// here — it is the sentence <c>AnchoringABrokenChain_RecordsAGapMarkerAndSaysTheChainIsBroken</c>
    /// gives for why <c>anchor</c> exits 1 over a broken chain even when it did record a marker. This
    /// verb was breaking it: a chain that had already failed to verify, followed by a path collision
    /// or a failed write, exited 6 and printed "the chain was read and nothing is wrong with it that
    /// this can see". The write problem is fixed by naming a different path in one second; the broken
    /// chain is an incident, and it was the half being swallowed.
    /// </para>
    /// <para>
    /// So a completed BROKEN verdict wins the exit code and is stated first, with the writing
    /// failure kept underneath it rather than dropped. A completed INTACT verdict leaves
    /// <see cref="AnchorCommand.NotRecorded"/> in place, which is the more informative of the two
    /// there — 0 would claim a copy exists. A null verdict means the walk never finished, so there is
    /// nothing to preserve and nothing to hide.
    /// </para>
    /// </remarks>
    private static (int ExitCode, string[] Lines) NotWritten(
        AuditAnchorChainVerification? verdict, IEnumerable<string> lines)
    {
        if (verdict is not { IsIntact: false } broken)
        {
            return (AnchorCommand.NotRecorded, [.. lines]);
        }

        return (VerifyCommand.Broken,
        [
            "CHAIN BROKEN -- and separately, no copy was written.",
            $"  {broken.Kind} at anchor {broken.FirstBrokenSequence?.ToString("N0") ?? "(unknown)"}:"
                + $" {broken.Reason}",
            "  This exits with the CHAIN verdict, not the writing one. The export can be retried",
            "  against another path in a second; a chain that does not verify cannot.",
            string.Empty,
            .. lines,
        ]);
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

          ⚠️ PURE STRING LOGIC, AND THE TWO EARLIER VERSIONS OF THIS CHECK BOTH FAILED ON A
          PLATFORM I DID NOT RUN. The first used string.IsNullOrWhiteSpace, which returns FALSE for
          an embedded NUL -- so "a\0b.jsonl" walked past it, read the chain, and hit the
          ArgumentException this check exists to stop. The second replaced it with Path.GetFullPath,
          measured on Windows to throw for empty, whitespace AND the NUL. CI on ubuntu-latest
          disagreed twice: `export "   "` SUCCEEDED there and wrote a file named three spaces.
          Confirmed outside .NET as well -- `touch "   "` on Ubuntu creates the file; `ls -b` shows
          it. Whitespace is a legal filename on Linux, so GetFullPath classifies differently
          depending on where the tool runs.

          So this rejects exactly the shapes that are a MISTAKE EVERYWHERE, using logic that cannot
          vary: a path that is empty or blank, and a path containing a NUL. Nobody means to name an
          audit copy three spaces, and an operator who typed that fumbled their quoting -- refusing
          it identically on every platform is worth more to an operator tool than honouring an
          exotic but legal filename. NUL is invalid on every filesystem there is.

          Everything else is left to the filesystem to judge, which is the part the previous version
          got wrong by trying to pre-empt. A path the platform rejects for its own reasons lands in
          the write-failure branches below and reports exit 6 with the exception named, and
          NoBadPathIsEVERReportedAsAStoreFailure pins the only answer that would be wrong: 3, the
          code the runbook says to wire an alert on.
        */
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0'))
        {
            return (VerifyCommand.UsageError, new[]
            {
                "NOT EXPORTED: that is not a usable path.",
                "  `export` needs somewhere to write the copy, and a blank argument or one carrying",
                "  a NUL character cannot name a file. Nothing was read and nothing was written.",
                "  Name a file that does not exist yet:",
                "    export ./anchors-2026-08-27.jsonl",
            });
        }

        /*
          THE KEY IS VALIDATED AFTER THE PATH AND BEFORE THE SCOPE, and it has to be done here rather
          than left to the host, for the reason VerifyCommand records: validating during construction
          made --help unreachable on an unconfigured machine.

          ⚠️ THIS COMMAND DID NOT SHIP WITHOUT A KEY GUARD -- IT SHIPPED WITH ONE THAT CANNOT RUN.
          The guard is still below, reading options.Value, and reading options.Value is what triggers
          the validation that checks the identical predicate. So it threw one line before the guard
          and nothing caught it. Measured 2026-08-28 on the shipped build, Audit__AnchorKey unset:
          `export` and `anchor` printed an unhandled OptionsValidationException and exited 4 -- "the
          command line was wrong" -- for a correct command line, while `verify` answered 3 with a
          sentence. Only VerifyCommand ran the validator inside a try, so only VerifyCommand survived
          the change that broke the other two.

          ⚠️ AND THE SUITE WAS GREEN THROUGHOUT. AMissingAnchorKeyIsRefusedBeforeAnythingIsRead builds
          its provider by hand with Options.Create(...), which registers no validation and no
          IStartupValidator, so options.Value returns the bad value quietly and the guard below is
          reached. The test asserts the branch that production cannot enter. It is the composition
          root that differs, not the command -- which is the repository's oldest rule wearing
          different clothes: a test that asserts against a stand-in proves something about the
          stand-in. ExportRefusesAnUnusableAnchorKeyWithAVerdictCode_NotAUsageError now composes it
          the way Program.cs does, and reddens on 4.

          (This sentence named a test that does not exist -- a plausible name for one, elided, which
          no suite has ever declared. The guard written to catch exactly that could not see it: its
          citation scan classified comments by a per-line prefix, so it never read the inside of a
          block comment like this one. The dead name is deliberately not repeated here, because
          repeating it is another citation and the guard would be right to refuse it again.)

          The path guard above stays FIRST: a typo in the thing just typed is still the cheapest
          mistake to name, and it needs no configuration to catch.
        */
        try
        {
            services.GetService<IStartupValidator>()?.Validate();
        }
        catch (OptionsValidationException invalid)
        {
            var reasons = new List<string>
            {
                "CANNOT EXPORT: this tool is not configured to read the chain.",
            };
            reasons.AddRange(invalid.Failures.Select(failure => $"  {failure}"));
            return (VerifyCommand.Misconfigured, reasons.ToArray());
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

        /*
          RESOLVED INSIDE A TRY, and for THIS verb the reason is the transitive one -- building the
          context builds the ring, the ring can refuse, and until this guard existed the refusal left
          this verb exiting 4 with a stack trace. IAuditAnchorChain alone would not have triggered it
          -- it depends only on IOptions -- which is exactly why the guard has to wrap the context.
        */
        IAuditAnchorChain anchors;
        AzureBankDbContext context;
        try
        {
            anchors = scope.ServiceProvider.GetRequiredService<IAuditAnchorChain>();
            context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        }
        catch (AuditKeyRingException ring)
        {
            return VerifyCommand.RingNotConfigured(ring);
        }

        /*
          THE VERDICT OUTLIVES THE TRY, because a failure to WRITE must not be able to bury what the
          walk already found. It is null until the walk completes, which is what separates "no
          verdict yet" from "intact" -- a bool could not.
        */
        AuditAnchorChainVerification? verdict = null;

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
            verdict = state;

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

            /*
              THE COLLISION CATCH WRAPS THE CONSTRUCTION AND NOTHING ELSE, and the first version of
              this wrapped the write as well -- which made it able to say something false.

              MEASURED: create with FileMode.CreateNew, write twelve bytes, then have the write fail.
              The file is still there, twelve bytes long, and File.Exists returns TRUE. So the filter
              `when (File.Exists(path))` matched, and the operator was told "something else created
              it between the check and the write" and "nothing was changed here". Both false: this
              run created it, and it left a partial copy behind. A partial export installed as a
              reference is the worst artefact this verb can produce, and it was being announced as
              the safest outcome.

              Separating them means only a CreateNew failure can be read as a collision. A write that
              fails after the file exists falls to the write-failure branch below, which says the
              file may be partial and has to go.
            */
            FileStream file;
            try
            {
                file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            catch (IOException) when (File.Exists(path))
            {
                /*
                  THE WINDOW THE PRE-CHECK CANNOT CLOSE, reported as what it is rather than as a disk
                  failure. Something arrived at this path while the chain was being read -- another
                  run, or somebody else -- and FileMode.CreateNew refused rather than truncating it.
                  Measured shape: IOException, HResult 0x80070050, "The file ... already exists", and
                  File.Exists is true from inside this filter, which is what makes the test portable
                  rather than a check on an HResult that differs per platform.
                */
                return NotWritten(verdict, new[]
                {
                    $"NOT EXPORTED: {path} appeared while this run was reading the chain.",
                    "  Something else created it between the check and the write, and it was refused",
                    "  rather than overwritten -- an earlier copy is the only thing a later one can be",
                    "  compared against. Nothing was changed here. Export to a new path and keep both.",
                });
            }

            await using (file)
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
        catch (IOException failure)
        {
            /*
              A FAILED WRITE IS "NOTHING CAME OF IT", NOT "NO VERDICT". The chain was read and it
              said something; what failed was the recording. That is exactly the distinction exit 6
              was added for in `anchor`, and reusing it here is why this verb needs no code of its
              own -- a full disk and a read-only directory are the same class of failure as a
              refused append.
            */
            return NotWritten(verdict, new[]
            {
                $"NOT EXPORTED: {path} could not be written.",
                $"  {failure.GetType().Name}: {failure.Message}",
                "  Nothing was changed in the database. Check the path, the permissions and the free",
                "  space. Any verdict about the chain itself is stated above this, or not at all.",
                "  ⚠️ A PARTIAL FILE MAY BE AT THAT PATH. The copy is created before it is filled,",
                "  so a write that fails part-way leaves a truncated one behind -- and a truncated",
                "  export installed as a reference is worse than none, because a later comparison",
                "  would report a difference this machine created. Delete it before re-running.",
                "  This will not delete it: refusing to remove a copy is the whole point of the verb.",
            });
        }
        catch (UnauthorizedAccessException failure)
        {
            // Not an IOException -- .NET raises this one for permissions and for a directory in the
            // way, and it is the same class of failure as the branch above.
            return NotWritten(verdict, new[]
            {
                $"NOT EXPORTED: {path} could not be written.",
                $"  {failure.GetType().Name}: {failure.Message}",
                "  Nothing was changed in the database. Check the path and the permissions. Any",
                "  verdict about the chain itself is stated above this, or not at all.",
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
