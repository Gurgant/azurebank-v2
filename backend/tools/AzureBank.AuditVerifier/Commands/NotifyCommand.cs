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
/// A MODE OF THIS TOOL, NOT A SCHEDULED JOB — the anchor's decision, for the anchor's reason:
/// nothing in this deployment runs between sessions, so a control that needs a runner names the
/// operator, and says in the same breath that a control depending on somebody choosing to run it
/// does not constrain that person. The API writes the row (in the same save as the enrolment, so
/// the obligation is never lost and never survives a rollback); this verb is what reads it.
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
/// EXIT CODES, none of them new. 0: every waiting notice was written and marked. 2: nothing was
/// waiting — its own answer, not a success, for the reason <see cref="VerifyCommand.NothingToVerify"/>
/// gives. 3: the tool is not configured, the ring will not build, or the store could not be read.
/// 4: the command line was wrong — no contact, no directory, or a directory inside a git
/// repository. 5: interrupted. 6: at least one notice is still owed after the run — a reuse of
/// <see cref="AnchorCommand.NotRecorded"/>, whose meaning ("there was work to do and it could not
/// be recorded") stretches to a notice that could not be written or marked, at the cost of one
/// qualifying sentence in the existing copies of the list rather than a fifth copy.
/// </para>
/// </remarks>
public static class NotifyCommand
{
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
            var waiting = await context.SubscriberNotices
                .Where(n => n.DeliveredAt == null)
                .OrderBy(n => n.OccurredAt)
                .ToListAsync(cancellationToken);

            if (waiting.Count == 0)
            {
                return (VerifyCommand.NothingToVerify, new[]
                {
                    "NOTHING TO NOTIFY: no notice is owed.",
                    "  Every recorded notice has been rendered, or none was ever recorded. Not a success",
                    "  and not a failure: nothing was waiting.",
                });
            }

            var now = DateTime.UtcNow;

            foreach (var notice in waiting)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var reference = notice.Id.ToString("N");

                var address = await context.Users
                    .AsNoTracking()
                    .Where(u => u.Id == notice.UserId)
                    .Select(u => u.Email)
                    .SingleOrDefaultAsync(cancellationToken);

                if (string.IsNullOrWhiteSpace(address))
                {
                    lines.Add($"NO ADDRESS: notice {reference} for user {notice.UserId:N} has no email on the account; still owed.");
                    owed++;
                    continue;
                }

                /*
                  A LINE BREAK IN THE ADDRESS WOULD BECOME A HEADER. The address heads the message,
                  and RFC 5322 headers end at CRLF, so an email holding one -- unreachable through
                  registration, reachable by anybody who can write the table -- would inject whatever
                  followed it (a Bcc:, say). Refused here as unusable, and refused again by the
                  transport in case a second caller ever skips this check.
                */
                if (address.AsSpan().IndexOfAny('\r', '\n', '\0') >= 0)
                {
                    lines.Add($"NO ADDRESS: notice {reference} for user {notice.UserId:N} has an email on the account that cannot head a message (it contains a line break); still owed.");
                    owed++;
                    continue;
                }

                /*
                  JOINED BY (ACTOR, EVENT), NEVER BY TIME: the notice and the audit row read two
                  clocks milliseconds apart. An absent row is reported and the notice is still
                  rendered -- see the class remarks.
                */
                var backed = await context.AuditEvents
                    .AnyAsync(e => e.ActorUserId == notice.UserId && e.Event == notice.Event, cancellationToken);
                if (!backed)
                {
                    lines.Add($"NO AUDIT ROW backs notice {reference}: no {notice.Event} row exists for that user. "
                              + "The notice is rendered anyway; the absence is the finding.");
                }

                RenderedNotice rendered;
                try
                {
                    rendered = NoticeRenderer.Render(notice, contact, now);
                }
                catch (InvalidOperationException)
                {
                    lines.Add($"NOT NOTIFIED: notice {reference} names event {notice.Event}, which this build cannot render; still owed.");
                    owed++;
                    continue;
                }

