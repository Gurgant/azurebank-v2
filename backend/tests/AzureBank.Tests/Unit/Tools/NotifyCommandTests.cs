using System.Text;
using AzureBank.AuditVerifier.Commands;
using AzureBank.AuditVerifier.Notices;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Shared.Options;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AzureBank.Tests.Unit.Tools;

/// <summary>
/// The notify verb: what it renders, where the address goes and does not go, and every answer it
/// gives that is not a delivery (ADR-0045).
/// </summary>
/// <remarks>
/// <para>
/// Hand-built InMemory provider, the <see cref="ExportCommandTests"/> idiom, with the transport
/// substituted by <see cref="RecordingTransport"/> so the envelope — rendered notice plus address —
/// is what the assertions read. One test uses the real <see cref="PickupDirectoryTransport"/>
/// against a temp directory, and that directory is deleted in <see cref="Dispose"/> whatever the
/// outcome; nothing is written inside the repository, which the verb itself refuses anyway.
/// </para>
/// <para>
/// What InMemory cannot prove and does not pretend to: that two concurrent runs cannot both mark
/// one row. That is <c>SubscriberNoticeSqlServerTests</c>, on the provider that enforces the token.
/// </para>
/// </remarks>
public class NotifyCommandTests : IDisposable
{
    private const string ChainKey = "notify-command-tests-chain-key-0123456789ab";
    private const string AnchorKey = "notify-command-tests-anchor-key-9876543210xy";
    private const string Contact = "security@your-bank.example, +00 000 0000";
    private const string Address = "owner@example.com";

    private readonly AzureBankDbContext _context;
    private readonly RecordingTransport _transport = new();
    private readonly string _directory;

    public NotifyCommandTests()
    {
        var options = Options.Create(new AuditOptions { ChainKey = ChainKey, AnchorKey = AnchorKey });
        var chain = new AuditChain(options, NullLogger<AuditChain>.Instance);

        _context = new AzureBankDbContext(
            new DbContextOptionsBuilder<AzureBankDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options,
            timeProvider: null,
            auditChain: chain);

        _directory = Path.Combine(Path.GetTempPath(), "azurebank-notify-" + Guid.NewGuid().ToString("N"));
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

        GC.SuppressFinalize(this);
    }

    private ServiceProvider Provider(INoticeTransport? transport = null)
    {
        var collection = new ServiceCollection();
        collection.AddSingleton<IOptions<AuditOptions>>(
            Options.Create(new AuditOptions { ChainKey = ChainKey, AnchorKey = AnchorKey }));
        collection.AddSingleton(_context);
        collection.AddSingleton(transport ?? _transport);
        return collection.BuildServiceProvider();
    }

