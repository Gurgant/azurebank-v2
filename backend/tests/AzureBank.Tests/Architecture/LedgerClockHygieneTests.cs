using System.Reflection;
using FluentAssertions;
using Xunit;

namespace AzureBank.Tests.Architecture;

/// <summary>
/// The day's window and the ledger stamp come from ONE clock (ADR-0050): neither the helper that
/// sums the day nor the service that writes the rows may read <c>DateTime.UtcNow</c>.
/// </summary>
/// <remarks>
/// A source scan in the <see cref="SourceHygieneTests"/> shape, because the defect is invisible to
/// the compiler: a <c>DateTime.UtcNow</c> in either file compiles, runs, and is right to the
/// microsecond every day except at midnight — where a row stamped by the context's
/// <see cref="TimeProvider"/> and a window computed from the system clock can disagree about which
/// day it belongs to, and no FakeTimeProvider test can see it because the fake only controls the
/// half it was handed. <c>TransferService</c> already had none for <c>CreatedAt</c> (its comment
/// at the `No now here` block says why); this keeps it so.
/// </remarks>
public class LedgerClockHygieneTests
{
    private static DirectoryInfo RepoRoot()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".github")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull(because: "the scan needs the sources; a guard that cannot run must fail loudly");
        return dir!;
    }

    [Theory]
    [InlineData("backend/src/AzureBank.Api/Services/Implementations/DailyOutflowLimitService.cs")]
    [InlineData("backend/src/AzureBank.Api/Services/Implementations/TransferService.cs")]
    public void TheDaysWindowAndTheLedgerStamp_ReadNoSecondClock(string relativePath)
    {
        var path = Path.Combine(RepoRoot().FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(path).Should().BeTrue(because: $"the guard scans {relativePath}; a moved file must move this line too");

        var lines = File.ReadAllLines(path);

        // Liveness: a scan of an empty or truncated file reports clean forever.
        lines.Length.Should().BeGreaterThan(50, "the file scanned must be the real one");

        var offenders = lines
            .Select((text, index) => (Text: text, Line: index + 1))
            .Where(l => l.Text.Contains("DateTime.UtcNow", StringComparison.Ordinal)
                        || l.Text.Contains("DateTimeOffset.UtcNow", StringComparison.Ordinal)
                        || l.Text.Contains("DateTime.Now", StringComparison.Ordinal))
            .Select(l => $"{relativePath}:{l.Line}  {l.Text.Trim()}")
            .ToList();

        offenders.Should().BeEmpty(
            "the window start and CreatedAt must come from the one registered TimeProvider, or the "
            + "day boundary is decided by two clocks and the FakeTimeProvider tests prove nothing");
    }
}