                string receipt;
                try
                {
                    receipt = await transport.DeliverAsync(rendered, address, fullDirectory, cancellationToken);
                }
                catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
                {
                    // The TYPE only: an I/O message can echo the path, and a relay's can echo the recipient.
                    lines.Add($"NOT NOTIFIED: notice {reference} could not be written ({failure.GetType().Name}); still owed.");
                    owed++;
                    continue;
                }

                // Per notice, read after the write: a batch rendered slowly would otherwise date every
                // file to the moment the run began rather than the moment each was produced.
                notice.DeliveredAt = DateTime.UtcNow;
                notice.DeliveryReceipt = receipt;
                try
                {
                    await context.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateConcurrencyException)
                {
                    /*
                      ANOTHER RUN MARKED IT FIRST. The file this run wrote is a second copy of a
                      notice the other run also produced; at-least-once is the honest description
                      of this transport, and the duplicate is named rather than hidden. Not owed:
                      the row is marked, by somebody.
                    */
                    context.Entry(notice).State = EntityState.Detached;
                    lines.Add($"NOT NOTIFIED by this run: notice {reference} was marked by another run while this one wrote {receipt}; that file is a duplicate.");
                    continue;
                }

                lines.Add($"  {receipt} <- notice {reference}, {notice.Event} at {notice.OccurredAt:yyyy-MM-dd HH:mm:ss}Z");
                written++;
            }

            lines.Insert(0, $"NOTIFIED {written} of {waiting.Count} waiting notices into {fullDirectory}");
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
    /// True when the directory, or any directory above it, is a git working tree — where it SITS and
    /// where it POINTS.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The mechanical half of "never commit a spool of addresses"; the runbook's delete-after
    /// sentence is the other half. A <c>.git</c> FILE counts too — that is what a worktree carries.
    /// </para>
    /// <para>
    /// LINKS ARE FOLLOWED. A symbolic link or a junction sitting outside every repository can point
    /// into one, and the files would land where it points; on Windows git treats a junction as an
    /// ordinary directory and stages what is inside it. So the walk is done twice: over the path as
    /// typed, and over its physical target once every link on it is resolved. Either being inside a
    /// working tree refuses the directory.
    /// </para>
    /// </remarks>
    internal static bool InsideAGitRepository(string fullDirectory)
    {
        if (AncestorHoldsGit(fullDirectory))
        {
            return true;
        }

        var physical = PhysicalPath(fullDirectory);
        return !string.Equals(physical, fullDirectory, StringComparison.OrdinalIgnoreCase)
               && AncestorHoldsGit(physical);
    }

    private static bool AncestorHoldsGit(string fullDirectory)
    {
        for (var current = new DirectoryInfo(fullDirectory); current is not null; current = current.Parent)
        {
            var marker = Path.Combine(current.FullName, ".git");
            if (Directory.Exists(marker) || File.Exists(marker))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The directory with every link on its path resolved to what it points at.
    /// </summary>
    /// <remarks>
    /// Walks upward to the deepest link, replaces that segment by its final target, and repeats on
    /// the result until no segment is a link. Bounded, so a link loop ends the walk instead of the
    /// process; an unreadable segment is left as it is rather than guessed at.
    /// </remarks>
    internal static string PhysicalPath(string fullDirectory)
    {
        var path = fullDirectory;
        for (var hops = 0; hops < 32; hops++)
        {
            string? resolved = null;
            for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
            {
                FileSystemInfo? target;
                try
                {
                    target = current.Exists ? current.ResolveLinkTarget(returnFinalTarget: true) : null;
                }
                catch (IOException)
                {
                    target = null;
                }

                if (target is null)
                {
                    continue;
                }

                var relative = Path.GetRelativePath(current.FullName, path);
                resolved = relative == "." ? target.FullName : Path.Combine(target.FullName, relative);
                break;
            }

            if (resolved is null)
            {
                return path;
            }

            path = Path.GetFullPath(resolved);
        }

        return path;
    }
}
