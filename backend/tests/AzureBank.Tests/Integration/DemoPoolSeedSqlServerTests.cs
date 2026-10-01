extern alias seeder;

using System.Net;
using System.Text.Json;
using AzureBank.Shared.Constants;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Shared.Options;
using AzureBank.Shared.Services.Implementations;
using AzureBank.Shared.Utilities;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using seeder::AzureBank.Seeder.Seeders;

namespace AzureBank.Tests.Integration;

/// <summary>
/// What <c>seed-pool</c> builds: free demo copies, each a demo user, two contacts, four accounts and
/// two months of history, with no password anywhere.
/// </summary>
/// <remarks>
/// <para>
/// A copy is what a visitor is handed, so every property a visitor would meet is pinned on the rows
/// themselves: the balances the dashboard shows, a ledger that adds up to them, contacts whose
/// handles a transfer accepts, a PIN the API verifies. And the property the pool exists for: a copy
/// nobody has claimed cannot be signed in to, by anyone, with anything.
/// </para>
/// <para>
/// Each test runs the builder from the Seeder's own container on a database of its own
/// (<see cref="DemoPoolDatabase"/>), so "a second run adds nothing" is a statement about two
/// processes and not about one tracked context.
/// </para>
/// </remarks>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class DemoPoolSeedSqlServerTests
{
    // One pool row, three users, three role rows, four accounts, twenty-six ledger rows.
    private const int RowsPerCopy = 37;

    private sealed record Counts(int Copies, int Users, int RoleRows, int Accounts, int LedgerRows)
    {
        public int Total => Copies + Users + RoleRows + Accounts + LedgerRows;
    }

    private static async Task<Counts> CountAsync(DemoPoolDatabase database)
    {
        await using var db = database.NewContext();
        return new Counts(
            await db.Set<DemoCopy>().CountAsync(),
            await db.Users.CountAsync(),
            await db.UserRoles.CountAsync(),
            await db.Accounts.IgnoreQueryFilters().CountAsync(),
            await db.Transactions.IgnoreQueryFilters().CountAsync());
    }

    private static async Task<int> UsersAsync(DemoPoolDatabase database)
    {
        await using var db = database.NewContext();
        return await db.Users.CountAsync();
    }

    [SqlServerFact]
    public async Task SeedPoolThree_BuildsThreeCopiesOfThirtySevenRows_AndASecondRunAddsNone()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();

        var first = await database.SeedPoolAsync(3);

        first.Seeded.Should().Be(3);
        (first.FreeAtStart, first.Free, first.BuildFailed, first.ForeignUsers, first.ExitCode).Should().Be((0, 3, 0, 0, 0));

        var built = await CountAsync(database);
        built.Should().Be(new Counts(Copies: 3, Users: 9, RoleRows: 9, Accounts: 12, LedgerRows: 78));
        built.Total.Should().Be(3 * RowsPerCopy);

        var copies = await database.CopiesAsync();
        copies.Should().OnlyContain(c => c.Users.Count == 3, "a copy is three users");
        copies.SelectMany(c => c.Users).Should().OnlyContain(
            u => u.PasswordHash == null, "a free copy has no password, so nothing can sign in to it");
        copies.Should().OnlyContain(c => c.Row.ClaimedAt == null && c.Row.ClaimId == null && c.Row.DeletedAt == null);
        copies.SelectMany(c => c.Users).Select(u => u.PinHash).Distinct().Should().ContainSingle(
            "one PIN hash is computed for the run and shared by every copy it builds: the PIN is public, and a hash per user would cost nine Argon2id runs here");

        await using (var db = database.NewContext())
        {
            (await db.AuditEvents.CountAsync()).Should().Be(0, "the Seeder holds no audit chain key and writes no audit row");
            (await db.RefreshTokens.CountAsync()).Should().Be(0, "nobody has signed in");
        }

        // The same command again: the pool already holds its three free copies.
        var second = await database.SeedPoolAsync(3);

        second.Seeded.Should().Be(0, "seed-pool tops the pool up to its target; it does not add its target");
        (second.FreeAtStart, second.Free, second.ExitCode).Should().Be((3, 3, 0));
        (await CountAsync(database)).Should().Be(built);

        // And a larger target builds the difference.
        var third = await database.SeedPoolAsync(5);

        third.Seeded.Should().Be(2);
        (await CountAsync(database)).Total.Should().Be(5 * RowsPerCopy);
    }

    [SqlServerFact]
    public async Task WithNoNumber_SeedPoolFillsToTheConfiguredTarget()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();

        var summary = await database.SeedPoolAsync(target: null, settings: new() { ["Demo:Pool:TargetFree"] = "3" });

        (summary.Target, summary.Seeded, summary.Free).Should().Be((3, 3, 3));
        (await CountAsync(database)).Copies.Should().Be(3);
    }

    [SqlServerFact]
    public async Task ACopy_IsJohnWithTwoAccounts_AndTwoContactsHeCanPay()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        await using (var db = database.NewContext())
        {
            (await db.Roles.CountAsync()).Should().Be(0, "ARRANGE: a migrated database has no role, and seed-pool must not need `seed` to have run");
        }

        var before = DateTime.UtcNow;
        var summary = await database.SeedPoolAsync(1);
        var after = DateTime.UtcNow;

        summary.Seeded.Should().Be(1);
        var copy = (await database.CopiesAsync()).Should().ContainSingle().Which;

        // The pool row: free, unused, and owned by the demo user. Its id is minted by the Seeder as
        // every other id here is: a version 7 UUID.
        copy.Id.Version.Should().Be(7);
        copy.Row.CreatedAt.Should().BeOnOrAfter(before.AddSeconds(-1)).And.BeOnOrBefore(after.AddSeconds(1));
        copy.Row.ClaimedAt.Should().BeNull();
        copy.Row.ClaimId.Should().BeNull();
        copy.Row.ClientKey.Should().BeNull();
        copy.Row.Writes.Should().Be(0);
        copy.Row.DeletedAt.Should().BeNull();
        copy.Users.Should().HaveCount(3);
        copy.Owner.AzureTag.Should().StartWith("john_", "the pool row's owner is the demo user");

        // The cast, with handles that share the copy's suffix and addresses nobody can guess.
        var suffix = copy.Owner.AzureTag["john_".Length..];
        suffix.Should().MatchRegex("^[a-z0-9]{4}$");
        (copy.Owner.FirstName, copy.Owner.LastName, copy.Owner.AzureTag).Should().Be(("John", "Smith", $"john_{suffix}"));
        (copy.Jane.FirstName, copy.Jane.LastName, copy.Jane.AzureTag).Should().Be(("Jane", "Smith", $"jane_{suffix}"));
        (copy.Mike.FirstName, copy.Mike.LastName, copy.Mike.AzureTag).Should().Be(("Mike", "Brown", $"mike_{suffix}"));
        copy.Owner.Email.Should().MatchRegex(@"^demo-[a-z0-9]{16}@azurebank\.example$");
        copy.Jane.Email.Should().MatchRegex(@"^contact-[a-z0-9]{16}@azurebank\.example$");
        copy.Mike.Email.Should().MatchRegex(@"^contact-[a-z0-9]{16}@azurebank\.example$");
        copy.Users.Select(u => u.Email).Should().OnlyHaveUniqueItems();

        // Created through Identity, as registration creates a user: found by email, and by nothing else.
        var hasher = new PasswordHasher(new PinHashingOptions { PinPepper = CustomWebApplicationFactory.PinPepper });
        foreach (var user in copy.Users)
        {
            user.DemoCopyId.Should().Be(copy.Id);
            user.EmailConfirmed.Should().BeTrue();
            user.NormalizedEmail.Should().Be(user.Email!.ToUpperInvariant());
            user.UserName.Should().Be(user.Id.ToString(), "the user name is the immutable id, never the handle");
            user.SecurityStamp.Should().NotBeNullOrEmpty();
            user.PasswordHash.Should().BeNull();
            hasher.VerifyPin(user.PinHash!, "123456").Should().BeTrue("every user of a copy has the demo PIN, under the API's pepper");
        }

        copy.Users.Select(u => u.PinHash).Distinct().Should().ContainSingle("one PIN hash is computed for the run and shared");

        await using (var db = database.NewContext())
        {
            // The roles, and the User role on each of the three.
            (await db.Roles.Select(r => r.Name).ToListAsync()).Should().BeEquivalentTo(Roles.All);
            var userRole = await db.Roles.Where(r => r.Name == Roles.User).Select(r => r.Id).SingleAsync();
            (await db.UserRoles.Where(r => r.RoleId == userRole).Select(r => r.UserId).ToListAsync())
                .Should().BeEquivalentTo(copy.UserIds);

            // The accounts, with the balances the dashboard opens on.
            var accounts = await db.Accounts.AsNoTracking().OrderBy(a => a.Balance).ToListAsync();
            accounts.Select(a => (a.UserId, a.Name, a.Type, a.Balance, a.IsPrimary)).Should().Equal(
                (copy.Owner.Id, "Checking", AccountType.Checking, 2300.00m, false),
                (copy.Jane.Id, "Personal Savings", AccountType.Savings, 8500.00m, true),
                (copy.Owner.Id, "Main Savings", AccountType.Savings, 12450.00m, true),
                (copy.Mike.Id, "Investment Account", AccountType.Investment, 25000.00m, true));
            accounts.Select(a => a.AccountNumber).Should().OnlyHaveUniqueItems()
                .And.AllSatisfy(number => number.Should().MatchRegex(@"^AB-\d{4}-\d{4}-\d{2}$"));
        }
    }

    [SqlServerFact]
    public async Task ACopysLedger_IsTheDemoLedger_DatedFromTheCopysSeedInstant_AndStaysInsideTheCopy()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();

        // Two copies, so "inside the copy" can be told from "inside the database".
        (await database.SeedPoolAsync(2)).Seeded.Should().Be(2);

        var copies = await database.CopiesAsync();
        await using var db = database.NewContext();
        var accounts = await db.Accounts.AsNoTracking().ToDictionaryAsync(a => a.Id);
        var rows = await db.Transactions.AsNoTracking().ToListAsync();
        var byId = rows.ToDictionary(t => t.Id);
        rows.Should().HaveCount(52);

        foreach (var copy in copies)
        {
            var handles = copy.Users.ToDictionary(u => u.Id, u => u.AzureTag);
            var mine = rows.Where(t => handles.ContainsKey(accounts[t.AccountId].UserId)).ToList();
            mine.Should().HaveCount(26, "fourteen movements and six transfers of two rows each");
            mine.Should().OnlyContain(t => IdGenerator.IsValidTransactionNumber(t.TransactionNumber));

            // The rows themselves, not only how many: which account, how long before the seed
            // instant, what moved, how much, and the words the history shows.
            mine.Select(t => new ExpectedDemoLedger.Row(
                    RoleOf(accounts[t.AccountId], copy),
                    WholeHours(copy.Row.CreatedAt - t.CreatedAt),
                    t.Type,
                    t.Amount,
                    t.Description))
                .Should().BeEquivalentTo(
                    ExpectedDemoLedger.Rows, "a visitor's copy opens on the demo's own history, row for row");

            // Every account's history ends at the balance the account holds.
            foreach (var chain in mine.GroupBy(t => t.AccountId))
            {
                var ordered = chain.OrderBy(t => t.CreatedAt).ThenBy(t => t.Id).ToList();
                for (var i = 1; i < ordered.Count; i++)
                {
                    ordered[i].BalanceBefore.Should().Be(ordered[i - 1].BalanceAfter, "each row starts where the one before it ended");
                }

                ordered[^1].BalanceAfter.Should().Be(accounts[chain.Key].Balance, "the newest row ends at the account's balance");
            }

            // The dates are offsets from the instant the pool row carries.
            var dated = mine.OrderBy(t => t.CreatedAt).ToList();
            dated[^1].CreatedAt.Should().BeCloseTo(copy.Row.CreatedAt, TimeSpan.FromSeconds(1), "the newest row is the seed instant");
            dated[0].CreatedAt.Should().BeCloseTo(
                copy.Row.CreatedAt - TimeSpan.FromDays(63) - TimeSpan.FromHours(4), TimeSpan.FromSeconds(1), "the oldest is the first salary");

            // Every transfer is two rows linked to each other, both inside this copy.
            var outgoing = mine.Where(t => t.Type == TransactionType.TransferOut).ToList();
            outgoing.Should().HaveCount(6);
            foreach (var sent in outgoing)
            {
                sent.RelatedTransactionId.Should().NotBeNull();
                var received = byId[sent.RelatedTransactionId!.Value];
                received.RelatedTransactionId.Should().Be(sent.Id, "the link runs both ways");
                received.CreatedAt.Should().Be(sent.CreatedAt, "one transfer, one instant");

                var from = accounts[sent.AccountId].UserId;
                var to = accounts[received.AccountId].UserId;
                handles.Should().ContainKey(to, "a copy's money never leaves the copy: the delete of one copy relies on it");
                if (from != to)
                {
                    sent.RecipientAzureTag.Should().Be(handles[to]);
                    received.SenderAzureTag.Should().Be(handles[from]);
                }
            }
        }

        // Two copies share the cast and nothing that identifies a row.
        copies.SelectMany(c => c.Users).Select(u => u.AzureTag).Should().OnlyHaveUniqueItems();
        accounts.Values.Select(a => a.AccountNumber).Should().OnlyHaveUniqueItems();
    }

    /// <summary>Which of the ledger's four accounts this account of <paramref name="copy"/> is.</summary>
    private static DemoLedgerAccount RoleOf(Account account, BuiltCopy copy) =>
        account.UserId == copy.Jane.Id ? DemoLedgerAccount.JaneSavings
        : account.UserId == copy.Mike.Id ? DemoLedgerAccount.MikeInvestment
        : account.IsPrimary ? DemoLedgerAccount.OwnerSavings
        : DemoLedgerAccount.OwnerChecking;

    /// <summary>
    /// An offset to the hour. Every row of the ledger is a whole number of hours before the seed
    /// instant; the pool row and the ledger may read the clock a moment apart.
    /// </summary>
    private static TimeSpan WholeHours(TimeSpan offset) => TimeSpan.FromHours(Math.Round(offset.TotalHours));

    [SqlServerFact]
    public async Task AnAccountNumberThatCollides_RetriesTheWholeCopy_AndLeavesNoHalfCopy()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        await database.BuildCopiesAsync(1);
        string taken;
        await using (var db = database.NewContext())
        {
            taken = await db.Accounts.Select(a => a.AccountNumber).FirstAsync();
        }

        // The second copy's first account number is rewritten to one the first copy holds: SQL
        // Server raises the real 2601, after the pool row and the three users were already sent.
        var collision = new CollidingAccountNumberInterceptor(taken);
        var summary = await database.SeedPoolAsync(2, interceptors: collision);

        collision.Fired.Should().BeTrue("the collision must actually have been injected, else the test proves nothing");
        (summary.Seeded, summary.BuildFailed, summary.Free, summary.ExitCode).Should().Be(
            (1, 0, 2, 0), "a collision is retried with fresh values, not counted as a failure");

        (await CountAsync(database)).Should().Be(
            new Counts(Copies: 2, Users: 6, RoleRows: 6, Accounts: 8, LedgerRows: 52),
            "the attempt that collided rolled back whole: no pool row, no user and no account of it is left");
        (await database.CopiesAsync()).Should().OnlyContain(c => c.Users.Count == 3);
    }

    [SqlServerFact]
    public async Task WhenEveryAttemptCollides_ACopyIsGivenUpAfterThreeRetries()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        await database.BuildCopiesAsync(1);
        string taken;
        await using (var db = database.NewContext())
        {
            taken = await db.Accounts.Select(a => a.AccountNumber).FirstAsync();
        }

        // Armed for good: every attempt of every copy collides, so the retries must end somewhere.
        var collision = new CollidingAccountNumberInterceptor(taken, everyTime: true);
        var summary = await database.SeedPoolAsync(2, interceptors: collision);

        (summary.Seeded, summary.BuildFailed, summary.ExitCode).Should().Be(
            (0, 3, 12), "three copies in a row could not be built, and the run stops there");
        collision.Collisions.Should().Be(12, "each of the three copies is tried once and retried three times");
        summary.Failures.Should().HaveCount(3).And.OnlyContain(f => f.ErrorNumber == 2601, "the failure is the database's, and it is named");
        (await CountAsync(database)).Should().Be(
            new Counts(Copies: 1, Users: 3, RoleRows: 3, Accounts: 4, LedgerRows: 26), "no attempt left anything behind");
    }

    [SqlServerFact]
    public async Task ATransientFaultMidCopy_IsRetried_AndLeavesExactlyOneCopy()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();

        // The copy is built inside the retrying execution strategy, which re-runs the whole
        // attempt on the same context. The fault lands on the ledger insert, after the pool row,
        // the users and the accounts of the first attempt were sent.
        var transient = new TransientFailureInterceptor("INSERT INTO [Transactions]");
        var summary = await database.SeedPoolAsync(1, interceptors: transient);

        transient.Fired.Should().BeTrue("the transient fault must actually have been injected, else the test proves nothing");
        (summary.Seeded, summary.BuildFailed, summary.ExitCode).Should().Be((1, 0, 0));
        (await CountAsync(database)).Should().Be(
            new Counts(Copies: 1, Users: 3, RoleRows: 3, Accounts: 4, LedgerRows: 26),
            "the retry replaces the failed attempt; it does not add a second batch beside it");
    }

    [SqlServerFact]
    public async Task WhenTheAnswerToACopysCommitIsLost_TheCopyIsBuiltOnce_AndIsNotAFailure()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();

        // The copy commits, and the answer to the commit never arrives: the database holds the
        // copy, and the builder sees a fault it would retry. Before it does, it has to ask the
        // database whether this copy is already there.
        var lost = new TransferTransientFault(TransferFaultMode.AfterCommit);
        lost.Arm();
        var summary = await database.SeedPoolAsync(
            1, interceptors: [new TransferCommandFaultInterceptor(lost), new TransferCommitFaultInterceptor(lost)]);

        lost.Fired.Should().BeTrue("the acknowledgement must actually have been lost, else the test proves nothing");
        (summary.Seeded, summary.BuildFailed, summary.Free, summary.ExitCode).Should().Be(
            (1, 0, 1, 0), "the copy landed: it is counted once, and it is not a failure");
        summary.Failures.Should().BeEmpty();
        (await CountAsync(database)).Should().Be(
            new Counts(Copies: 1, Users: 3, RoleRows: 3, Accounts: 4, LedgerRows: 26),
            "a builder that did not ask would run the attempt again, fail it on the copy's own row, and build a second copy beside the first");
    }

    [SqlServerFact]
    public async Task WhenIdentityRefusesAUserItsRole_TheCopyIsNotBuilt_AndNothingOfItIsLeft()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();

        // AddToRoleAsync answers a failure here and throws nothing, so only a builder that reads
        // the answer knows the role was not given.
        var refusal = new RefusingAfterCreationUserValidator();
        database.AlsoRegister = services => services.AddSingleton<IUserValidator<ApplicationUser>>(refusal);

        var summary = await database.SeedPoolAsync(2);

        refusal.Refusals.Should().BeGreaterThan(0, "the refusal must actually have been injected, else the test proves nothing");
        (summary.Seeded, summary.BuildFailed, summary.Free, summary.ExitCode).Should().Be(
            (0, 3, 0, 12), "a user without its role is not a copy: each attempt fails, and the run stops at three in a row");
        summary.Failures.Should().HaveCount(3).And.OnlyContain(
            f => f.Message.Contains(RefusingAfterCreationUserValidator.Code), "the failure says what Identity answered");
        (await CountAsync(database)).Total.Should().Be(0, "the attempt rolled back whole: no user is left without a role, and no role row without a copy");

        // CONTROL: the same run without the refusal builds, so the failures were the refusal's.
        database.AlsoRegister = null;
        (await database.SeedPoolAsync(2)).Seeded.Should().Be(2);
    }

    [SqlServerFact]
    public async Task WhenNoCopyCanBeBuilt_SeedPoolStopsAfterThreeFailuresInARow_AndExitsTwelve()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();

        var refused = FailingCommandInterceptor.OnText("INSERT INTO [DemoCopies]");
        var summary = await database.SeedPoolAsync(5, interceptors: refused);

        refused.Failures.Should().Be(3, "each copy is tried once, and the run gives up after three failures in a row");
        (summary.Seeded, summary.BuildFailed, summary.Free, summary.Target).Should().Be((0, 3, 0, 5));
        summary.Failures.Should().HaveCount(3).And.OnlyContain(f => f.Message.Contains(FailingCommandInterceptor.Message));
        summary.ExitCode.Should().Be(12, "a copy build failed and the pool is below its target");
        (await CountAsync(database)).Total.Should().Be(0, "a copy that could not be built leaves nothing behind");
    }

    [SqlServerFact]
    public async Task AFreeCopyTooOldToHandOut_DoesNotCountTowardsTheTarget()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var copies = await database.BuildCopiesAsync(2);

        // Older than the 44 hours a free copy stays fresh: its history would open days in the past.
        await database.BackdateSeedAsync(copies[0].Id, DateTime.UtcNow.AddHours(-45));

        var summary = await database.SeedPoolAsync(2);

        (summary.FreeAtStart, summary.Seeded, summary.Free).Should().Be((1, 1, 2));
        (await CountAsync(database)).Copies.Should().Be(3, "the stale copy stays until recycle deletes it");
    }

    [SqlServerFact]
    public async Task HowLongAFreeCopyCounts_IsTheConfiguredAge()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var copies = await database.BuildCopiesAsync(2);
        await database.BackdateSeedAsync(copies[0].Id, DateTime.UtcNow.AddHours(-11));

        // CONTROL: 11 hours of the default 44. Both copies count, and there is nothing to build.
        var withTheDefault = await database.SeedPoolAsync(2);

        (withTheDefault.FreeAtStart, withTheDefault.Seeded, withTheDefault.Free).Should().Be((2, 0, 2));

        // 11 hours of 10: the older copy no longer counts, and one is built in its place.
        var summary = await database.SeedPoolAsync(2, settings: new() { ["Demo:Pool:MaxFreeAgeHours"] = "10" });

        (summary.FreeAtStart, summary.Seeded, summary.Free).Should().Be((1, 1, 2));
        (await CountAsync(database)).Copies.Should().Be(3);
    }

    [SqlServerFact]
    public async Task AFreeCopysOwner_CannotBeSignedInTo_WithAnyPassword()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var copy = (await database.BuildCopiesAsync(1)).Single();
        using var client = database.Api().CreateClient();

        // The seed's public password, a password built from the public PIN, and the one a test
        // gives a claimed copy: none of them, because there is no hash to match.
        foreach (var password in new[] { "Test123!", "Pin-123456", DemoPoolDatabase.VisitorPassword })
        {
            using var response = await DemoVisitor.TrySignInAsync(client, copy.Owner.Email!, password);

            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            var problem = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
            problem.GetProperty("errorCode").GetString().Should().Be(ErrorCodes.InvalidCredentials);
        }

        await using (var db = database.NewContext())
        {
            (await db.RefreshTokens.CountAsync()).Should().Be(0, "no grant was issued");
        }

        // TWIN: the same request signs in once the owner has a password, so the refusals above were
        // about the missing password and not about a request the API would refuse anyway.
        await database.GiveOwnerAPasswordAsync(copy);
        using var signedIn = await DemoVisitor.TrySignInAsync(client, copy.Owner.Email!, DemoPoolDatabase.VisitorPassword);
        signedIn.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [SqlServerFact]
    public async Task WithTheDemoOff_SeedPoolRefuses_AndWritesNothing()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();

        var act = () => database.SeedPoolAsync(3, settings: new() { ["Demo:Enabled"] = "false" });

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*Demo:Enabled*");
        (await CountAsync(database)).Total.Should().Be(0);

        // CONTROL: the same call with the demo on builds, so the refusal was the flag's.
        (await database.SeedPoolAsync(3)).Seeded.Should().Be(3);
    }

    [SqlServerFact]
    public async Task OnADatabaseWithUsersAndNoPool_SeedPoolRefusesBeforeItWritesAnything()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();

        // The signature of the wrong database: somebody's users, and not one pool row.
        using var client = database.Api().CreateClient();
        await DemoVisitor.RegisterAsync(client, "resident");
        (await UsersAsync(database)).Should().Be(1, "ARRANGE: one user outside every copy");

        var summary = await database.SeedPoolAsync(2);

        (summary.ForeignUsers, summary.Seeded, summary.ExitCode).Should().Be((1, 0, 13));
        var counts = await CountAsync(database);
        (counts.Copies, counts.Users).Should().Be((0, 1), "150 users were about to be written beside real ones, and none was");
    }
}
