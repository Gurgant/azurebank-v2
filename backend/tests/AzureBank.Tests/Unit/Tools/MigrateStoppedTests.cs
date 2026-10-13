extern alias seeder;

using AzureBank.Tests.Fixtures;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using GateResult = seeder::AzureBank.Seeder.Commands.GateResult;
using MigrateCommand = seeder::AzureBank.Seeder.Commands.MigrateCommand;

namespace AzureBank.Tests.Unit.Tools;

/// <summary>
/// <c>migrate</c> stopped while it runs: exit 1, one warning that says to run it again, and no
/// error line.
/// </summary>
/// <remarks>
/// <para>
/// A deployment stops a one-shot with SIGTERM. The command then has to say that it was stopped,
/// and not that it failed: <c>migrate failed: …</c> on a run somebody stopped sends an operator to
/// look for a fault in the database. What decides between the two is the TOKEN, not the type of
/// the exception: SqlClient reports a command cancelled in flight as a <c>SqlException</c>
/// (the command's own comment, measured 2026-10-01), so a filter on
/// <c>OperationCanceledException</c> would call that a failure.
/// </para>
/// <para>
/// The stop is given through the wait the command accepts as a parameter: it cancels the run's
/// token and then throws. Nothing is opened, so no server is needed.
/// </para>
/// </remarks>
public sealed class MigrateStoppedTests
{
    private const string ConnectionKey = "ConnectionStrings:DefaultConnection";

    // Port 1 on loopback: nothing listens there, and nothing leaves the machine.
    private const string AbsentServer =
        "Server=127.0.0.1,1;Database=x;User Id=u;Password=not-a-secret;Connect Timeout=1";

    public static TheoryData<string, Exception> WhatTheStopLooksLike() => new()
    {
        { "the cancellation itself", new OperationCanceledException() },
        { "a failure that is not a cancellation by type", new InvalidOperationException("A severe error occurred on the current command.") },
    };

    [Theory]
    [MemberData(nameof(WhatTheStopLooksLike))]
    public async Task StoppedWhileItWaits_ItExitsOne_SaysItWasCancelled_AndLogsNoError(string seenAs, Exception thrown)
    {
        var log = new RecordingLoggerProvider();
        var opens = new OpenCountingInterceptor();
        await using var provider = SeederHost.Build(
            log, opens, onCommittedSettings: false, (ConnectionKey, AbsentServer), ("Database:MaxRetryCount", "0"));
        using var stopping = new CancellationTokenSource();

        async Task<GateResult> StopTheRun(CancellationToken token)
        {
            await stopping.CancelAsync();
            throw thrown;
        }

        var exitCode = await MigrateCommand.RunAsync(provider, TimeSpan.Zero, stopping.Token, StopTheRun);

        var warnings = log.Lines.Where(line => line.Level == LogLevel.Warning).Select(line => line.Message);
        var errors = log.Lines.Where(line => line.Level >= LogLevel.Error).Select(line => line.Message);
        using var all = new AssertionScope();
        exitCode.Should().Be(1, $"a run that did not finish is not a success ({seenAs})");
        warnings.Should().ContainSingle()
            .Which.Should().Be(
                "migrate was cancelled before it finished. Run it again: only migrations the history "
                + "table does not list are applied.");
        errors.Should().BeEmpty("being stopped is not a failure of the run's own");
        opens.Opens.Should().Be(0);
    }

    [Fact]
    public async Task TheSameFailure_OnARunNobodyStopped_IsAFailure_AndSaysSo()
    {
        // CONTROL: without the cancelled token the same exception is reported as what it is.
        var log = new RecordingLoggerProvider();
        await using var provider = SeederHost.Build(
            log, interceptor: null, onCommittedSettings: false, (ConnectionKey, AbsentServer), ("Database:MaxRetryCount", "0"));

        var exitCode = await MigrateCommand.RunAsync(
            provider,
            TimeSpan.Zero,
            CancellationToken.None,
            _ => throw new InvalidOperationException("A severe error occurred on the current command."));

        var errors = log.Lines.Where(line => line.Level >= LogLevel.Error).Select(line => line.Message).ToList();
        using var all = new AssertionScope();
        exitCode.Should().Be(1);
        errors.Should().ContainSingle()
            .Which.Should().StartWith("migrate failed: A severe error occurred on the current command.");
        log.Lines.Should().NotContain(line => line.Message.Contains("was cancelled"));
    }
}