    private async Task<Guid> OwnerAsync(string? email = Address, string pinHash = "pin-hash-sentinel")
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
            PinHash = pinHash,
            PasswordHash = "password-hash-sentinel",
        });
        await _context.SaveChangesAsync();
        return id;
    }

    private async Task<SubscriberNotice> OwedAsync(
        Guid userId, bool withAuditRow = true, string? @event = null)
    {
        // Defaulted rather than required so every caller written before ADR-0047 still reads as a
        // test about the enrolment. The audit row takes the SAME name on purpose: `notify` joins a
        // notice to its evidence by (ActorUserId, Event), so a helper that let the two drift would
        // make `ANoticeWithoutItsAuditRow_…` pass for the wrong reason.
        @event ??= SecurityEvents.PinEnrolled;

        if (withAuditRow)
        {
            _context.AuditEvents.Add(new AuditEvent
            {
                Id = Guid.CreateVersion7(),
                OccurredAt = DateTime.UtcNow,
                Event = @event,
                Outcome = AuditOutcome.Succeeded,
                ActorUserId = userId,
                RowHash = string.Empty,
            });
        }

        var notice = new SubscriberNotice
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Event = @event,
            OccurredAt = new DateTime(2026, 9, 3, 10, 15, 0, DateTimeKind.Utc),
        };
        _context.SubscriberNotices.Add(notice);
        await _context.SaveChangesAsync();
        return notice;
    }

    private Task<(int ExitCode, string[] Lines)> RunAsync(
        ServiceProvider provider, string? directory = null, string contact = Contact) =>
        NotifyCommand.RunAsync(provider, directory ?? _directory, contact, CancellationToken.None);

    [Fact]
    public async Task APendingNotice_IsRenderedToTheAccountsAddress_AndMarkedDelivered()
    {
        var owner = await OwnerAsync();
        var notice = await OwedAsync(owner);
        await using var provider = Provider();

        var (exitCode, lines) = await RunAsync(provider);
        var text = string.Join("\n", lines);

        exitCode.Should().Be(VerifyCommand.Intact);
        _transport.Envelopes.Should().ContainSingle();
        var (rendered, toAddress, directory) = _transport.Envelopes[0];

        toAddress.Should().Be(Address, "the notice is addressed to the email held on the account, joined at delivery");
        directory.Should().Be(Path.GetFullPath(_directory));
        rendered.Subject.Should().Contain("AzureBank").And.Contain("transfer PIN");
        rendered.Body.Should().Contain("2026-09-03 10:15")
            .And.Contain("for the first time")
            .And.Contain(Contact)
            .And.Contain(notice.Id.ToString("N"));
        rendered.FileName.Should().Be($"{notice.Id:N}.eml");

        var stored = await _context.SubscriberNotices.AsNoTracking().SingleAsync(n => n.Id == notice.Id);
        stored.DeliveredAt.Should().NotBeNull();
        stored.DeliveryReceipt.Should().Be(RecordingTransport.Receipt);

        text.Should().Contain("NOTIFIED 1 of 1").And.Contain(notice.Id.ToString("N"));
        text.Should().NotContain("@", "the console never carries an address")
            .And.NotContain("owner", "nor anything that names the recipient");
    }

    [Fact]
    public async Task AChangeNotice_IsRenderedWithItsOwnWords_NotTheEnrolments()
    {
        /*
          The two kinds must not be interchangeable, and the assertions are written so that pointing
          the change arm at `PinEnrolled(...)` — the cheapest wrong edit — turns this red. A change
          costs the CURRENT PIN and never the password, so "for the first time" and "your account
          password was proved" are both FALSE here; the second would also tell a reader whose PIN was
          watched that their password had been used. Measured 2026-09-04 against the real verb, whose
          output is the sample in docs/notices/.
        */
        var owner = await OwnerAsync();
        var notice = await OwedAsync(owner, @event: SecurityEvents.PinChanged);
        await using var provider = Provider();

        var (exitCode, lines) = await RunAsync(provider);

        exitCode.Should().Be(VerifyCommand.Intact);
        var (rendered, _, _) = _transport.Envelopes.Should().ContainSingle().Subject;

        rendered.Subject.Should().Contain("was changed")
            .And.NotContain("was set", "a change is not an enrolment, and the subject is what a reader sees first");
        rendered.Body.Should().Contain("was changed")
            .And.Contain("proved the PIN that was in place before it")
            .And.Contain("Your account password was not")
            .And.Contain(Contact)
            .And.Contain(notice.Id.ToString("N"));
        rendered.Body.Should().NotContain("for the first time")
            .And.NotContain("password was proved in the same request",
                "the enrolment's proof sentence is false for a change");

        string.Join("\n", lines).Should().Contain(SecurityEvents.PinChanged,
            "the operator line names the kind, which is what a runbook is searched by");
    }

    [Fact]
    public async Task BothKinds_AreEachRenderedOnce_AndEachRowMarked()
    {
        // The pending query is kind-blind and must stay so: the ratified relay claims the same set.
        var owner = await OwnerAsync();
        var enrolled = await OwedAsync(owner);
        var changed = await OwedAsync(owner, @event: SecurityEvents.PinChanged);
        await using var provider = Provider();

        var (exitCode, lines) = await RunAsync(provider);

        exitCode.Should().Be(VerifyCommand.Intact);
        _transport.Envelopes.Should().HaveCount(2);
        string.Join("\n", lines).Should().Contain("NOTIFIED 2 of 2")
            .And.NotContain("NO AUDIT ROW", "each kind has an audit row of its own name");

        var stored = await _context.SubscriberNotices.AsNoTracking()
            .Where(n => n.Id == enrolled.Id || n.Id == changed.Id).ToListAsync();
        stored.Should().OnlyContain(n => n.DeliveredAt != null && n.DeliveryReceipt != null);
    }

    [Fact]
    public async Task AKindThisBuildCannotRender_StaysOwed_AndIsNamed()
    {
        /*
          Nothing covered this branch before ADR-0047, and it is the one that stops a future kind
          shipping half-built: a row whose Event has no renderer arm must stay OWED and be named, not
          be marked delivered and not be sent blank. Once a relay exists, a row marked delivered by
          mistake is a notice nobody will ever get.
        */
        var owner = await OwnerAsync();
        var notice = await OwedAsync(owner, @event: "PinAbdicated");
        await using var provider = Provider();

        var (exitCode, lines) = await RunAsync(provider);
        var text = string.Join("\n", lines);

        exitCode.Should().Be(
            AnchorCommand.NotRecorded,
            "an unrenderable notice is a finding, not a run that worked, and 6 is what 'still owed' means");
        _transport.Envelopes.Should().BeEmpty();
        text.Should().Contain("PinAbdicated", "the operator is told which kind this build cannot render");

        var stored = await _context.SubscriberNotices.AsNoTracking().SingleAsync(n => n.Id == notice.Id);
        stored.DeliveredAt.Should().BeNull("the row stays owed");
        stored.DeliveryReceipt.Should().BeNull();
    }

    [Fact]
    public async Task TheNoticeCarriesNoSecretNoLinkAndNotTheAddress()
    {
        /*
          THE RENDERER NEVER RECEIVED THE ADDRESS, so it cannot leak it; and it received no PIN, no
          hash and no name either. Everything the body must not contain is seeded with a sentinel
          that would be unmistakable if it leaked.
        */
        var owner = await OwnerAsync(pinHash: "PINHASH-SENTINEL-4242");
        await OwedAsync(owner);
        await using var provider = Provider();

        await RunAsync(provider);
        var (rendered, _, _) = _transport.Envelopes.Should().ContainSingle().Subject;
        var everything = rendered.Subject + "\n" + rendered.Body;

        everything.Should().NotContain(Address)
            .And.NotContain("PINHASH-SENTINEL")
            .And.NotContain("password-hash-sentinel")
            .And.NotContain("Sentinel")
            .And.NotContain("http", "no link: a notice with a link is a phishing template")
            .And.NotContain("sign out", "the T8 attacker holds the password and signs back in")
            .And.NotContain("ends every session");
        everything.Should().Contain("never be asked for")
            .And.Contain("IF THIS WAS NOT YOU")
            .And.Contain("addressed to", "never 'sent': nothing here sends");
    }

    [Fact]
    public async Task NothingWaiting_IsItsOwnAnswer_NotASuccess()
    {
        await using var provider = Provider();

        var (exitCode, lines) = await RunAsync(provider);

        exitCode.Should().Be(VerifyCommand.NothingToVerify, "an empty run is not a delivery");
        lines[0].Should().StartWith("NOTHING TO NOTIFY");
        _transport.Envelopes.Should().BeEmpty();
    }

    [Fact]
    public async Task AnAccountWithNoAddress_IsLeftOwed_AndSaidSo()
    {
        // Unreachable through /api/auth/register, reachable by seed or raw SQL: the column is nullable.
        var owner = await OwnerAsync(email: null);
        var notice = await OwedAsync(owner);
        await using var provider = Provider();

        var (exitCode, lines) = await RunAsync(provider);

        exitCode.Should().Be(AnchorCommand.NotRecorded, "a notice that could not be addressed is still owed");
        _transport.Envelopes.Should().BeEmpty();
        lines.Should().Contain(l => l.StartsWith("NO ADDRESS") && l.Contains(notice.Id.ToString("N")));
        (await _context.SubscriberNotices.AsNoTracking().SingleAsync(n => n.Id == notice.Id))
            .DeliveredAt.Should().BeNull();
    }

    [Fact]
    public async Task ANoticeWithoutItsAuditRow_IsStillDelivered_AndTheFindingIsPrinted()
    {
        var owner = await OwnerAsync();
        var notice = await OwedAsync(owner, withAuditRow: false);
        await using var provider = Provider();

        var (exitCode, lines) = await RunAsync(provider);

        exitCode.Should().Be(VerifyCommand.Intact, "the account holder is not punished for a missing row");
        _transport.Envelopes.Should().ContainSingle();
        lines.Should().Contain(l => l.StartsWith("NO AUDIT ROW") && l.Contains(notice.Id.ToString("N")));
        (await _context.SubscriberNotices.AsNoTracking().SingleAsync(n => n.Id == notice.Id))
            .DeliveredAt.Should().NotBeNull();
    }

    private static ServiceProvider UnreachableProvider()
    {
        // localhost,1 with a two-second connect timeout: refused fast (AuditVerifierReportTests).
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AzureBankDbContext>(o => o.UseSqlServer(
            "Server=localhost,1;Database=Nope;User Id=u;Password=p;TrustServerCertificate=True;Connect Timeout=2"));
        services.AddSingleton<IAuditChain>(new AuditChain(
            Options.Create(new AuditOptions { ChainKey = new string('k', 32) }),
            NullLogger<AuditChain>.Instance));
        services.AddSingleton<INoticeTransport>(new RecordingTransport());
        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData("   ", "contact", "no repudiation contact")]
    [InlineData("nul\0here", "contact", "no repudiation contact")]
    [InlineData(Contact, "   ", "not a directory")]
    [InlineData(Contact, "nul\0here", "not a directory")]
    public async Task ABlankOrNulArgument_IsRefusedBeforeTheStoreIsTouched(string contact, string directory, string expected)
    {
        await using var provider = UnreachableProvider();
        var started = DateTime.UtcNow;

        var (exitCode, lines) = await NotifyCommand.RunAsync(provider, directory, contact, CancellationToken.None);

        exitCode.Should().Be(VerifyCommand.UsageError);
        lines[0].Should().StartWith("NOT NOTIFIED").And.Contain(expected);
        (DateTime.UtcNow - started).Should().BeLessThan(
            TimeSpan.FromSeconds(1.5), "the refusal must come before any connection is attempted");
    }

    [Fact]
    public async Task ADirectoryInsideAGitRepository_IsRefusedBeforeTheStoreIsTouched()
    {
        /*
          The test binary itself lives under this repository's bin/, so its base directory is the
          cheapest possible example of a path that must be refused: a spool of addresses one commit
          away from being published.
        */
        await using var provider = UnreachableProvider();
        NotifyCommand.InsideAGitRepository(AppContext.BaseDirectory).Should().BeTrue(
            "the test runs from the repository's own build output, or this test proves nothing");

        var (exitCode, lines) = await NotifyCommand.RunAsync(
            provider, AppContext.BaseDirectory, Contact, CancellationToken.None);

        exitCode.Should().Be(VerifyCommand.UsageError);
        lines[0].Should().StartWith("NOT NOTIFIED").And.Contain("inside a git repository");
    }

    [Fact]
    public async Task AnUnreachableStore_IsNoVerdict_NotBroken()
    {
        await using var provider = UnreachableProvider();

        var (exitCode, lines) = await NotifyCommand.RunAsync(provider, _directory, Contact, CancellationToken.None);

        exitCode.Should().Be(VerifyCommand.Misconfigured, "an unreachable store is a fact about this invocation, never 1");
        lines[0].Should().StartWith("CANNOT NOTIFY").And.Contain("could not be read or written");
        Directory.EnumerateFiles(_directory).Should().BeEmpty();
    }

    [Fact]
    public async Task AWriteFailure_LeavesTheNoticeOwed_AndNamesTheExceptionTypeNotTheAddress()
    {
        var owner = await OwnerAsync();
        var notice = await OwedAsync(owner);
        _transport.Fails = new IOException($"disk full while writing to {Address}");
        await using var provider = Provider();

        var (exitCode, lines) = await RunAsync(provider);
        var text = string.Join("\n", lines);

        exitCode.Should().Be(AnchorCommand.NotRecorded);
        text.Should().Contain("NOT NOTIFIED").And.Contain("(IOException)").And.Contain(notice.Id.ToString("N"));
        text.Should().NotContain(Address, "an exception message can echo the recipient; only the type is printed");
        (await _context.SubscriberNotices.AsNoTracking().SingleAsync(n => n.Id == notice.Id))
            .DeliveredAt.Should().BeNull("a notice that was not written is still owed");
    }

    [Fact]
    public async Task AnInterruptedRun_SaysSo_AndBlamesNothing()
    {
        var owner = await OwnerAsync();
        var notice = await OwedAsync(owner);
        await using var provider = Provider();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var (exitCode, lines) = await NotifyCommand.RunAsync(provider, _directory, Contact, cancelled.Token);

        exitCode.Should().Be(VerifyCommand.Interrupted);
        lines[0].Should().StartWith("INTERRUPTED");
        (await _context.SubscriberNotices.AsNoTracking().SingleAsync(n => n.Id == notice.Id))
            .DeliveredAt.Should().BeNull();
    }

    [Fact]
    public async Task TheRealTransportWritesAnRfc5322File_AndRefusesToOverwriteIt()
    {
        var owner = await OwnerAsync();
        var notice = await OwedAsync(owner);
        await using var provider = Provider(new PickupDirectoryTransport());

        var (exitCode, _) = await RunAsync(provider);
        exitCode.Should().Be(VerifyCommand.Intact);

        var path = Path.Combine(_directory, $"{notice.Id:N}.eml");
        File.Exists(path).Should().BeTrue();
        var bytes = await File.ReadAllBytesAsync(path);
        var text = Encoding.UTF8.GetString(bytes);

        bytes.Take(3).Should().NotEqual(new byte[] { 0xEF, 0xBB, 0xBF }, "no BOM in front of the first header");
        text.Should().StartWith($"From: {PickupDirectoryTransport.Sender}\r\n")
            .And.Contain($"To: {Address}\r\n")
            .And.Contain($"Message-ID: <{notice.Id:N}@azurebank.invalid>\r\n")
            .And.Contain("Subject: AzureBank: a transfer PIN was set on your account\r\n")
            .And.Contain("\r\n\r\nAzureBank", "one blank line separates the headers from the body")
            .And.NotContain("\n\n", "every line ending is CRLF");
        text.Should().MatchRegex(@"Date: [A-Z][a-z]{2}, \d{2} [A-Z][a-z]{2} \d{4} \d{2}:\d{2}:\d{2} \+0000\r\n");

        var stored = await _context.SubscriberNotices.SingleAsync(n => n.Id == notice.Id);
        stored.DeliveryReceipt.Should().Be($"{notice.Id:N}.eml");
        Directory.EnumerateFiles(_directory).Select(Path.GetFileName).Should().Equal(
            [$"{notice.Id:N}.eml"], "the staging file is moved into place, never left beside the message");

        /*
          THE SECOND RUN INTO THE SAME DIRECTORY. Reset the row so the notice is owed again — the
          shape of an operator re-running after a partial failure — and the file that is already
          there must be REFUSED, not truncated: the row stays owed and the bytes stay what they were.
        */
        stored.DeliveredAt = null;
        stored.DeliveryReceipt = null;
        await _context.SaveChangesAsync();

        var (secondCode, secondLines) = await RunAsync(provider);

        secondCode.Should().Be(AnchorCommand.NotRecorded);
        string.Join("\n", secondLines).Should().Contain("NOT NOTIFIED").And.Contain("(IOException)");
        (await File.ReadAllBytesAsync(path)).Should().Equal(bytes, "the earlier copy was not touched");
        (await _context.SubscriberNotices.AsNoTracking().SingleAsync(n => n.Id == notice.Id))
            .DeliveredAt.Should().BeNull();
        Directory.EnumerateFiles(_directory).Should().HaveCount(1, "the refused run leaves no staging file either");
    }

    [Fact]
    public async Task AMissingDirectory_IsRefusedBeforeTheStoreIsTouched()
    {
        await using var provider = UnreachableProvider();
        var missing = Path.Combine(Path.GetTempPath(), "azurebank-notify-missing-" + Guid.NewGuid().ToString("N"));
        var started = DateTime.UtcNow;

        var (exitCode, lines) = await NotifyCommand.RunAsync(provider, missing, Contact, CancellationToken.None);

        exitCode.Should().Be(VerifyCommand.UsageError, "the verb never creates a directory, and says so before reading anything");
        lines[0].Should().StartWith("NOT NOTIFIED").And.Contain("not an existing directory");
        Directory.Exists(missing).Should().BeFalse();
        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(1.5));
    }

    [Fact]
    public async Task ALinkPointingIntoAGitWorkingTree_IsRefused_WhereverItSits()
    {
        /*
          The link sits under the temp directory, outside every repository; it points at this test's
          own build output, which is inside one. A lexical walk of the link's ancestors finds no
          .git, and the files would land in the working tree anyway -- on Windows git stages what is
          inside a junction as ordinary directories. Both walks are done, and this is the one that
          catches it.
        */
        var link = Path.Combine(_directory, "into-the-repo");
        var target = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        NotifyCommand.InsideAGitRepository(_directory).Should().BeFalse("the temp directory itself must sit outside any repository, or this test proves nothing");

        CreateDirectoryLink(link, target);
        Directory.Exists(link).Should().BeTrue("the link was created, or this test proves nothing");

        NotifyCommand.PhysicalPath(link).Should().BeEquivalentTo(target, "the link resolves to where it points");
        NotifyCommand.InsideAGitRepository(link).Should().BeTrue();

        await using var provider = UnreachableProvider();
        var (exitCode, lines) = await NotifyCommand.RunAsync(provider, link, Contact, CancellationToken.None);

        exitCode.Should().Be(VerifyCommand.UsageError);
        lines[0].Should().StartWith("NOT NOTIFIED").And.Contain("inside a git repository");
    }

    private static void CreateDirectoryLink(string link, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            // A junction needs no privilege, unlike a symbolic link; git stages its contents as files.
            using var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                ArgumentList = { "/c", "mklink", "/J", link, target },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            mklink!.WaitForExit();
            mklink.ExitCode.Should().Be(0, "mklink /J refused: {0}", mklink.StandardError.ReadToEnd());
            return;
        }

        Directory.CreateSymbolicLink(link, target);
    }

    [Fact]
    public async Task AnAddressWithALineBreak_IsNotUsed_AndTheNoticeStaysOwed()
    {
        /*
          RFC 5322 headers end at CRLF. An address holding one would end the To: header and start
          whatever followed -- a Bcc: to the attacker, say. Unreachable through registration,
          reachable by anybody who can write the table, which is the adversary the whole audit
          design already assumes.
        */
        var owner = await OwnerAsync(email: "owner@example.com\r\nBcc: attacker@example.com");
        var notice = await OwedAsync(owner);
        await using var provider = Provider();

        var (exitCode, lines) = await RunAsync(provider);

        exitCode.Should().Be(AnchorCommand.NotRecorded);
        _transport.Envelopes.Should().BeEmpty("nothing is rendered for an address that cannot head a message");
        lines.Should().Contain(l => l.StartsWith("NO ADDRESS") && l.Contains("line break"));
        string.Join("\n", lines).Should().NotContain("attacker");
        (await _context.SubscriberNotices.AsNoTracking().SingleAsync(n => n.Id == notice.Id))
            .DeliveredAt.Should().BeNull();
    }

    [Fact]
    public void TheTransportRefusesALineBreakInTheAddress_OnItsOwn()
    {
        // The second lock on the same door: a caller that skipped the command's check still cannot
        // inject a header through the transport.
        var rendered = new RenderedNotice("ref", "Subject", "body", "ref.eml");

        var act = () => PickupDirectoryTransport.Compose(rendered, "owner@example.com\nBcc: x@example.com", DateTime.UtcNow);

        act.Should().Throw<ArgumentException>().WithMessage("*line break*");
    }

    [Fact]
    public void TheStagingName_IsUniqueToEachCall_SoOneRunCannotDeleteAnothers()
    {
        /*
          On Unix a file another process holds open can be deleted, so two runs sharing one staging
          name would let the loser's cleanup remove the winner's half-written file. The name is what
          keeps them apart; this pins that it differs per call and still sits beside the final name.
        */
        var final = Path.Combine(_directory, "abc.eml");

        var first = PickupDirectoryTransport.StagingPathFor(final);
        var second = PickupDirectoryTransport.StagingPathFor(final);

        first.Should().NotBe(second);
        first.Should().StartWith(final + ".").And.EndWith(".partial");
        Path.GetDirectoryName(first).Should().Be(_directory, "the staging file lives beside the message, on the same volume, so the move is a rename");
    }
}
