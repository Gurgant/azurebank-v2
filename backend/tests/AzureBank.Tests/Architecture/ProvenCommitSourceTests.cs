using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace AzureBank.Tests.Architecture;

/// <summary>
/// What the 409 <c>IDEMPOTENCY_RESULT_UNKNOWN</c>'s <c>applied: true</c> stands on, held at the
/// source (ADR-0009).
/// </summary>
/// <remarks>
/// <para>
/// The answer says an operation was applied because the key's record was read from the database as
/// <c>Executed</c>. That is proof only while one rule holds: the middleware sets <c>Executed</c> on
/// the tracked record in memory, and it reaches the database with the NEXT save of the request's
/// context, which must be the save that moves the money. A save added on that context between the
/// flip and the business commit would write <c>Executed</c> with no money moved, and the API would
/// answer <c>applied: true</c> for it.
/// </para>
/// <para>
/// No compiler and no behaviour test sees such a save being added: it compiles, every existing test
/// stays green, and the first evidence would be a visitor told that a payment went through which
/// did not. So the save sites of the files on the money path are counted here. A changed count is
/// not a defect by itself; it is the moment to read the new save against the rule above, and then
/// to move the number.
/// </para>
/// <para>
/// A source scan in the <see cref="LedgerClockHygieneTests"/> shape. The SQL Server tests hold the
/// behaviour: a refusal after the flip was written leaves no <c>Executed</c> record, and a commit
/// refused as it starts is run again.
/// </para>
/// </remarks>
public class ProvenCommitSourceTests
{
    private const string Services = "backend/src/AzureBank.Api/Services/Implementations/";

    private static readonly Regex SaveCall = new(@"SaveChanges(Async)?\(", RegexOptions.CultureInvariant);

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

    private static string Read(string relativePath)
    {
        var path = Path.Combine(RepoRoot().FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(path).Should().BeTrue(because: $"the guard scans {relativePath}; a moved file must move this line too");

        var text = File.ReadAllText(path);

        // Liveness: a scan of an empty or truncated file reports clean forever.
        text.Should().Contain(
            $"class {Path.GetFileNameWithoutExtension(path)}",
            "the file scanned must be the real one, holding the service it is named after");
        return text;
    }

    /// <remarks>
    /// The counts, and what each save is:
    /// <list type="bullet">
    /// <item><c>TransactionService</c> 2: the deposit's one save, and the withdrawal's, inside its transaction.</item>
    /// <item><c>TransferService</c> 4: the ledger rows and the back-link, for each of the two transfers, inside their transactions.</item>
    /// <item><c>StepUpAuthorizationService</c> 2: the mint's, in a request that holds no idempotency
    /// record, and the InMemory arm of the consume; on SQL Server the consume is a set-based update
    /// inside the caller's transaction.</item>
    /// <item><c>AuditService</c> 1: a refusal's row, on a context of its own scope.</item>
    /// <item><c>DailyOutflowLimitService</c> and <c>AccountAccessService</c> 0: they read.</item>
    /// </list>
    /// </remarks>
    [Theory]
    [InlineData("TransactionService.cs", 2)]
    [InlineData("TransferService.cs", 4)]
    [InlineData("StepUpAuthorizationService.cs", 2)]
    [InlineData("AuditService.cs", 1)]
    [InlineData("DailyOutflowLimitService.cs", 0)]
    [InlineData("AccountAccessService.cs", 0)]
    public void TheMoneyPath_SavesOnlyWhereItWasReadToSave(string file, int expected)
    {
        var saves = SaveCall.Matches(Read(Services + file)).Count;

        saves.Should().Be(
            expected,
            "{0} is on the path of a keyed money request. A save on the request's context between the "
            + "middleware's flip to Executed and the business commit writes Executed with no money "
            + "moved, and the API would answer applied: true for it. Read every save in this file "
            + "against that rule (ADR-0009), then change this number",
            file);
    }

    private static List<string> SourceFilesCalling(Regex call)
    {
        var root = Path.Combine(RepoRoot().FullName, "backend", "src");
        var all = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();

        // Liveness: a walk that found no sources would report "no callers" forever.
        all.Count.Should().BeGreaterThan(100, "the walk must have read the API's sources");

        return all
            .Where(path => call.IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetFileName(path))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
    }

    [Fact]
    public void ResultUnknown_IsAnsweredFromTheClaimAndTheReRunOnly()
    {
        // The two places that read the key's record from the database before they answer: a later
        // request's claim, and a re-run attempt's reload. Whatever this code comes to say about the
        // outcome, it can only be said where the record was just read.
        var callers = SourceFilesCalling(
            new Regex(@"IdempotencyException\.ResultUnknown\w*\(", RegexOptions.CultureInvariant));

        callers.Should().Equal(
            new[] { "ConcurrencyRetry.cs", "IdempotencyService.cs" },
            "a third place answering IDEMPOTENCY_RESULT_UNKNOWN must show which database read it stands on");
    }

    [Fact]
    public void TheAnswerThatSaysApplied_IsThrownFromExactlyTwoFiles()
    {
        // applied: true is said from a database read of the key's record, made by the request that
        // answers: the claim's untracked read, and the re-run's reload. Nowhere else has one.
        var callers = SourceFilesCalling(
            new Regex(@"IdempotencyException\.ResultUnknownApplied\(", RegexOptions.CultureInvariant));

        callers.Should().Equal(
            new[] { "ConcurrencyRetry.cs", "IdempotencyService.cs" },
            "the answer that says a payment went through is given only where the record was just read as committed");
    }
}
