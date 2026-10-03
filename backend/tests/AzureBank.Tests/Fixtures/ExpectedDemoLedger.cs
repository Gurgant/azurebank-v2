extern alias seeder;

using AzureBank.Shared.Enums;
using seeder::AzureBank.Seeder.Seeders;

namespace AzureBank.Tests.Fixtures;

/// <summary>
/// The demo's history, written down row by row: what a visitor's copy opens on, and what the fixed
/// demo opens on.
/// </summary>
/// <remarks>
/// <para>
/// A count, a chain that adds up and a first and a last date are all true of a ledger in which the
/// rent is 951 or the dinner was a lunch. These are the rows themselves: the account, how long
/// before the seed instant, what moved, how much, and the words the history shows.
/// </para>
/// <para>
/// An internal transfer is described by the other account's name, as <c>TransferService</c>
/// describes one; an external transfer by the note its sender typed, on both of its rows.
/// </para>
/// </remarks>
internal static class ExpectedDemoLedger
{
    /// <summary>One row of the history.</summary>
    public sealed record Row(DemoLedgerAccount Account, TimeSpan Ago, TransactionType Type, decimal Amount, string? Description);

    private const DemoLedgerAccount Savings = DemoLedgerAccount.OwnerSavings;
    private const DemoLedgerAccount Checking = DemoLedgerAccount.OwnerChecking;
    private const DemoLedgerAccount Jane = DemoLedgerAccount.JaneSavings;
    private const DemoLedgerAccount Mike = DemoLedgerAccount.MikeInvestment;

    private const TransactionType In = TransactionType.Deposit;
    private const TransactionType Out = TransactionType.Withdrawal;
    private const TransactionType Sent = TransactionType.TransferOut;
    private const TransactionType Received = TransactionType.TransferIn;

    private static TimeSpan Ago(int days, int hours = 0) => TimeSpan.FromDays(days) + TimeSpan.FromHours(hours);

    /// <summary>The 26 rows, oldest first: fourteen movements and six transfers of two rows each.</summary>
    public static IReadOnlyList<Row> Rows { get; } =
    [
        new(Savings, Ago(63, 4), In, 5000.00m, "Salary deposit"),
        new(Savings, Ago(48, 2), Out, 300.00m, "ATM withdrawal"),
        new(Savings, Ago(34, 6), Sent, 1500.00m, "Internal transfer to Checking"),
        new(Checking, Ago(34, 6), Received, 1500.00m, "Internal transfer from Main Savings"),
        new(Savings, Ago(33, 4), In, 5000.00m, "Salary deposit"),
        new(Checking, Ago(31, 3), Out, 950.00m, "Monthly rent"),
        new(Checking, Ago(27, 7), Out, 84.60m, "Groceries - supermarket"),
        new(Savings, Ago(25, 3), Out, 1200.00m, "Holiday booking - flights"),
        new(Checking, Ago(20, 5), Out, 120.35m, "Electricity and gas bill"),
        new(Savings, Ago(18, 5), In, 120.00m, "Cashback reward"),
        new(Checking, Ago(16, 8), Sent, 60.00m, "Dinner split"),
        new(Jane, Ago(16, 8), Received, 60.00m, "Dinner split"),
        new(Checking, Ago(12, 4), Out, 15.99m, "Mobile phone plan"),
        new(Jane, Ago(10, 3), Sent, 30.00m, "Taxi share"),
        new(Checking, Ago(10, 3), Received, 30.00m, "Taxi share"),
        new(Savings, Ago(6, 2), Sent, 800.00m, "Internal transfer to Checking"),
        new(Checking, Ago(6, 2), Received, 800.00m, "Internal transfer from Main Savings"),
        new(Checking, Ago(4, 6), Out, 67.20m, "Groceries - supermarket"),
        new(Savings, Ago(3), In, 5000.00m, "Salary deposit"),
        new(Savings, Ago(2), Out, 200.00m, "ATM withdrawal"),
        new(Checking, Ago(1, 12), Sent, 45.00m, "Concert tickets"),
        new(Mike, Ago(1, 12), Received, 45.00m, "Concert tickets"),
        new(Savings, Ago(1), Out, 150.00m, "Online purchase - Electronics"),
        new(Checking, Ago(0, 12), Sent, 25.00m, "Coffee and cake"),
        new(Jane, Ago(0, 12), Received, 25.00m, "Coffee and cake"),
        new(Savings, Ago(0), In, 350.00m, "Refund - Return item"),
    ];
}
