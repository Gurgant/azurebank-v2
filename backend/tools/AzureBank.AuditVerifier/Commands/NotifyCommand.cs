using System.CommandLine;
using System.CommandLine.Invocation;
using System.Data.Common;
using AzureBank.Infrastructure.Notices;
using AzureBank.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AzureBank.AuditVerifier.Commands;

/// <summary>
/// Renders every notice the API has recorded as owed to an account holder, one file each, into a
/// directory the operator names, and marks each row delivered (ADR-0045).
/// </summary>
/// <remarks>
/// <para>
/// ONE OF TWO RUNNERS since ADR-0048, and the one that needs a person. Until 2026-09-04 this was
/// "a mode of this tool, not a scheduled job" — the anchor's decision, for a deployment in which
/// nothing ran between sessions. The API now runs a relay that claims and delivers the same rows
/// on a period when <c>Notices:Runner</c> names it; this verb is for a store where it does not, and
/// for the day it is down. Both use <see cref="NoticeClaim"/>: the verb CLAIMS what it delivers,
/// under its own name and a short lease, so the two cannot both hold a row at the same moment.
/// The API writes the row (in the same save as the enrolment, so the obligation is never lost and
/// never survives a rollback); this verb and the relay are what read it.
/// </para>
/// <para>
/// ⚠️ WHAT THIS DOES NOT DO. It does not send. The transport writes an RFC 5322 message into a
/// pickup directory on this machine, addressed to the email held on the account, and a file this
/// machine wrote to this machine's disk has been seen by nobody — <c>ExportCommand</c> says the same
/// of an anchor. The notice has reached the edge of the deployment, and the ADR calls that what it
/// is: the independence clause of NIST SP 800-63B-4 §4.1.2.1 met (the session that enrolled the
/// PIN cannot read this table, redirect the address or suppress this run), the delivery of §4.6 not
/// met. Nothing here says "sent".
/// </para>
/// <para>
/// NO CHAIN WALK, AND NEVER EXIT 1. A verb that could report CHAIN BROKEN while writing mail would
/// make a tampered table read as a delivery problem. <c>verify</c> and <c>evidence</c> are the verbs
/// for that question; the runbook says to run <c>verify</c> first after an incident. What this verb
/// does check is whether the audit row the notice belongs to still exists, and it reports an
/// absence as a finding rather than refusing — a missing row is the thing an operator most needs
/// to hear about, and withholding the notice would punish the account holder for it.
/// </para>
/// <para>
/// THE ADDRESS NEVER REACHES THE CONSOLE. It is read from the account, handed to the transport for
/// the <c>To:</c> header, and printed nowhere — not in a receipt, not in a failure line, and not
/// through an exception message, which is why failures name the exception TYPE only (an I/O error
/// can echo the path it was writing, and a relay's refusal can echo the recipient).
/// </para>
/// <para>
/// EXIT CODES, none of them new. 0: every notice this run claimed was written and marked. 2:
/// nothing was FREE — no notice is owed, or every owed one is leased by a live runner, and the
/// line says which — its own answer, not a success, for the reason
/// <see cref="VerifyCommand.NothingToVerify"/> gives. 3: the tool is not configured, the ring will not build, or the store could not be read.
/// 4: the command line was wrong — no contact, no directory, or a directory inside a git
/// repository. 5: interrupted. 6: at least one notice is still owed after the run — a reuse of
/// <see cref="AnchorCommand.NotRecorded"/>, whose meaning ("there was work to do and it could not
/// be recorded") stretches to a notice that could not be written or marked, at the cost of one
/// qualifying sentence in the existing copies of the list rather than a fifth copy.
/// </para>
/// </remarks>
public static class NotifyCommand
{
    /// <summary>
    /// How long this run holds what it claims. Long enough to render a large spool, short enough
    /// that a verb killed half-way frees its rows to the relay within minutes.
    /// </summary>
    internal static readonly TimeSpan VerbLease = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How many free rows one claim takes; the verb claims batch after batch until none is free or
    /// its lease lapses, so a backlog is still rendered whole while each claim stays small.
    /// Settable so a test can drive a second batch with two rows instead of a hundred and one.
    /// </summary>
    internal static int VerbBatch { get; set; } = 100;

