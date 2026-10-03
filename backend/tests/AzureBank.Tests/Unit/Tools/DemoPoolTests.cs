extern alias seeder;

using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using AzureBank.Shared.Constants;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using seeder::AzureBank.Seeder.Pool;
using seeder::AzureBank.Seeder.Seeders;

namespace AzureBank.Tests.Unit.Tools;

/// <summary>
/// The parts of the demo pool that need no database: the identities a copy is given, the ledger it
/// starts with, and what a run's counts mean for the process that ran it.
/// </summary>
/// <remarks>
/// The builder and the recycler themselves run against SQL Server
/// (<c>DemoPoolSeedSqlServerTests</c>, <c>DemoPoolRecycleSqlServerTests</c>); everything here is a
/// pure function, so each rule can be read off one line of a table.
/// </remarks>
public class DemoPoolTests
{
    private const int Draws = 1_000;

    // ── The PIN ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheDemoPin_Is123456_AndFitsThePinRule()
    {
        // The value a visitor reads on the page and the value the builder hashes are this constant.
        DemoCopyDefaults.Pin.Should().Be("123456");
        DemoCopyDefaults.Pin.Should().MatchRegex(ValidationRules.PinPattern);
    }

    // ── Identities ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AThousandCopies_EveryHandleFitsTheHandleRule_AndACopysThreeHandlesShareOneSuffix()
    {
        var handleRule = new Regex(ValidationRules.AzureTagPattern);

        for (var i = 0; i < Draws; i++)
        {
            var copy = DemoCredentials.Create();

            copy.Suffix.Should().MatchRegex("^[a-z0-9]{4}$", "the suffix is four characters of a-z and 0-9");
            copy.Owner.AzureTag.Should().Be($"john_{copy.Suffix}");
            copy.Jane.AzureTag.Should().Be($"jane_{copy.Suffix}");
            copy.Mike.AzureTag.Should().Be($"mike_{copy.Suffix}");

            foreach (var handle in new[] { copy.Owner.AzureTag, copy.Jane.AzureTag, copy.Mike.AzureTag })
            {
                handleRule.IsMatch(handle).Should().BeTrue(
                    "'{0}' must pass the rule every handle is validated against, or a transfer to it answers 400", handle);
            }
        }
    }

    [Fact]
    public void AThousandCopies_EveryEmailIsAnAddressOnTheReservedDomain_WithSixteenRandomCharacters()
    {
        var address = new EmailAddressAttribute();

        for (var i = 0; i < Draws; i++)
        {
            var copy = DemoCredentials.Create();

            copy.Owner.Email.Should().MatchRegex(@"^demo-[a-z0-9]{16}@azurebank\.example$");
            copy.Jane.Email.Should().MatchRegex(@"^contact-[a-z0-9]{16}@azurebank\.example$");
            copy.Mike.Email.Should().MatchRegex(@"^contact-[a-z0-9]{16}@azurebank\.example$");

            foreach (var email in new[] { copy.Owner.Email, copy.Jane.Email, copy.Mike.Email })
            {
                address.IsValid(email).Should().BeTrue("'{0}' is what the sign-in form validates with [EmailAddress]", email);
                email.Length.Should().BeLessThanOrEqualTo(ValidationRules.EmailMaxLength);
            }
        }
    }

    [Fact]
    public void ACopysCast_IsJohnSmith_JaneSmith_AndMikeBrown()
    {
        var copy = DemoCredentials.Create();

        (copy.Owner.FirstName, copy.Owner.LastName).Should().Be(("John", "Smith"));
        (copy.Jane.FirstName, copy.Jane.LastName).Should().Be(("Jane", "Smith"));
        (copy.Mike.FirstName, copy.Mike.LastName).Should().Be(("Mike", "Brown"));
    }

