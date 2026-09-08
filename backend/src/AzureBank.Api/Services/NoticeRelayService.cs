using AzureBank.Infrastructure.Data;
using AzureBank.Infrastructure.Notices;
using AzureBank.Shared.Options;
using Microsoft.Extensions.Options;

namespace AzureBank.Api.Services;

/// <summary>
/// The API's runner: on a period, it runs one <see cref="NoticeSweep"/> — claiming owed
/// <c>SubscriberNotices</c> rows under a lease and delivering each through the registered
/// <see cref="INoticeTransport"/> (ADR-0048). The API's first correctness-bearing loop, and it says
/// so.
/// </summary>
/// <remarks>
/// <para>
/// WHAT RUNS AND WHAT DOES NOT. Always registered, so a second host that calls the same extension
/// inherits it; it reads <see cref="NoticeRelayOptions.Runner"/> once at start and, unless that is
/// <see cref="NoticeRunner.Api"/>, says so and returns. Since ADR-0051 that is an Information line
/// for every other value including <see cref="NoticeRunner.Function"/>, which now names a runner
/// this repository ships: an operator who set it is told this process is not it, not warned that
/// nothing is. The flag, not the lease, is what keeps two KINDS of runner from both sending; two
/// hosts of this API with the flag set both run the loop, and the lease keeps them off each other's
/// rows.
/// </para>
/// <para>
/// THE SWEEP IS NOT HERE. It is <see cref="NoticeSweep"/> in Infrastructure, shared with the
/// Function (ADR-0051 D1) for the reason ADR-0048 D4 gave one level down: two runners that
/// reimplemented the claim-and-deliver order would be two protocols wearing one name. What is here
/// is what belongs to THIS host — the flag, the schedule, the scope per sweep, and the line that
/// says the loop is live.
/// </para>
/// <para>
/// THE HOUSE SHAPE for a loop (the two hygiene sweeps): <see cref="PeriodicTimer"/>, so the first
/// look is one full period after start and a fast test host is never interrupted; a catch-all per
/// tick logged at Error so one bad sweep never ends the loop; cancellation rethrown past it and
/// absorbed at the outer level as shutdown.
/// </para>
/// <para>
/// Clock: <see cref="TimeProvider"/>, defaulting to the system one as <c>AuditService</c> does, so
/// a test can lapse a lease by advancing time rather than by rewriting the row.
/// </para>
/// </remarks>
public sealed class NoticeRelayService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly NoticeRelayOptions _options;
    private readonly ILogger<NoticeRelayService> _logger;
    private readonly TimeProvider _clock;
    private readonly string? _directory;

    /// <summary>
    /// <c>api/{host}/{pid}/{8 hex}</c>: distinguishes two runners on one machine, and is a name
    /// for the log, never a secret. Bounded to the column width by <see cref="NoticeClaim.RunnerNameFor"/>.
    /// </summary>
    public string RunnerName { get; }

    public NoticeRelayService(
        IServiceScopeFactory scopes,
        IOptions<NoticeRelayOptions> options,
        ILogger<NoticeRelayService> logger,
        TimeProvider? clock = null)
    {
        _scopes = scopes;
        _options = options.Value;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        RunnerName = NoticeClaim.RunnerNameFor(
            NoticeClaim.ApiKind, Environment.MachineName, Environment.ProcessId, Guid.NewGuid());

        // Validated at start by the registration; resolved once for the line that announces the
        // loop, and only when this process is the runner — nothing else may hand the sweep a path.
        _directory = _options.Runner == NoticeRunner.Api && !string.IsNullOrWhiteSpace(_options.PickupDirectory)
            ? Path.GetFullPath(_options.PickupDirectory)
            : null;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.Runner != NoticeRunner.Api || _directory is null)
        {
            _logger.LogInformation(
                "Notice relay: runner is {Runner}; this process delivers nothing (Notices:Runner)",
                _options.Runner);
            return;
        }

        _logger.LogInformation(
            "Notice relay: live as {RunnerName}, every {PeriodSeconds}s, lease {LeaseSeconds}s, batch {BatchSize}, into {Directory}",
            RunnerName, _options.PeriodSeconds, _options.LeaseSeconds, _options.BatchSize, _directory);

        using var timer = new PeriodicTimer(_options.Period, _clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await SweepAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Notice relay: sweep failed; will retry next period");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutdown.
        }
    }

    /// <summary>
    /// One sweep in a scope of this host's own. Internal so a test drives it without waiting a
    /// period; the work itself is <see cref="NoticeSweep"/>'s, and the scope is what this host adds.
    /// </summary>
    /// <returns>How many rows this sweep claimed, delivered, and left owed after a named failure.</returns>
    internal async Task<NoticeSweepSummary> SweepAsync(CancellationToken cancellationToken)
    {
        if (_directory is null)
        {
            throw new InvalidOperationException("The relay has no pickup directory: Notices:Runner is not Api.");
        }

        using var scope = _scopes.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var transport = scope.ServiceProvider.GetRequiredService<INoticeTransport>();

        var sweep = new NoticeSweep(context, transport, _options, _logger, _clock);
        return await sweep.RunAsync(RunnerName, cancellationToken);
    }
}
