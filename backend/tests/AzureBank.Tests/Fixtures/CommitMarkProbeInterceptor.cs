using System.Collections.Concurrent;
using System.Data.Common;
using AzureBank.Api.Data;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AzureBank.Tests.Fixtures;

/// <summary>
/// Records, while armed, how each save of a request's own context reached the database and
/// whether the request's commit mark (<c>IRequestDeadline.CommitEntered</c>) was set by then.
/// </summary>
/// <remarks>
/// <para>
/// A save that writes rows lands in one of two ways. Inside a transaction it lands at that
/// transaction's commit, which the commit gate marks before it starts: the probe records each
/// commit that landed with the mark as it stood then. Outside one, a single statement commits by
/// itself and passes no gate: the probe records that save as one that returned with rows written,
/// no transaction open and the mark as it stood.
/// </para>
/// <para>
/// Only a context that finds a request deadline in its scope is recorded: the contexts of a
/// background job and of a fresh scope are not gated and are not a request's own.
/// </para>
/// </remarks>
public sealed class CommitMarkProbeInterceptor : SaveChangesInterceptor, IDbTransactionInterceptor
{
    private readonly ConcurrentQueue<bool> _commits = new();
    private readonly ConcurrentQueue<bool> _savesOutsideATransaction = new();
    private int _armed;

    /// <summary>Starts recording.</summary>
    public void Arm() => Volatile.Write(ref _armed, 1);

    /// <summary>Stops recording.</summary>
    public void Disarm() => Volatile.Write(ref _armed, 0);

    /// <summary>One entry per commit that landed: whether the mark was set.</summary>
    public IReadOnlyCollection<bool> Commits => _commits;

    /// <summary>
    /// One entry per save that wrote rows with no transaction open when it returned: whether the
    /// mark was set.
    /// </summary>
    public IReadOnlyCollection<bool> SavesOutsideATransaction => _savesOutsideATransaction;

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _armed) == 1
            && result > 0
            && eventData.Context is { Database.CurrentTransaction: null } context
            && CommitGateInterceptor.DeadlineOf(context) is { } deadline)
        {
            _savesOutsideATransaction.Enqueue(deadline.CommitEntered);
        }

        return base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public Task TransactionCommittedAsync(
        DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _armed) == 1 && CommitGateInterceptor.DeadlineOf(eventData.Context) is { } deadline)
        {
            _commits.Enqueue(deadline.CommitEntered);
        }

        return Task.CompletedTask;
    }
}
