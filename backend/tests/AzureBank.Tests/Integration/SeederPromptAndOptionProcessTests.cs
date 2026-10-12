extern alias seeder;

using AzureBank.Tests.Fixtures;
using FluentAssertions;
using FluentAssertions.Execution;
using Xunit;
using MigrateCommand = seeder::AzureBank.Seeder.Commands.MigrateCommand;

namespace AzureBank.Tests.Integration;

/// <summary>
/// Two things the Seeder decides from its command line, as the process a job runs
/// (<see cref="SeederProcess"/>): <c>reset</c> without <c>--confirm</c>, and <c>migrate</c> with a
/// wait it does not accept.
/// </summary>
/// <remarks>
/// <para>
/// <c>reset</c> DELETES THE DATABASE. Without <c>--confirm</c> it asks, and under a job runner
/// nobody answers: the child's input is closed, as it is there, so the question reads
/// end-of-input. Every other test of <c>reset</c> passes <c>confirm: true</c>, so the branch that
/// stands between a forgotten flag and a deleted database was run by none.
/// </para>
/// <para>
/// The connection string names a server that is not there (port 1 on loopback) with one second to
/// connect and no retry. A <c>reset</c> that went on past the question fails there, with exit 1
/// and "reset failed"; one that stops at the question never reaches for it.
/// </para>
/// </remarks>
public sealed class SeederPromptAndOptionProcessTests
{
    private const string AbsentServer =
        "Server=127.0.0.1,1;Database=x;User Id=u;Password=not-a-secret;Connect Timeout=1";

    private static readonly (string Name, string Value)[] ALocalDatabaseThatIsNotThere =
    [
        ("ConnectionStrings__DefaultConnection", AbsentServer),
        ("Security__PinPepper", SeederHost.Pepper),
        ("Database__MaxRetryCount", "0"),
    ];

    [Fact]
    public async Task Reset_WithoutConfirm_AndNobodyToAnswer_DeletesNothing_AndExitsZero()
    {
        // Measured 2026-10-12: exit 0, the warning, the question, "Operation cancelled.", and not
        // a line about the database.
        var (output, exitCode) = await SeederProcess.Run(["reset"], ALocalDatabaseThatIsNotThere);

        using var all = new AssertionScope();
        exitCode.Should().Be(0, "a question nobody answered is declined: nothing was done and nothing failed. "
            + SeederProcess.Shown(output));
        output.Should().Contain("WARNING: This will DELETE all data in the database!");
        output.Should().Contain("Are you sure you want to continue? (y/N): ");
        output.Should().Contain("Operation cancelled.");
        output.Should().NotContain("Deleting database");
        output.Should().NotContain("reset failed", "the server was never reached for");
    }

    [Fact]
    public async Task Reset_WithConfirm_OnTheSameSettings_DoesReachForTheDatabase()
    {
        // The other side of the test above, on the same settings: with the flag the command goes
        // on, so the silence there is the question's doing and not a refusal further up.
        // Measured 2026-10-12: exit 1 and "reset failed".
        var (output, exitCode) = await SeederProcess.Run(["reset", "--confirm"], ALocalDatabaseThatIsNotThere);

        using var all = new AssertionScope();
        exitCode.Should().Be(1, SeederProcess.Shown(output));
        output.Should().NotContain("Are you sure you want to continue?", "--confirm is the answer");
        output.Should().Contain("reset failed");
    }

    [Theory]
    [InlineData("one second under the range")]
    [InlineData("one second over the range")]
    public async Task Migrate_WithAWaitOutsideItsRange_IsRefusedOnTheCommandLine_AndWaitsForNothing(string wait)
    {
        // Measured 2026-10-12 with -1 and 601: exit 1 and the sentence of the option's own check,
        // in under a second. No wait began.
        var seconds = wait == "one second under the range"
            ? "-1"
            : (MigrateCommand.LongestWaitSeconds + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var started = DateTime.UtcNow;

        var (output, exitCode) = await SeederProcess.Run(
            ["migrate", "--wait-seconds", seconds], ALocalDatabaseThatIsNotThere);

        using var all = new AssertionScope();
        exitCode.Should().Be(1, SeederProcess.Shown(output));
        output.Should().Contain($"--wait-seconds must be between 0 and {MigrateCommand.LongestWaitSeconds}.");
        output.Should().NotContain("did not accept a connection", "the database was not waited for");
        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(30));
    }
}
