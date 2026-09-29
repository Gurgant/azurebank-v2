using System.Data.Common;
using AzureBank.Api.Middleware;
using AzureBank.Api.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace AzureBank.Api.Data;

/// <summary>
/// The commit gate (ADR-0058): at each commit of the request's own context, either refuses to let
/// the commit start, because the request deadline has already cancelled the request, or turns the
/// deadline off for as long as the commit runs.
/// </summary>
/// <remarks>
/// <para>
/// WHY A GATE AT ALL. A token can stop a commit from starting and can never interrupt one: SqlClient
/// has no asynchronous commit, and <c>DbTransaction.CommitAsync</c> checks the token only before it
/// starts. So once a commit has started, cancelling the request can only lose its answer: the money
/// moved and the visitor is told to try again. The gate makes "the deadline fired" and "a commit
/// started" exclusive, under the deadline's lock: a request that is cancelled commits nothing
/// afterwards, and a commit that started is answered.
/// </para>
/// <para>
/// ONE HOOK, EVERY COMMIT OF THE REQUEST'S CONTEXT: the explicit ones (transfers, the withdrawal,
/// set-primary, account closure), the audited saves the context opens for itself (deposit, rename,
/// reveal, PIN set), registration's, and EF's own implicit transactions. A commit that fails turns
/// the deadline back on, at its original instant, and the request stays marked as one that started a
/// commit.
/// </para>
/// <para>
/// NOT GATED: a context from a fresh scope (a refusal's audit row, the release of an idempotency
/// claim, the PIN counters), which finds no deadline in its scope; the endpoints marked
/// <see cref="NoRequestDeadlineAttribute"/>; hosts that register no gate; and the in-memory
/// provider, which has no transactions. A single statement outside a transaction (the idempotency
/// claim, storing an answer for replay) commits by itself and is not gated either.
/// </para>
/// <para>
/// A singleton: it holds no state. The deadline is found through the scope the context was resolved
/// from (EF records it as the context's application service provider).
/// </para>
/// </remarks>
public sealed class CommitGateInterceptor : DbTransactionInterceptor
{
    public override InterceptionResult TransactionCommitting(
        DbTransaction transaction, TransactionEventData eventData, InterceptionResult result)
    {
        Enter(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult> TransactionCommittingAsync(
        DbTransaction transaction, TransactionEventData eventData, InterceptionResult result,
        CancellationToken cancellationToken = default)
    {
        Enter(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) =>
        Landed(eventData.Context);

    public override Task TransactionCommittedAsync(
        DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Landed(eventData.Context);
        return Task.CompletedTask;
    }

    public override void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData) =>
        Failed(eventData);

    public override Task TransactionFailedAsync(
        DbTransaction transaction, TransactionErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Failed(eventData);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Skips the rollback to EF's savepoint when the request has been cancelled, and only then.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A save inside a transaction that fails is rolled back to the savepoint EF took before it,
    /// with the save's own token. Once the request is cancelled that attempt is refused before it is
    /// sent (<c>DbTransaction.RollbackAsync</c> checks the token first), so it changes nothing, and
    /// EF logs the refusal as a transaction ERROR, without the exception: measured, one such line for
    /// every cancelled write, a client hanging up included. Every caller then rolls the whole
    /// transaction back. Skipping the attempt changes nothing in the database and drops that line.
    /// </para>
    /// <para>
    /// BOTH TOKENS, NOT ONLY THE SAVE'S. Skipping is safe because a request whose deadline token is
    /// cancelled can start no commit afterwards (the gate refuses it), so the rows the savepoint
    /// would have undone can never land. A save's token that is cancelled while the request is not
    /// (another token handed to one save) proves nothing of the kind: a catch that saves again and
    /// commits in the same transaction would commit the failed batch's rows too. So that rollback
    /// is left to run.
    /// </para>
    /// </remarks>
    public override ValueTask<InterceptionResult> RollingBackToSavepointAsync(
        DbTransaction transaction, TransactionEventData eventData, InterceptionResult result,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(
            cancellationToken.IsCancellationRequested
            && DeadlineOf(eventData.Context) is { Token.IsCancellationRequested: true }
                ? InterceptionResult.Suppress()
                : result);

    private static void Enter(DbContext? context)
    {
        if (DeadlineOf(context) is { } deadline && !deadline.TryEnterCommit())
        {
            // Before the commit is sent: the transaction is still open and the caller's catch rolls
            // it back, as for any other failure before a commit.
            throw new OperationCanceledException(
                "The request deadline passed before this commit could start; nothing was committed.",
                deadline.Token);
        }
    }

    private static void Landed(DbContext? context)
    {
        if (DeadlineOf(context) is { } deadline)
        {
            deadline.Committed();
            ApiMetrics.CommitGateEntered.Add(1);
        }
    }

    private static void Failed(TransactionErrorEventData eventData)
    {
        // A refused commit is reported here as well (EF logs every commit that throws), and finds
        // the deadline already fired, where CommitFailed changes nothing.
        if (eventData.Action == "Commit" && DeadlineOf(eventData.Context) is { } deadline)
        {
            deadline.CommitFailed();
        }
    }

    /// <summary>
    /// The deadline of the request whose scope built <paramref name="context"/>, or null: the scope
    /// is the application service provider EF was given when it built the context's options.
    /// </summary>
    private static IRequestDeadline? DeadlineOf(DbContext? context) =>
        context?.GetService<IDbContextOptions>()
            .FindExtension<CoreOptionsExtension>()?
            .ApplicationServiceProvider?
            .GetService<RequestDeadlineScope>()?
            .Deadline;
}