    [Fact]
    public void AThousandCopies_NoRandomPartRepeats_AndEveryCharacterOfTheAlphabetIsDrawn()
    {
        var copies = Enumerable.Range(0, Draws).Select(_ => DemoCredentials.Create()).ToList();

        // 16 characters of 36 are 82.7 bits: a repeat among 3,000 means the parts are not random,
        // or one is derived from another.
        var emailParts = copies
            .SelectMany(c => new[] { c.Owner.Email, c.Jane.Email, c.Mike.Email })
            .Select(e => Regex.Match(e, "-([a-z0-9]*)@").Groups[1].Value)
            .ToList();
        emailParts.Should().OnlyHaveUniqueItems("each email's random part is drawn on its own");

        // 36^4 is 1,679,616 suffixes: 1,000 draws repeat about 0.3 times on average, so 990 distinct
        // leaves a wide margin and still fails a generator that returns a constant or counts.
        copies.Select(c => c.Suffix).Distinct().Should().HaveCountGreaterThanOrEqualTo(990);

        // Each of the 36 characters is expected about 111 times in 4,000 suffix characters and 1,333
        // times in 48,000 email characters; the chance one is absent is below 1e-48.
        const string alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
        string.Concat(copies.Select(c => c.Suffix)).Distinct().Should().BeEquivalentTo(alphabet.ToCharArray());
        string.Concat(emailParts).Distinct().Should().BeEquivalentTo(alphabet.ToCharArray());
    }

    [Fact]
    public void CopiesThatDrawTheSameSuffix_HaveAddressesThatShareNothing()
    {
        // The address is what a visitor signs in with. The suffix is on three handles, and a rename
        // that is answered "taken" tells anyone that a handle exists. So an address worked out from
        // the suffix would be an address a stranger can work out, and two copies that drew one
        // suffix would then carry one address.
        //
        // 36^4 is 1,679,616 suffixes: 20,000 draws hold about 119 pairs that share one
        // (20,000 x 19,999 / 2 / 1,679,616), and the chance that they hold none is below 1e-51. The
        // thousand draws of the tests above hold 0.3, which is why they cannot see this.
        var sharing = Enumerable.Range(0, 20_000)
            .Select(_ => DemoCredentials.Create())
            .GroupBy(copy => copy.Suffix)
            .Where(copies => copies.Count() > 1)
            .ToList();

        sharing.Should().NotBeEmpty("ARRANGE: a suffix must have been drawn twice, else the test proves nothing");
        foreach (var copies in sharing)
        {
            copies.SelectMany(copy => new[] { copy.Owner.Email, copy.Jane.Email, copy.Mike.Email })
                .Should().OnlyHaveUniqueItems(
                    "the suffix '{0}' says nothing about the addresses of a copy that carries it", copies.Key);
        }
    }

    // ── The ledger ───────────────────────────────────────────────────────────────────────────────

    private static readonly DateTime SeedInstant = new(2026, 10, 1, 9, 30, 0, DateTimeKind.Utc);

    private static Dictionary<DemoLedgerAccount, Account> ACopysAccounts()
    {
        static ApplicationUser User(string handle, string first, string last) => new()
        {
            Id = Guid.CreateVersion7(),
            AzureTag = handle,
            FirstName = first,
            LastName = last,
        };

        static Account Open(ApplicationUser owner, string name, AccountType type, decimal balance, bool primary) => new()
        {
            Id = Guid.CreateVersion7(),
            UserId = owner.Id,
            User = owner,
            AccountNumber = AzureBank.Shared.Utilities.IdGenerator.GenerateAccountNumber(),
            Name = name,
            Type = type,
            Balance = balance,
            IsPrimary = primary,
        };

        var john = User("john_k7m2", "John", "Smith");
        var jane = User("jane_k7m2", "Jane", "Smith");
        var mike = User("mike_k7m2", "Mike", "Brown");

        return new Dictionary<DemoLedgerAccount, Account>
        {
            [DemoLedgerAccount.OwnerSavings] = Open(john, "Main Savings", AccountType.Savings, 12450.00m, primary: true),
            [DemoLedgerAccount.OwnerChecking] = Open(john, "Checking", AccountType.Checking, 2300.00m, primary: false),
            [DemoLedgerAccount.JaneSavings] = Open(jane, "Personal Savings", AccountType.Savings, 8500.00m, primary: true),
            [DemoLedgerAccount.MikeInvestment] = Open(mike, "Investment Account", AccountType.Investment, 25000.00m, primary: true),
        };
    }

    private static decimal Signed(Transaction row) =>
        row.Type is TransactionType.Deposit or TransactionType.TransferIn ? row.Amount : -row.Amount;

