using AzureBank.Shared.Entities;

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

/// <summary>The demo ledger: two months of history on four accounts.</summary>
/// <remarks>NOT WRITTEN YET: it answers no rows, so its tests compile and fail.</remarks>
public static class DemoLedger
{
    /// <summary>A row to insert, the instant it happened, and the other half of its transfer.</summary>
    public sealed record Entry(Transaction Row, DateTime OccurredAt, Transaction? Pair);

    /// <summary>The rows, oldest first, with their balances chained and their ids in date order.</summary>
    public static IReadOnlyList<Entry> Build(IReadOnlyDictionary<DemoLedgerAccount, Account> accounts, DateTime now) => [];
}
