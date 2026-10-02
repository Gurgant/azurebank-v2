using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Shared.Utilities;
using Microsoft.EntityFrameworkCore;

namespace AzureBank.Seeder.Seeders;

/// <summary>The four accounts the demo ledger hangs off, by the part each plays.</summary>
public enum DemoLedgerAccount
{
    /// <summary>The demo user's savings account, the primary one.</summary>
    OwnerSavings,

    /// <summary>The demo user's checking account.</summary>
    OwnerChecking,

    /// <summary>The first contact's savings account.</summary>
    JaneSavings,

    /// <summary>The second contact's investment account.</summary>
    MikeInvestment,
}

/// <summary>
/// The demo ledger: two months of history on the demo user's two accounts, and the transfers that
/// connect them to the two contacts'.
/// </summary>
/// <remarks>
/// <para>
/// ONE HISTORY, TWO USES. The fixed demo (<see cref="TransactionSeeder"/>) and every copy of the
/// demo pool (<c>DemoCopyBuilder</c>) are built from the rows below, so the two cannot drift apart.
/// The accounts are named by the part they play, never by a number or a handle: the fixed demo
/// finds its four by their literal numbers, a copy has just created its own.
/// </para>
/// <para>
/// Every account's rows chain to the balance the account holds: each row's <c>BalanceBefore</c> is
/// the previous row's <c>BalanceAfter</c>, and the newest row ends at the account's balance. The
/// balances are computed backwards from that figure rather than written down, so the history cannot
/// drift from the account it belongs to. Transfers are written the way <c>TransferService</c> writes
/// them: a <c>TransferOut</c> and a <c>TransferIn</c> linked to each other, an external one carrying
/// the other party's handle, an internal one described by the two accounts' names.
/// </para>
/// </remarks>
public static class DemoLedger
{
    /// <summary>A row to insert, the instant it happened, and the other half of its transfer.</summary>
    public sealed record Entry(Transaction Row, DateTime OccurredAt, Transaction? Pair);

    /// <summary>A deposit or a withdrawal: one row, <paramref name="Ago"/> before the seed.</summary>
    private sealed record Movement(DemoLedgerAccount Account, TimeSpan Ago, TransactionType Type, decimal Amount, string Description);

    /// <summary>
    /// A transfer: two rows, linked to each other. <paramref name="Note"/> is what the sender typed; an
    /// internal transfer has none and is described by its accounts, as <c>TransferService</c> does.
    /// </summary>
    private sealed record Transfer(DemoLedgerAccount From, DemoLedgerAccount To, TimeSpan Ago, decimal Amount, string? Note);

    private const DemoLedgerAccount Savings = DemoLedgerAccount.OwnerSavings;
    private const DemoLedgerAccount Checking = DemoLedgerAccount.OwnerChecking;
    private const DemoLedgerAccount Jane = DemoLedgerAccount.JaneSavings;
    private const DemoLedgerAccount Mike = DemoLedgerAccount.MikeInvestment;

    private static TimeSpan Ago(int days, int hours = 0) => TimeSpan.FromDays(days) + TimeSpan.FromHours(hours);

    /*
      THE LEDGER. The last four savings movements are the original demo rows — salary three days back,
      ATM two, online purchase one, refund today — and SeededDemoDataSqlServerTests still pins them at
      those offsets. The rest reaches two months behind them, so the history has weeks to group, the
      dashboard's month has income and spending, and its five most recent rows include two people:
      the dashboard names recent recipients from them.

      Hours are offsets too, so no two rows of one account share an instant, and the two rows of a
      transfer share theirs, as a real transfer's do.
    */
    private static readonly Movement[] Movements =
    [
        new(Savings, Ago(63, 4), TransactionType.Deposit, 5000.00m, "Salary deposit"),
        new(Savings, Ago(48, 2), TransactionType.Withdrawal, 300.00m, "ATM withdrawal"),
        new(Savings, Ago(33, 4), TransactionType.Deposit, 5000.00m, "Salary deposit"),
        new(Checking, Ago(31, 3), TransactionType.Withdrawal, 950.00m, "Monthly rent"),
        new(Checking, Ago(27, 7), TransactionType.Withdrawal, 84.60m, "Groceries - supermarket"),
        new(Savings, Ago(25, 3), TransactionType.Withdrawal, 1200.00m, "Holiday booking - flights"),
        new(Checking, Ago(20, 5), TransactionType.Withdrawal, 120.35m, "Electricity and gas bill"),
        new(Savings, Ago(18, 5), TransactionType.Deposit, 120.00m, "Cashback reward"),
        new(Checking, Ago(12, 4), TransactionType.Withdrawal, 15.99m, "Mobile phone plan"),
        new(Checking, Ago(4, 6), TransactionType.Withdrawal, 67.20m, "Groceries - supermarket"),
        new(Savings, Ago(3), TransactionType.Deposit, 5000.00m, "Salary deposit"),
        new(Savings, Ago(2), TransactionType.Withdrawal, 200.00m, "ATM withdrawal"),
        new(Savings, Ago(1), TransactionType.Withdrawal, 150.00m, "Online purchase - Electronics"),
        new(Savings, Ago(0), TransactionType.Deposit, 350.00m, "Refund - Return item"),
    ];