    [Fact]
    public void TheLedger_IsTwentySixRows_ElevenOnEachOfTheOwnersAccounts_ThreeOnJanes_OneOnMikes()
    {
        var accounts = ACopysAccounts();

        var ledger = DemoLedger.Build(accounts, SeedInstant);

        ledger.Should().HaveCount(26, "fourteen movements and six transfers of two rows each");
        var perAccount = ledger.GroupBy(e => e.Row.AccountId).ToDictionary(g => g.Key, g => g.Count());
        perAccount.Should().BeEquivalentTo(new Dictionary<Guid, int>
        {
            [accounts[DemoLedgerAccount.OwnerSavings].Id] = 11,
            [accounts[DemoLedgerAccount.OwnerChecking].Id] = 11,
            [accounts[DemoLedgerAccount.JaneSavings].Id] = 3,
            [accounts[DemoLedgerAccount.MikeInvestment].Id] = 1,
        });
        ledger.Select(e => e.Row.TransactionNumber).Should().OnlyHaveUniqueItems();
        ledger.Should().OnlyContain(e => e.Row.Status == TransactionStatus.Completed);
    }

    [Fact]
    public void TheLedger_IsTheDemosHistory_RowForRow()
    {
        var accounts = ACopysAccounts();
        var roles = accounts.ToDictionary(account => account.Value.Id, account => account.Key);

        var ledger = DemoLedger.Build(accounts, SeedInstant);

        // What a visitor reads: which account, how long ago, what moved, how much, and the words.
        ledger.Select(e => new ExpectedDemoLedger.Row(
                roles[e.Row.AccountId], SeedInstant - e.OccurredAt, e.Row.Type, e.Row.Amount, e.Row.Description))
            .Should().BeEquivalentTo(
                ExpectedDemoLedger.Rows, "a count and a chain that adds up are also true of a ledger with other amounts and other words");
    }

