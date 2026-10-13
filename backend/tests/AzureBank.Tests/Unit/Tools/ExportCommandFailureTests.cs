using AzureBank.AuditVerifier.Commands;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Shared.Options;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AzureBank.Tests.Unit.Tools;

/// <summary>
/// What <c>export</c> answers when something goes wrong AFTER its checks of the path and of the
/// key have passed: a file that arrives at the path while the chain is being read, a run that is
/// stopped, and a store that cannot be read.
/// </summary>
/// <remarks>
/// <para>
/// THE SEAM IS THE ANCHOR CHAIN. The command reads the records, then asks
/// <see cref="IAuditAnchorChain.VerifyChainAsync"/>, then creates the file. A chain that does
/// something at that call and then answers as the real one does puts an event exactly between
/// the check of the path and the write, on every run and on every platform, with no race.
/// <c>ExportCommandTests</c> says of the collision at the create that it "needs a genuine race,
/// which no seam here can produce"; this is the seam.
/// </para>
/// <para>
/// Every test writes under its own temporary directory and deletes it, as <c>ExportCommandTests</c>
/// does.
/// </para>
/// </remarks>
public sealed class ExportCommandFailureTests : IDisposable
{
    private const string ChainKey = "export-failure-tests-chain-key-0123456789ab";
    private const string AnchorKey = "export-failure-tests-anchor-key-9876543210xy";
    private const string Sentinel = "{\"anchorSequence\":1,\"this\":\"is the copy of another run\"}\n";

    private readonly string _store = Guid.NewGuid().ToString();
    private readonly string _directory;
    private readonly AzureBankDbContext _context;
    private readonly IOptions<AuditOptions> _options =
        Options.Create(new AuditOptions { ChainKey = ChainKey, AnchorKey = AnchorKey });

