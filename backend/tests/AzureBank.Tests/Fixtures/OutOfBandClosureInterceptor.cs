using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AzureBank.Tests.Fixtures;

/// <summary>
/// Closes an account from a second connection, once, on the first command after arming whose text
/// holds the trigger, and records what the request sends to the accounts table before and after.
/// </summary>
/// <remarks>
/// <para>
/// TWO TRIGGERS. The default is the request's own <c>UPDATE [Accounts]</c>: the closure then lands
/// after the request read the account and before its save, the save loses the account's
/// <c>RowVersion</c>, and the request goes round its retry. Another trigger places the closure
/// earlier, for example on the read of the step-up authorisation, which comes after the request
/// looked at its accounts and before it reloads them: no save is lost there.
/// <see cref="Seen"/> says which of the two happened, so a proof asserts where its closure
/// landed and not only that it ran.
/// </para>
/// <para>
/// A raw connection, because every context the factory builds carries this interceptor. The
/// closure writes the account columns a closure writes and nothing else: no audit row and no
/// spent authorisation, since the proofs are about what the racing request does with the row.
/// </para>
/// <para>
/// Three variations of that write. With <c>emptyFirst</c> the same statement also sets the balance
/// to zero: the account columns a withdrawal of everything followed by a closure leaves; the
/// ledger is not touched. With <c>newPrimaryId</c> the closed account also stops being the primary
/// one and the named account becomes it, in one transaction: the columns the API leaves when a
/// user makes another account primary and then closes the old one. With <c>remove</c> the row is
/// deleted instead of closed, which only an account with no ledger rows allows.
/// </para>
/// </remarks>
public sealed class OutOfBandClosureInterceptor(
    string connectionString, Guid accountId, string trigger = OutOfBandClosureInterceptor.AccountUpdate,
    bool emptyFirst = false, Guid? newPrimaryId = null, bool remove = false)
    : DbCommandInterceptor
{
    /// <summary>The text every write of a request to the accounts table holds.</summary>
    public const string AccountUpdate = "UPDATE [Accounts]";

    private const string AccountsTable = "[Accounts]";

    private readonly Lock _gate = new();
    private bool _armed;
    private bool _fired;
    private bool _rodeAnAccountUpdate;
    private int _readsBefore;
    private int _updatesBefore;
    private int _readsAfter;
    private int _updatesAfter;

    /// <summary>
    /// What the request sent to the accounts table from the arming on, on either side of the
    /// closure. The command the closure rode is counted before it: the request had sent it.
    /// </summary>
    public sealed record Sent(
        int AccountReadsBefore, int AccountUpdatesBefore, bool RodeAnAccountUpdate,
        int AccountReadsAfter, int AccountUpdatesAfter)
    {
        public override string ToString() =>
            $"before the closure {AccountReadsBefore} reads and {AccountUpdatesBefore} updates of accounts, "
            + $"rode an update: {RodeAnAccountUpdate}, "
            + $"after it {AccountReadsAfter} reads and {AccountUpdatesAfter} updates";
    }

    /// <summary>
    /// Arms the interceptor so earlier setup writes are ignored.
    /// </summary>
    public void Arm()
    {
        lock (_gate)
        {
            _armed = true;
        }
    }

    /// <summary>
    /// True once the out-of-band closure update executes.
    /// </summary>
    public bool Fired
    {
        get
        {
            lock (_gate)
            {
                return _fired;
            }
        }
    }

    /// <summary>
    /// Rows matched by the out-of-band write: one for a closure or a removal, two when another
    /// account becomes the primary one in the same step.
    /// </summary>
    public int OutOfBandRowsAffected { get; private set; }

    /// <summary>The counts as they stand now. Read it before sending another request.</summary>
    public Sent Seen
    {
        get
        {
            lock (_gate)
            {
                return new Sent(_readsBefore, _updatesBefore, _rodeAnAccountUpdate, _readsAfter, _updatesAfter);
            }
        }
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData,
        InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        await RecordAndCloseIfThisIsTheTriggerAsync(command, cancellationToken);
        return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        await RecordAndCloseIfThisIsTheTriggerAsync(command, cancellationToken);
        return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    private async Task RecordAndCloseIfThisIsTheTriggerAsync(
        DbCommand command, CancellationToken cancellationToken)
    {
        var text = command.CommandText;
        var isUpdate = text.Contains(AccountUpdate, StringComparison.Ordinal);
        var isRead = !isUpdate && text.Contains(AccountsTable, StringComparison.Ordinal);
        bool fireNow;

        lock (_gate)
        {
            if (!_armed)
            {
                return;
            }

            fireNow = !_fired && text.Contains(trigger, StringComparison.Ordinal);
            if (_fired)
            {
                _readsAfter += isRead ? 1 : 0;
                _updatesAfter += isUpdate ? 1 : 0;
            }
            else
            {
                _readsBefore += isRead ? 1 : 0;
                _updatesBefore += isUpdate ? 1 : 0;
            }

            if (fireNow)
            {
                _fired = true;
                _rodeAnAccountUpdate = isUpdate;
            }
        }

        if (fireNow)
        {
            OutOfBandRowsAffected = await CloseAsync(
                connectionString, accountId, emptyFirst, newPrimaryId, remove, cancellationToken);
        }
    }

    /// <summary>
    /// The out-of-band write itself, on a connection of its own; returns the rows it matched. A
    /// control uses it to close, before the request, an account the API refuses to close: a
    /// primary one.
    /// </summary>
    public static async Task<int> CloseAsync(
        string connectionString, Guid accountId, bool emptyFirst = false, Guid? newPrimaryId = null,
        bool remove = false, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var close = connection.CreateCommand();
        close.Parameters.Add(new SqlParameter("@id", accountId));

        if (remove)
        {
            close.CommandText = "DELETE FROM [Accounts] WHERE [Id] = @id";
            return await close.ExecuteNonQueryAsync(cancellationToken);
        }

        var closure =
            "UPDATE [Accounts] SET "
            + (emptyFirst ? "[Balance] = 0, " : string.Empty)
            + (newPrimaryId is null ? string.Empty : "[IsPrimary] = 0, ")
            + "[IsDeleted] = 1, [DeletedAt] = @now, [UpdatedAt] = @now "
            + "WHERE [Id] = @id AND [IsDeleted] = 0";
        close.Parameters.Add(new SqlParameter("@now", DateTime.UtcNow));

        if (newPrimaryId is { } primary)
        {
            // The old primary first: one user holds one open primary account, by a unique index.
            close.CommandText =
                "SET XACT_ABORT ON; BEGIN TRANSACTION; " + closure + "; "
                + "UPDATE [Accounts] SET [IsPrimary] = 1, [UpdatedAt] = @now "
                + "WHERE [Id] = @primary AND [IsDeleted] = 0; COMMIT;";
            close.Parameters.Add(new SqlParameter("@primary", primary));
        }
        else
        {
            close.CommandText = closure;
        }

        return await close.ExecuteNonQueryAsync(cancellationToken);
    }
}
