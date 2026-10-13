using System.Globalization;
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
/// What the verifier prints is the same text under every culture: a count or a sequence has its
/// digits in groups of three with a comma between the groups.
/// </summary>
/// <remarks>
/// de-DE groups with a full stop and fr-FR with a narrow no-break space; the empty name is the
/// invariant culture. The culture is set on the test's own flow and the machine's is put back. A
/// number below one thousand is written the same under every culture, so the lines that only carry
/// an anchor's own sequence are held by <c>AuditReportNumberFormattingTests</c>, which reads the
/// sources.
/// </remarks>
public class VerifierReportsUnderAHostCultureTests : IDisposable
{
    private const string ChainKey = "culture-tests-chain-key-0123456789abcdefghij";
    private const string AnchorKey = "culture-tests-anchor-key-9876543210zyxwvuts";

    private readonly ServiceProvider _services;
    private readonly AzureBankDbContext _context;
    private readonly string _directory;

    public VerifierReportsUnderAHostCultureTests()
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

        _directory = Path.Combine(Path.GetTempPath(), "azurebank-culture-" + Guid.NewGuid().ToString("N"));
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
            // A temporary directory left behind is not worth failing a green run over.
        }

        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    [InlineData("")]
    public void AnIntactVerdictWithRowsOutsideEveryAnchor_ReadsTheSameUnderEveryCulture(string culture)
    {
        var lines = Under(culture, () => VerifyCommand.Report(
            new AuditChainVerification(12345, null, null),
            lowest: 1,
            highest: 12345,
            new VerifyCommand.AnchorCoverage(ChainVerified: true, DeepestCovered: 11000, Records: 3)).Lines);

        lines.Should().Contain("CHAIN INTACT: 12,345 rows verified.")
            .And.Contain("  Sequence range: 1 to 12,345")
            .And.Contain("UNCOVERED WINDOW: at least 1,345 rows are outside every anchor.")
            .And.Contain("  The deepest coverage any record claims is sequence 11,000; the chain ends at 12,345.");
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    [InlineData("")]
    public void AnIntactVerdictOverAnchorsThatReachPastTheTail_ReadsTheSameUnderEveryCulture(string culture)
    {
        var lines = Under(culture, () => VerifyCommand.Report(
            new AuditChainVerification(12345, null, null),
            lowest: 1,
            highest: 12345,
            new VerifyCommand.AnchorCoverage(ChainVerified: true, DeepestCovered: 23456, Records: 3)).Lines);

        lines.Should().Contain(
                "⚠️ UNCOVERED WINDOW: NEGATIVE. The anchors claim coverage through sequence 23,456,"
                + " and the chain ends at 12,345.")
            .And.Contain("  11,111 sequences that an anchor says it saw are not there now. That");
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    [InlineData("")]
    public void AnIntactVerdictOverGapMarkersOnly_ReadsTheSameUnderEveryCulture(string culture)
    {
        var lines = Under(culture, () => VerifyCommand.Report(
            new AuditChainVerification(12345, null, null),
            lowest: 1,
            highest: 12345,
            new VerifyCommand.AnchorCoverage(ChainVerified: true, DeepestCovered: null, Records: 1234)).Lines);

        lines.Should().Contain("UNCOVERED WINDOW: EVERY row. 1,234 anchor records exist and none");
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    [InlineData("")]
    public void ABrokenVerdict_ReadsTheSameUnderEveryCulture(string culture)
    {
        var lines = Under(culture, () => VerifyCommand.Report(
            new AuditChainVerification(
                11344, 12345, "The row does not hash.", Kind: AuditChainBreakKind.HashMismatch),
            lowest: 1001,
            highest: 12344).Lines);

        lines.Should().Contain("CHAIN BROKEN at sequence 12,345.")
            .And.Contain("  Rows verified before the break: 11,344")
            .And.Contain("  Sequences read: 1,001 to 12,344");
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    [InlineData("")]
    public async Task AnAnchorAndItsExport_ReadTheSameUnderEveryCulture(string culture)
    {
        await WriteRowsAsync(1001);
        var path = Path.Combine(_directory, "anchors.jsonl");

        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            var (_, anchored) = await AnchorCommand.RunAsync(_services, CancellationToken.None);
            var (_, exported) = await ExportCommand.RunAsync(_services, path, CancellationToken.None);

            anchored.Should().Contain("  Covers sequences 1 to 1,001, 1,001 rows.")
                .And.Contain("      anchor 1  through sequence 1,001");
            exported.Should().Contain("  Covering through sequence 1,001.");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private async Task WriteRowsAsync(int count)
    {
        for (var i = 0; i < count; i++)
        {
            _context.AuditEvents.Add(new AuditEvent
            {
                Id = Guid.CreateVersion7(),
                OccurredAt = DateTime.UtcNow,
                Event = $"CultureEvent{i}",
                Outcome = AuditOutcome.Succeeded,
                ActorUserId = Guid.NewGuid(),
                RowHash = string.Empty,
            });
            await _context.SaveChangesAsync();
        }
    }

    private static IReadOnlyList<string> Under(string culture, Func<IReadOnlyList<string>> report)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            return report();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