    public static Command Create(IServiceProvider services)
    {
        var command = new Command(
            "notify",
            "Render every notice owed to an account holder into a pickup directory, and mark it delivered.");

        var directoryArgument = new Argument<string>(
            "directory",
            "An existing directory OUTSIDE any git repository; one .eml file is written per notice.");
        command.AddArgument(directoryArgument);

        var contactOption = new Option<string>(
            "--contact",
            "How a recipient repudiates the event: an address, a number. Mandatory content of the "
            + "notice (NIST SP 800-63B-4 §4.6), so nothing is rendered without it.")
        {
            IsRequired = true,
        };
        command.AddOption(contactOption);

        command.SetHandler(async (InvocationContext invocation) =>
        {
            var directory = invocation.ParseResult.GetValueForArgument(directoryArgument);
            var contact = invocation.ParseResult.GetValueForOption(contactOption) ?? string.Empty;
            var (exitCode, lines) = await RunAsync(services, directory, contact, invocation.GetCancellationToken());

            // Recorded before printed, for the reason every sibling records: writing to a closed
            // stdout throws, an escaping exception becomes exit 1, and 1 means CHAIN BROKEN.
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
        string directory,
        string contact,
        CancellationToken cancellationToken)
    {
        /*
          THE COMMAND LINE IS CHECKED BEFORE THE STORE IS TOUCHED, for the reason export records: it
          is the thing the operator just typed and the cheapest mistake to name, and it needs no
          configuration to judge. A tool that opened a connection first would answer a missing
          contact with a connection error on a machine where the database is down.
        */
        if (string.IsNullOrWhiteSpace(contact) || contact.Contains('\0'))
        {
            return (VerifyCommand.UsageError, new[]
            {
                "NOT NOTIFIED: no repudiation contact was given.",
                "  NIST SP 800-63B-4 §4.6 makes contact information mandatory content of the notice,",
                "  so a notice without one is not rendered. Pass --contact with what a recipient uses",
                "  to say \"this was not me\".",
                "    notify ../azurebank-notices --contact \"security@your-bank.example, +00 000 0000\"",
            });
        }

        if (string.IsNullOrWhiteSpace(directory) || directory.Contains('\0'))
        {
            return (VerifyCommand.UsageError, new[]
            {
                "NOT NOTIFIED: that is not a directory.",
                "  `notify` writes one file per notice into an existing directory outside any git",
                "  repository. A blank path or one carrying a NUL character cannot name one.",
            });
        }

        string fullDirectory;
        try
        {
            fullDirectory = Path.GetFullPath(directory);
        }
        catch (Exception invalid) when (invalid is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (VerifyCommand.UsageError, new[]
            {
                "NOT NOTIFIED: that is not a directory.",
                $"  The path could not be resolved ({invalid.GetType().Name}).",
            });
        }

        if (!Directory.Exists(fullDirectory))
        {
            return (VerifyCommand.UsageError, new[]
            {
                $"NOT NOTIFIED: {fullDirectory} is not an existing directory.",
                "  `notify` does not create it: a spool of addresses should land only where somebody",
                "  meant it to. Create the directory, outside any git repository, and run again.",
            });
        }

        if (InsideAGitRepository(fullDirectory))
        {
            return (VerifyCommand.UsageError, new[]
            {
                $"NOT NOTIFIED: {fullDirectory} is inside a git repository.",
                "  A pickup directory is a spool of addresses at rest, and one under a repository is",
                "  one commit away from being published. Name a directory outside the tree.",
            });
        }

        try
        {
            services.GetService<IStartupValidator>()?.Validate();
        }
        catch (OptionsValidationException invalid)
        {
            var reasons = new List<string>
            {
                "CANNOT NOTIFY: this tool is not configured to read the store.",
            };
            reasons.AddRange(invalid.Failures.Select(failure => $"  {failure}"));
            return (VerifyCommand.Misconfigured, reasons.ToArray());
        }

        using var scope = services.CreateScope();

        /*
          RESOLVED INSIDE A TRY, for the transitive reason export records: building the context
          builds the ring, the ring can refuse, and an unguarded refusal here would exit 4 with a
          stack trace for a correct command line. All five verbs answer that refusal with 3.
        */
        AzureBankDbContext context;
        INoticeTransport transport;
        try
        {
            context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
            transport = scope.ServiceProvider.GetRequiredService<INoticeTransport>();
        }
        catch (AuditKeyRingException ring)
        {
            return VerifyCommand.RingNotConfigured(ring);
        }

        var lines = new List<string>();
        var written = 0;
        var owed = 0;

        try
        {
            // The clock is a seam so a test can lapse the verb's lease without waiting two minutes.
            var clock = services.GetService<TimeProvider>() ?? TimeProvider.System;
            var now = clock.GetUtcNow().UtcDateTime;
            var leaseEnd = now.Add(VerbLease);
            var runner = NoticeClaim.RunnerNameFor(
                "verb", Environment.MachineName, Environment.ProcessId, Guid.NewGuid());

            /*
              THE VERB CLAIMS TOO (ADR-0048). A row a live runner holds is not this run's: rendering it
              would produce the duplicate the lease exists to prevent, and a row this run read without
              claiming could be taken by the relay between the read and the write. So the verb takes
              the same lease the relay takes, under its own name, batch by batch, and delivers only
              what it holds. Rows another runner holds are counted and named, not touched: if that
              runner dies, its lease lapses and the next claim — this verb or the relay — takes them.
            */
            await NoticeClaim.ClaimAsync(context, runner, now, leaseEnd, VerbBatch, cancellationToken);
            var leased = await NoticeClaim.HeldByOthersAsync(context, runner, now, cancellationToken);
            var waiting = await NoticeClaim.HeldBy(context, runner, now).ToListAsync(cancellationToken);

            if (waiting.Count == 0)
            {
                return (VerifyCommand.NothingToVerify, leased == 0
                    ? new[]
                    {
                        "NOTHING TO NOTIFY: no notice is owed.",
                        "  Every recorded notice has been rendered, or none was ever recorded. Not a success",
                        "  and not a failure: nothing was waiting.",
                    }
                    : new[]
                    {
                        $"NOTHING TO NOTIFY: {leased} owed notice(s) are leased by a live runner and none is free.",
                        "  The API's relay is delivering them. If it is not running, its leases lapse within",
                        "  minutes and a later run of this verb takes them.",
                    });
            }

            var run = new NoticeDeliveryRun(context, transport);
            var claimedTotal = 0;
            var lapsed = false;
            var attempted = new HashSet<Guid>();

            /*
              ONE UNIT PER ROW, SHARED WITH THE RELAY (ADR-0048): NoticeDeliveryRun reads the address,
              checks the evidence, renders, delivers and marks. This verb owns the words, and the
              words are the ones it printed when it owned the steps too. The address is in none.
              Batch after batch until nothing is free — or the verb's own lease lapses, after which
              the rows it has not reached are free to the next claim and delivering them here would
              be the duplicate the lease exists to prevent, exactly as the relay stops.
            */
            while (waiting.Count > 0)
            {
                claimedTotal += waiting.Count;

                foreach (var notice in waiting)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (clock.GetUtcNow().UtcDateTime >= leaseEnd)
                    {
                        lapsed = true;
                        break;
                    }

                    attempted.Add(notice.Id);
                    var result = await run.DeliverAsync(notice, contact, fullDirectory, cancellationToken);
                    var reference = result.Reference;

                    if (result.AuditRowMissing)
                    {
                        lines.Add($"NO AUDIT ROW backs notice {reference}: no {notice.Event} row exists for that user. "
                                  + "The notice is rendered anyway; the absence is the finding.");
                    }

                    switch (result.Outcome)
                    {
                        case NoticeOutcome.NoAddress:
                            lines.Add($"NO ADDRESS: notice {reference} for user {notice.UserId:N} has no email on the account; still owed.");
                            owed++;
                            break;
                        case NoticeOutcome.UnusableAddress:
                            lines.Add($"NO ADDRESS: notice {reference} for user {notice.UserId:N} has an email on the account that cannot head a message (it contains a line break); still owed.");
                            owed++;
                            break;
                        case NoticeOutcome.Unrenderable:
                            lines.Add($"NOT NOTIFIED: notice {reference} names event {notice.Event}, which this build cannot render; still owed.");
                            owed++;
                            break;
                        case NoticeOutcome.TransportFailed:
                            lines.Add($"NOT NOTIFIED: notice {reference} could not be written ({result.FailureType}); still owed.");
                            owed++;
                            break;
                        case NoticeOutcome.MarkedByAnother:
                            lines.Add($"NOT NOTIFIED by this run: notice {reference} was marked by another run while this one wrote {result.Receipt}; that file is a duplicate.");
                            break;
                        case NoticeOutcome.Delivered:
                            lines.Add($"  {result.Receipt} <- notice {reference}, {notice.Event} at {notice.OccurredAt:yyyy-MM-dd HH:mm:ss}Z");
                            written++;
                            break;
                    }
                }

                if (lapsed)
                {
                    break;
                }

                // The next batch. The re-read by name would also return rows this run already
                // attempted and could not deliver — still owed, still held — so those are excluded:
                // one attempt per row per run, and each row counted once.
                var more = await NoticeClaim.ClaimAsync(
                    context, runner, clock.GetUtcNow().UtcDateTime, leaseEnd, VerbBatch, cancellationToken);
                waiting = more == 0
                    ? []
                    : (await NoticeClaim.HeldBy(context, runner, clock.GetUtcNow().UtcDateTime)
                        .ToListAsync(cancellationToken))
                        .Where(n => !attempted.Contains(n.Id))
                        .ToList();
            }

            lines.Insert(0, $"NOTIFIED {written} of {claimedTotal} waiting notices into {fullDirectory}");
            if (lapsed)
            {
                // Attempted is the count that matters: a row another run marked first was attempted too.
                var unreached = claimedTotal - attempted.Count;
                owed += unreached;
                lines.Add($"LEASE LAPSED after {attempted.Count} of {claimedTotal}: the verb held its rows for {VerbLease.TotalMinutes:0} minutes and "
                          + $"{unreached} were not reached. They are free to the next claim — this verb again, or the relay. Run again.");
            }
            if (leased > 0)
            {
                lines.Add($"{leased} more owed notice(s) are leased by a live runner and were left to it.");
            }
            lines.Insert(1, "  Each file is a complete message addressed to the email held on the account, and it has");
            lines.Insert(2, "  reached this machine's disk and nobody else: nothing here sends. Point a relay at the");
            lines.Insert(3, "  directory or move the files yourself, and delete the spool afterwards.");

            if (owed > 0)
            {
                lines.Add($"{owed} still owed. Fix what each line above names and run again; a marked notice is never rewritten.");
                return (AnchorCommand.NotRecorded, lines.ToArray());
            }

            return (VerifyCommand.Intact, lines.ToArray());
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // The token, not the type: EF surfaces a cancelled query as TaskCanceledException,
            // OperationCanceledException or, mid-command, a provider exception with the token set.
            return (VerifyCommand.Interrupted, new[]
            {
                "INTERRUPTED: the run was cancelled before it finished.",
                $"  {written} notice(s) were written and marked before the interruption and stay marked;",
                "  the rest are still owed. Run again.",
            });
        }
        catch (Exception failure)
        {
            var cause = failure is DbException ? failure : failure.InnerException as DbException ?? failure;
            return (VerifyCommand.Misconfigured, new[]
            {
                $"CANNOT NOTIFY: the store could not be read or written ({cause.GetType().Name}).",
                "  Not a statement about any notice: nothing is rendered from a row this run could not",
                "  read, and a row it could not mark is still owed. Check the connection string and the",
                "  database, then run again.",
            });
        }
    }

    /// <summary>
    /// The guard the relay's option validation shares (ADR-0048); kept here by name for the tests
    /// that pin it, delegating to <see cref="PickupDirectoryGuard"/>.
    /// </summary>
    internal static bool InsideAGitRepository(string fullDirectory) =>
        PickupDirectoryGuard.InsideAGitRepository(fullDirectory);

    internal static string PhysicalPath(string fullDirectory) =>
        PickupDirectoryGuard.PhysicalPath(fullDirectory);
}