    private static readonly Transfer[] Transfers =
    [
        new(Savings, Checking, Ago(34, 6), 1500.00m, null),
        new(Checking, Jane, Ago(16, 8), 60.00m, "Dinner split"),
        new(Jane, Checking, Ago(10, 3), 30.00m, "Taxi share"),
        new(Savings, Checking, Ago(6, 2), 800.00m, null),
        new(Checking, Mike, Ago(1, 12), 45.00m, "Concert tickets"),
        new(Checking, Jane, Ago(0, 12), 25.00m, "Coffee and cake"),
    ];

    /// <summary>
    /// How many rows the ledger has: one for each movement and two for each transfer, which is what
    /// <see cref="Build"/> writes.
    /// </summary>
    public static int RowCount => Movements.Length + (2 * Transfers.Length);

    /// <summary>
    /// The rows, oldest first, with their balances chained and their ids in date order.
    /// </summary>
    /// <param name="accounts">
    /// The four accounts, each with its owner loaded: an external transfer's rows carry the handles
    /// of the accounts' owners, so a copy's rows name the copy's own users.
    /// </param>
    /// <param name="now">The seed instant, in UTC. Every row's date is an offset from it.</param>
    public static IReadOnlyList<Entry> Build(IReadOnlyDictionary<DemoLedgerAccount, Account> accounts, DateTime now)
    {
        var ledger = new List<Entry>();

        foreach (var movement in Movements)
        {
            var row = NewRow(accounts[movement.Account], movement.Type, movement.Amount, movement.Description);
            ledger.Add(new Entry(row, now - movement.Ago, null));
        }

        foreach (var transfer in Transfers)
        {
            var from = accounts[transfer.From];
            var to = accounts[transfer.To];
            var isInternal = from.UserId == to.UserId;

            var outgoing = NewRow(from, TransactionType.TransferOut, transfer.Amount,
                isInternal ? $"Internal transfer to {to.Name}" : transfer.Note ?? $"Transfer to @{to.User.AzureTag}");
            var incoming = NewRow(to, TransactionType.TransferIn, transfer.Amount,
                isInternal ? $"Internal transfer from {from.Name}" : transfer.Note ?? $"Transfer from @{from.User.AzureTag}");
            if (!isInternal)
            {
                outgoing.RecipientAzureTag = to.User.AzureTag;
                incoming.SenderAzureTag = from.User.AzureTag;
            }

            var at = now - transfer.Ago;
            ledger.Add(new Entry(outgoing, at, incoming));
            ledger.Add(new Entry(incoming, at, outgoing));
        }

        ledger = [.. ledger.OrderBy(r => r.OccurredAt)];

        // Each account's chain, computed backwards from the balance it holds: the opening balance is
        // what is left once every row is taken back out, and each row then moves it forward.
        foreach (var chain in ledger.GroupBy(r => r.Row.AccountId))
        {
            var balance = chain.First().Row.Account.Balance - chain.Sum(r => Signed(r.Row));
            foreach (var entry in chain)
            {
                entry.Row.BalanceBefore = balance;
                balance += Signed(entry.Row);
                entry.Row.BalanceAfter = balance;
            }
        }

        // Version 7 ids carry a timestamp. Stamping each with the row's own instant keeps the history's
        // tie-break (CreatedAt, then Id) in agreement with the dates, as a real row's id is.
        foreach (var entry in ledger)
        {
            entry.Row.Id = Guid.CreateVersion7(new DateTimeOffset(entry.OccurredAt, TimeSpan.Zero));
            entry.Row.CreatedAt = entry.OccurredAt;
        }

        return ledger;
    }