    [Fact]
    public void EveryAccountsChain_EndsAtTheBalanceTheAccountHolds()
    {
        var accounts = ACopysAccounts();

        var ledger = DemoLedger.Build(accounts, SeedInstant);

        var chains = ledger.GroupBy(e => e.Row.AccountId).ToList();
        chains.Should().HaveCount(4, "the ledger reaches all four accounts");
        foreach (var chain in chains)
        {
            // The history's own order: the instant, then the id.
            var ordered = chain.OrderBy(e => e.OccurredAt).ThenBy(e => e.Row.Id).Select(e => e.Row).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                ordered[i].BalanceAfter.Should().Be(
                    ordered[i].BalanceBefore + Signed(ordered[i]), "a row moves its balance by its own amount");
                if (i > 0)
                {
                    ordered[i].BalanceBefore.Should().Be(
                        ordered[i - 1].BalanceAfter, "each row starts where the one before it ended");
                }
            }

            var account = accounts.Values.Single(a => a.Id == chain.Key);
            ordered[^1].BalanceAfter.Should().Be(account.Balance, "the newest row ends at the account's balance");
            ordered[0].BalanceBefore.Should().BeGreaterThanOrEqualTo(0, "no account opened overdrawn");
        }
    }

    [Fact]
    public void EveryTransfer_IsTwoRowsThatNameEachOther_AndAnExternalOneCarriesTheCopysOwnHandles()
    {
        var accounts = ACopysAccounts();
        var owners = accounts.Values.ToDictionary(a => a.Id, a => a.User);

        var ledger = DemoLedger.Build(accounts, SeedInstant);

        var outgoing = ledger.Where(e => e.Row.Type == TransactionType.TransferOut).ToList();
        outgoing.Should().HaveCount(6);
        ledger.Count(e => e.Row.Type == TransactionType.TransferIn).Should().Be(6);
        ledger.Where(e => e.Row.Type is TransactionType.Deposit or TransactionType.Withdrawal)
            .Should().OnlyContain(e => e.Pair == null, "a deposit or a withdrawal is one row");

        foreach (var sent in outgoing)
        {
            sent.Pair.Should().NotBeNull("a transfer's row names its other half");
            var received = ledger.Single(e => ReferenceEquals(e.Row, sent.Pair));
            received.Pair.Should().BeSameAs(sent.Row, "the link runs both ways");
            received.Row.Type.Should().Be(TransactionType.TransferIn);
            received.Row.Amount.Should().Be(sent.Row.Amount);
            received.OccurredAt.Should().Be(sent.OccurredAt, "one transfer, one instant");

            var from = owners[sent.Row.AccountId];
            var to = owners[received.Row.AccountId];
            if (from.Id == to.Id)
            {
                sent.Row.RecipientAzureTag.Should().BeNull();
                received.Row.SenderAzureTag.Should().BeNull();
                sent.Row.Description.Should().StartWith("Internal transfer to ");
                received.Row.Description.Should().StartWith("Internal transfer from ");
            }
            else
            {
                // The handles come from the accounts' owners, so a copy's rows name the copy's own
                // users and never the fixed demo's.
                sent.Row.RecipientAzureTag.Should().Be(to.AzureTag);
                received.Row.SenderAzureTag.Should().Be(from.AzureTag);
                received.Row.Description.Should().Be(sent.Row.Description, "the sender's note travels with the money");
            }
        }

        outgoing.Count(e => e.Row.RecipientAzureTag == "jane_k7m2").Should().Be(2);
        outgoing.Count(e => e.Row.RecipientAzureTag == "mike_k7m2").Should().Be(1);
        outgoing.Count(e => e.Row.RecipientAzureTag == null).Should().Be(2, "two moves between the owner's own accounts");
        ledger.Count(e => e.Row.SenderAzureTag == "jane_k7m2").Should().Be(1, "one transfer comes back from Jane");
    }

    [Fact]
    public void TheRows_AreDatedAsOffsetsFromTheSeedInstant_AndTheirIdsFollowTheDates()
    {
        var ledger = DemoLedger.Build(ACopysAccounts(), SeedInstant);

        ledger.Should().NotBeEmpty();
        ledger.Select(e => e.OccurredAt).Should().BeInAscendingOrder("the rows come oldest first");
        ledger[0].OccurredAt.Should().Be(SeedInstant - TimeSpan.FromDays(63) - TimeSpan.FromHours(4), "the first salary");
        ledger[^1].OccurredAt.Should().Be(SeedInstant, "the refund is the seed instant itself");
        ledger.Should().OnlyContain(e => e.Row.CreatedAt == e.OccurredAt);

        // A version 7 id carries its instant, so the history's tie-break (CreatedAt, then Id) agrees
        // with the dates. Its first 48 bits are the Unix time in milliseconds.
        foreach (var entry in ledger)
        {
            var bytes = entry.Row.Id.ToByteArray(bigEndian: true);
            var milliseconds = bytes.Take(6).Aggregate(0L, (value, b) => (value << 8) | b);
            DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime
                .Should().Be(entry.OccurredAt, "the id is stamped with the row's own instant");
        }
    }

    // ── Exit codes ───────────────────────────────────────────────────────────────────────────────

    private static PoolRunSummary Run(
        int rowsAtStart, int freeAtStart, int free, int target, int buildFailed, int foreignUsers, int deleteFailed, bool ceiling) =>
        new()
        {
            RowsAtStart = rowsAtStart,
            FreeAtStart = freeAtStart,
            Free = free,
            Target = target,
            BuildFailed = buildFailed,
            ForeignUsers = foreignUsers,
            DeleteFailed = deleteFailed,
            Ceiling = ceiling,
        };

    [Theory]
    //          rows  was free target buildFailed foreign deleteFailed ceiling lowMark  code
    [InlineData(30, 19, 50, 50, 0, 0, 0, false, 20, 10)] // PoolLow: fewer free than the mark, and not empty
    [InlineData(30, 1, 50, 50, 0, 0, 0, false, 20, 10)]
    [InlineData(30, 0, 50, 50, 0, 0, 0, false, 20, 11)] // PoolEmpty: visitors may have been turned away
    [InlineData(30, 0, 50, 50, 0, 0, 0, false, 0, 11)] // empty is empty whatever the mark
    [InlineData(30, 30, 49, 50, 1, 0, 0, false, 20, 12)] // TopUpIncomplete: a build failed and the pool is short
    [InlineData(0, 0, 0, 50, 3, 0, 0, false, 20, 12)] // even on the first fill
    [InlineData(30, 30, 50, 50, 0, 1, 0, false, 20, 13)] // ForeignUsers
    [InlineData(0, 0, 0, 50, 0, 4, 0, false, 20, 13)] // the wrong database: nothing was written
    [InlineData(30, 30, 50, 50, 0, 0, 1, false, 20, 14)] // DeleteFailed
    [InlineData(30, 30, 10, 10, 0, 0, 0, true, 20, 15)] // ClaimCeiling
    public void ASignal_GivesRecycleItsOwnExitCode(
        int rowsAtStart, int freeAtStart, int free, int target, int buildFailed, int foreignUsers, int deleteFailed,
        bool ceiling, int lowMark, int expected)
    {
        var summary = Run(rowsAtStart, freeAtStart, free, target, buildFailed, foreignUsers, deleteFailed, ceiling);

        PoolExitCodes.From(summary, lowMark).Should().Be(expected);
    }

    [Theory]
    //          rows  was free target buildFailed foreign deleteFailed ceiling lowMark  code
    [InlineData(30, 0, 5, 10, 3, 2, 1, true, 20, 13)] // everything at once: 13 first
    [InlineData(30, 0, 5, 10, 3, 0, 1, true, 20, 14)] // then 14
    [InlineData(30, 0, 5, 10, 3, 0, 0, true, 20, 12)] // then 12
    [InlineData(30, 0, 10, 10, 0, 0, 0, true, 20, 15)] // then 15
    [InlineData(30, 0, 50, 50, 0, 0, 0, false, 20, 11)] // then 11; 10 needs a free copy, so the two never meet
    public void WhenSeveralSignalsApply_ThePrecedenceIs13_14_12_15_11_10(
        int rowsAtStart, int freeAtStart, int free, int target, int buildFailed, int foreignUsers, int deleteFailed,
        bool ceiling, int lowMark, int expected)
    {
        var summary = Run(rowsAtStart, freeAtStart, free, target, buildFailed, foreignUsers, deleteFailed, ceiling);

        PoolExitCodes.From(summary, lowMark).Should().Be(expected);
    }

    /// <summary>
    /// CONTROLS. Every row here expects 0, which is also what a function that knows no signal
    /// answers, so these say nothing alone: they are the edges of the rows above, and each one fails
    /// when its rule is written one step too wide.
    /// </summary>
    [Theory]
    //          rows  was free target buildFailed foreign deleteFailed ceiling lowMark
    [InlineData(50, 50, 50, 50, 0, 0, 0, false, 20)] // a full pool
    [InlineData(0, 0, 50, 50, 0, 0, 0, false, 20)] // the first fill of an empty database is not "empty"
    [InlineData(30, 20, 50, 50, 0, 0, 0, false, 20)] // at the mark is not below it
    [InlineData(30, 1, 50, 50, 0, 0, 0, false, 0)] // a mark of 0 turns "low" off
    [InlineData(30, 30, 50, 50, 1, 0, 0, false, 20)] // a build failed and the target was still reached
    public void WithNoSignal_RecycleExitsZero(
        int rowsAtStart, int freeAtStart, int free, int target, int buildFailed, int foreignUsers, int deleteFailed,
        bool ceiling, int lowMark)
    {
        var summary = Run(rowsAtStart, freeAtStart, free, target, buildFailed, foreignUsers, deleteFailed, ceiling);

        PoolExitCodes.From(summary, lowMark).Should().Be(0);
    }

    [Theory]
    //          rows  was free target buildFailed foreign wrongDatabase code
    [InlineData(30, 30, 49, 50, 1, 0, false, 12)] // a build failed and the pool is short
    [InlineData(30, 30, 49, 50, 1, 1, false, 12)] // the same, beside a user outside every copy
    [InlineData(0, 0, 0, 50, 0, 4, true, 13)] // the wrong database: nothing was written
    public void SeedPool_ReportsOnlyWhatItDid_AShortTopUpOrTheWrongDatabase(
        int rowsAtStart, int freeAtStart, int free, int target, int buildFailed, int foreignUsers, bool wrongDatabase, int expected)
    {
        var counted = Run(rowsAtStart, freeAtStart, free, target, buildFailed, foreignUsers, deleteFailed: 0, ceiling: false);
        var summary = counted with { StoppedOnTheWrongDatabase = wrongDatabase };

        PoolExitCodes.ForSeedPool(summary).Should().Be(expected);
    }

    /// <summary>
    /// CONTROL: a user outside every copy, beside a pool, is not <c>seed-pool</c>'s signal. It is
    /// the one-shot a stack waits for, and the user is on its line and is <c>recycle</c>'s 13.
    /// Recycle's own function, given the same counts, does answer 13.
    /// </summary>
    [Fact]
    public void SeedPool_ExitsZero_BesideAPool_ThoughAUserOutsideEveryCopyExists()
    {
        var summary = Run(rowsAtStart: 30, freeAtStart: 30, free: 50, target: 50, buildFailed: 0, foreignUsers: 1, deleteFailed: 0, ceiling: false);

        PoolExitCodes.ForSeedPool(summary).Should().Be(0);
        PoolExitCodes.From(summary, lowMark: 20).Should().Be(13);
    }

    /// <summary>
    /// CONTROL, for the same reason as <see cref="WithNoSignal_RecycleExitsZero"/>. <c>seed-pool</c>
    /// fills the pool; a pool that was low or empty when it started is why it was run, not a signal:
    /// a one-shot service that exits non-zero stops the stack that waits for it.
    /// </summary>
    [Theory]
    [InlineData(30, 0, 50, 50)] // it was empty, and now it is full
    [InlineData(30, 5, 50, 50)] // it was low
    [InlineData(0, 0, 50, 50)] // the first fill
    public void SeedPool_ExitsZero_WhenThePoolItFoundWasLowOrEmpty(int rowsAtStart, int freeAtStart, int free, int target)
    {
        var summary = Run(rowsAtStart, freeAtStart, free, target, buildFailed: 0, foreignUsers: 0, deleteFailed: 0, ceiling: false);

        PoolExitCodes.ForSeedPool(summary).Should().Be(0);
    }

    /// <summary>
    /// CONTROLS, for the same reason again. Exit 12 needs both halves: a copy that failed AND a
    /// pool that ended short. Each half alone is not a signal.
    /// </summary>
    [Theory]
    //          rows  was free target buildFailed
    [InlineData(30, 30, 50, 50, 1)] // a copy failed, and the target was reached all the same
    [InlineData(30, 30, 49, 50, 0)] // short with no copy failed: a visitor claimed one while the run counted
    public void SeedPool_ExitsZero_UnlessACopyFailedAndThePoolEndedShort(
        int rowsAtStart, int freeAtStart, int free, int target, int buildFailed)
    {
        var summary = Run(rowsAtStart, freeAtStart, free, target, buildFailed, foreignUsers: 0, deleteFailed: 0, ceiling: false);

        PoolExitCodes.ForSeedPool(summary).Should().Be(0);
    }

    [Theory]
    [InlineData(0, "PoolOk")]
    [InlineData(10, "PoolLow")]
    [InlineData(11, "PoolEmpty")]
    [InlineData(12, "TopUpIncomplete")]
    [InlineData(13, "ForeignUsers")]
    [InlineData(14, "DeleteFailed")]
    [InlineData(15, "ClaimCeiling")]
    public void EveryExitCode_HasTheNameTheSummaryLinePrints(int code, string name)
    {
        PoolExitCodes.Name(code).Should().Be(name);
    }

    [Theory]
    [InlineData(1, "1")]
    [InlineData(9, "9")]
    [InlineData(16, "16")]
    public void ACodeThePoolDoesNotDecide_IsPrintedAsItsNumber(int code, string printed)
    {
        // No summary ends with one of these. A name that came back empty would end the line at
        // "result=", which reads as a line cut short.
        PoolExitCodes.Name(code).Should().Be(printed);
    }

    // ── The summary line ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheSummary_IsOneLine_CarryingEveryCountWhateverTheCode()
    {
        var summary = new PoolRunSummary
        {
            Free = 50,
            FreeAtStart = 23,
            Claimed = 7,
            Claims24h = 12,
            ClientsAtCap = 0,
            Seeded = 38,
            DeletedExpired = 4,
            DeletedHardStop = 0,
            DeletedStaleFree = 11,
            DeleteFailed = 0,
            SweptIdempotency = 120,
            SweptGrants = 31,
            Tombstones = 412,
            ForeignUsers = 0,
            Ceiling = false,
            ExitCode = 0,
        };

        summary.ToLine().Should().Be(
            "pool: free=50 was=23 claimed=7 claims24h=12 clientsAtCap=0 seeded=38 "
            + "deleted(expired=4 hardStop=0 staleFree=11 failed=0) swept(idempotency=120 grants=31) "
            + "tombstones=412 foreignUsers=0 ceiling=no result=PoolOk");
    }

    [Fact]
    public void TheSummary_SaysWhenTheCeilingLimitedTheTopUp_AndNamesTheCodeItEndsWith()
    {
        var summary = new PoolRunSummary
        {
            Free = 2,
            FreeAtStart = 0,
            Claimed = 148,
            Claims24h = 148,
            ClientsAtCap = 3,
            Seeded = 2,
            DeleteFailed = 1,
            ForeignUsers = 0,
            Ceiling = true,
            ExitCode = 14,
        };

        var line = summary.ToLine();

        line.Should().Be(
            "pool: free=2 was=0 claimed=148 claims24h=148 clientsAtCap=3 seeded=2 "
            + "deleted(expired=0 hardStop=0 staleFree=0 failed=1) swept(idempotency=0 grants=0) "
            + "tombstones=0 foreignUsers=0 ceiling=yes result=DeleteFailed");
        line.Should().NotContain("\n", "a job's log is read line by line");
    }
}