    public ExportCommandFailureTests()
    {
        _context = Context(_options);
        _directory = Path.Combine(Path.GetTempPath(), "azurebank-export-failure-" + Guid.NewGuid().ToString("N"));
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

    private AzureBankDbContext Context(IOptions<AuditOptions> options) =>
        new(
            new DbContextOptionsBuilder<AzureBankDbContext>().UseInMemoryDatabase(_store).Options,
            timeProvider: null,
            auditChain: new AuditChain(options, NullLogger<AuditChain>.Instance));

    /// <summary>
    /// A provider over the store, whose anchor chain runs <paramref name="whileTheChainIsRead"/>
    /// and then answers as the real one does under <paramref name="options"/>.
    /// </summary>
    private ServiceProvider Provider(Func<Task> whileTheChainIsRead, IOptions<AuditOptions>? options = null)
    {
        options ??= _options;
        var collection = new ServiceCollection();
        collection.AddSingleton(options);
        collection.AddSingleton<IAuditChain>(new AuditChain(options, NullLogger<AuditChain>.Instance));
        collection.AddSingleton<IAuditAnchorChain>(
            new ActsWhileTheChainIsRead(new AuditAnchorChain(options), whileTheChainIsRead));
        collection.AddSingleton(Context(options));
        return collection.BuildServiceProvider();
    }

    /// <summary>Three audit rows and one anchor over them, written under the keys of this class.</summary>
    private async Task AnAnchoredChainAsync()
    {
        for (var i = 0; i < 3; i++)
        {
            _context.AuditEvents.Add(new AuditEvent
            {
                Id = Guid.CreateVersion7(),
                OccurredAt = DateTime.UtcNow,
                Event = $"ExportFailureEvent{i}",
                Outcome = AuditOutcome.Succeeded,
                ActorUserId = Guid.NewGuid(),
                RowHash = string.Empty,
            });
            await _context.SaveChangesAsync();
        }

        await using var honest = Provider(() => Task.CompletedTask);
        var (exitCode, _) = await AnchorCommand.RunAsync(honest, CancellationToken.None);
        exitCode.Should().Be(VerifyCommand.Intact, "ARRANGE: one anchor over three rows");
    }

    [Fact]
    public async Task AFileThatArrivesWhileTheChainIsRead_IsKept_AndTheRunSaysItWroteNothing()
    {
        // The check of the path has a window, and the write is what closes it: the file is created
        // with FileMode.CreateNew, which refuses where FileMode.Create would truncate. Here the
        // path is free at the check and taken at the write.
        // Measured 2026-10-12: exit 6, and the other file is byte for byte what it was.
        await AnAnchoredChainAsync();
        var path = Path.Combine(_directory, "arrives-during-the-read.jsonl");
        await using var provider = Provider(() => File.WriteAllTextAsync(path, Sentinel));

        var (exitCode, lines) = await ExportCommand.RunAsync(provider, path, CancellationToken.None);

        using var all = new AssertionScope();
        exitCode.Should().Be(
            AnchorCommand.NotRecorded, "the chain was read and no file came of this run: neither 0 nor 3");
        lines[0].Should().Be($"NOT EXPORTED: {path} appeared while this run was reading the chain.");
        string.Join("\n", lines).Should().NotContain("EXPORTED 1 anchor");
        ComparableText.Of(await File.ReadAllTextAsync(path)).Should().Be(
            ComparableText.Of(Sentinel), "an earlier copy is never at risk from this verb, whoever wrote it");
    }

    [Fact]
    public async Task AFileThatArrivesWhileABROKENChainIsRead_IsKept_AndTheVerdictIsStillBroken()
    {
        // The same collision under a provider that holds another anchor key, so the walk ends
        // "not intact". A refused write must not turn that verdict into exit 6.
        // Measured 2026-10-12: exit 1, "CHAIN BROKEN" first, the refusal after it.
        await AnAnchoredChainAsync();
        var path = Path.Combine(_directory, "arrives-during-a-broken-read.jsonl");
        var stranger = Options.Create(new AuditOptions
        {
            ChainKey = ChainKey,
            AnchorKey = "a-completely-different-anchor-key-0123456789",
        });
        await using var provider = Provider(() => File.WriteAllTextAsync(path, Sentinel), stranger);

        var (exitCode, lines) = await ExportCommand.RunAsync(provider, path, CancellationToken.None);
        var text = string.Join("\n", lines);

        using var all = new AssertionScope();
        exitCode.Should().Be(VerifyCommand.Broken, "the verdict about the chain is still the verdict");
        lines[0].Should().Be("CHAIN BROKEN -- and separately, no copy was written.");
        text.Should().Contain("UnknownScheme at anchor 1");
        text.Should().Contain("appeared while this run was reading the chain");
        ComparableText.Of(await File.ReadAllTextAsync(path)).Should().Be(ComparableText.Of(Sentinel));
    }

    [Fact]
    public async Task AnExportStoppedWhileTheChainIsRead_IsInterrupted_AndLeavesNoFile()
    {
        // Decided by the token and not by the type of the exception, as in `anchor`: a walk
        // cancelled in flight on SQL Server faults with a SqlException that names no cancellation.
        // So the chain here signals the token and then throws something else.
        // Measured 2026-10-12: exit 5.
        await AnAnchoredChainAsync();
        var path = Path.Combine(_directory, "stopped.jsonl");
        using var stop = new CancellationTokenSource();
        await using var provider = Provider(async () =>
        {
            await stop.CancelAsync();
            throw new InvalidOperationException("stand-in for a faulted in-flight cancellation");
        });

        var (exitCode, lines) = await ExportCommand.RunAsync(provider, path, stop.Token);

        using var all = new AssertionScope();
        exitCode.Should().Be(VerifyCommand.Interrupted);
        exitCode.Should().NotBe(VerifyCommand.Misconfigured, "the store is fine: the operator stopped the run");
        lines[0].Should().Be("INTERRUPTED: the export was stopped.");
        File.Exists(path).Should().BeFalse("the run was stopped before the file was created");
    }

    [Fact]
    public async Task AStoreThatFailsWhileTheChainIsRead_IsNoVerdict_AndLeavesNoFile()
    {
        // Not a usage error and not a verdict: what `verify` and `anchor` say of the same outage.
        // Measured 2026-10-12: exit 3, the type and the message of the failure on the second line.
        await AnAnchoredChainAsync();
        var path = Path.Combine(_directory, "unread.jsonl");
        await using var provider = Provider(
            () => throw new InvalidOperationException("the store went away under the walk"));

        var (exitCode, lines) = await ExportCommand.RunAsync(provider, path, CancellationToken.None);

        using var all = new AssertionScope();
        exitCode.Should().Be(VerifyCommand.Misconfigured);
        exitCode.Should().NotBe(VerifyCommand.UsageError, "nothing was wrong with the command line");
        lines[0].Should().Be("NO VERDICT: the audit store could not be read, so nothing was exported.");
        lines[1].Should().Be("  InvalidOperationException: the store went away under the walk");
        string.Join("\n", lines).Should().Contain("NOT a statement about the chain");
        File.Exists(path).Should().BeFalse("nothing is written from a chain that was not read to its end");
    }

    /// <summary>
    /// The real anchor chain, with one thing done first each time the whole chain is verified.
    /// </summary>
    private sealed class ActsWhileTheChainIsRead(IAuditAnchorChain inner, Func<Task> first) : IAuditAnchorChain
    {
        public Task<AuditAnchor?> ReadTailAsync(DbContext context, CancellationToken cancellationToken = default) =>
            inner.ReadTailAsync(context, cancellationToken);

        public AuditAnchorCheck Check(AuditAnchor anchor) => inner.Check(anchor);

        public AuditAnchor Build(AuditChainVerification verification, AuditAnchor? tail, DateTime createdAtUtc) =>
            inner.Build(verification, tail, createdAtUtc);

        public async Task<AuditAnchorChainVerification> VerifyChainAsync(
            DbContext context, CancellationToken cancellationToken = default)
        {
            await first();
            return await inner.VerifyChainAsync(context, cancellationToken);
        }
    }
}