    private static decimal Signed(Transaction row) =>
        row.Type is TransactionType.Deposit or TransactionType.TransferIn ? row.Amount : -row.Amount;

    /*
      The real generator, for every number. The literals it replaced were the pre-#89 19-character
      shape, which the generator has not produced since 7a9fa26 — and the seeder was the ONLY place a
      retired transaction-number shape reached a real database rather than a test double.

      A NOTE ON THE DATE INSIDE THE NUMBER, because it is deliberately not the row's date. The
      generator stamps DateTime.UtcNow, so every number carries today while the rows are back-dated by
      up to two months. That is accepted rather than overlooked: the alternative is a
      date-parameterised generator seam existing only for demo data, and the number's date has never
      been load-bearing — nothing parses it, and ADR-0035 put the check symbol over it precisely so it
      is verified rather than interpreted.
    */
    private static Transaction NewRow(Account account, TransactionType type, decimal amount, string description) => new()
    {
        TransactionNumber = IdGenerator.GenerateTransactionNumber(),
        AccountId = account.Id,
        Account = account,
        Type = type,
        Amount = amount,
        Description = description,
        Status = TransactionStatus.Completed,
    };

    /// <summary>
    /// Applies each row's date, and links the two halves of each transfer, with UPDATEs that never pass
    /// through the change tracker. The rows must already be saved, and the caller's transaction must
    /// hold both steps: rows committed without their dates are a flat ledger nothing repairs.
    ///
    /// <para>
    /// The dates are required, not stylistic. <c>AzureBankDbContext.UpdateTimestamps()</c> has a loop
    /// specifically for <c>Transaction</c> ("doesn't inherit BaseEntity") that assigns
    /// <c>CreatedAt = DateTime.UtcNow</c> to every Added row, so the dates set in
    /// <see cref="Build"/> never survive the save — measured against LocalDB, a row asked for
    /// 2026-08-06 11:57 was stored as 2026-08-09 11:57. Saving a second time is not an option either:
    /// <c>EnforceTransactionImmutability</c> rejects any Modified transaction.
    /// </para>
    /// <para>
    /// The links belong here for the same reason. Two rows that point at each other cannot go in one
    /// INSERT, so <c>TransferService</c> saves the pair and then sets the outgoing row's link in a
    /// second step; here both links are set in the same UPDATE as the date.
    /// </para>
    /// <para>
    /// <c>ExecuteUpdateAsync</c> is the technique this repository already sanctions for exactly this
    /// problem — <c>HistoricalBalanceSqlServerTests.AgeTransactionsAsync</c> ages its fixture rows the
    /// same way, and documents the same two reasons. It bypasses both guards precisely because it
    /// bypasses the change tracker, which is why it belongs in the Seeder and a SQL-gated test and
    /// nowhere near a request path. Relational-only, which the Seeder always is.
    /// </para>
    /// <para>
    /// Without the dates the demo rows all landed in the same minute: the history screen grouped them
    /// under one date, and — because CI seeds the database the real-stack contract, integration and
    /// E2E jobs run against — any date filtering or historical-balance behaviour was being exercised
    /// against a ledger with no time spread at all.
    /// </para>
    /// </summary>
    public static async Task AgeAndLinkAsync(
        AzureBankDbContext context, IReadOnlyList<Entry> ledger, CancellationToken cancellationToken = default)
    {
        foreach (var entry in ledger)
        {
            var id = entry.Row.Id;
            var at = entry.OccurredAt;
            if (entry.Pair is { } pair)
            {
                Guid? related = pair.Id;
                await context.Transactions
                    .Where(t => t.Id == id)
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(t => t.CreatedAt, at).SetProperty(t => t.RelatedTransactionId, related),
                        cancellationToken);
            }
            else
            {
                await context.Transactions
                    .Where(t => t.Id == id)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.CreatedAt, at), cancellationToken);
            }
        }
    }
}
