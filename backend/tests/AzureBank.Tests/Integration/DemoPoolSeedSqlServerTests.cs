extern alias seeder;

using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Text.Json;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Shared.Options;
using AzureBank.Shared.Services.Implementations;
using AzureBank.Shared.Utilities;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using seeder::AzureBank.Seeder.Pool;
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
        var first = (await database.BuildCopiesAsync(1)).Single();
        string taken;
        await using (var db = database.NewContext())
        {
            taken = await db.Accounts.Select(a => a.AccountNumber).FirstAsync();
        }

        var log = new RecordingLoggerProvider();
        database.Log = log;

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

        // The retry is written down, once, as a warning: by the id of the copy, which is the one
        // the second attempt then built, and with the database's number. That line is the
        // builder's, and says what the run did. What the database said is in the log already: EF
        // writes the save that failed at Error, with SQL Server's message, and that message names
        // the value it refused. So the warning does not carry the exception a second time.
        var built = (await database.CopiesAsync()).Single(c => c.Id != first.Id);
        var lines = log.Lines;
        lines
            .Where(line => line.Level == LogLevel.Warning && line.Message.Contains(built.Id.ToString(), StringComparison.Ordinal))
            .Should().ContainSingle("a copy that collided keeps its id, and the collision is one line in the job's log")
            .Which.Message.Should().Contain("2601").And.NotContain(taken, "the database's message is EF's line, and is not written twice");
        lines.Should().Contain(
            line => line.Level == LogLevel.Error && line.Message.Contains(taken, StringComparison.Ordinal),
            "the database's message is in the log, value included, so the warning loses nothing by leaving it out");
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

    /// <summary>
    /// Refuses a user the first time Identity validates it, which is when it is created, and never
    /// again: the mirror of <see cref="RefusingAfterCreationUserValidator"/>.
    /// </summary>
    /// <remarks>
    /// The description quotes the address, as Identity's own "Email '…' is already taken" does. The
    /// code does not.
    /// </remarks>
    private sealed class RefusingAtCreationUserValidator : IUserValidator<ApplicationUser>
    {
        public const string Code = "InjectedCreationRefusal";

        private readonly ConcurrentDictionary<Guid, int> _validations = new();
        private int _refusals;

        /// <summary>Validations this validator refused. 0 means the test proved nothing.</summary>
        public int Refusals => Volatile.Read(ref _refusals);

        public Task<IdentityResult> ValidateAsync(UserManager<ApplicationUser> manager, ApplicationUser user)
        {
            if (_validations.AddOrUpdate(user.Id, 1, (_, seen) => seen + 1) > 1)
            {
                return Task.FromResult(IdentityResult.Success);
            }

            Interlocked.Increment(ref _refusals);
            return Task.FromResult(IdentityResult.Failed(
                new IdentityError { Code = Code, Description = $"Identity would not create {user.Email}." }));
        }
    }

    [SqlServerFact]
    public async Task WhenIdentityRefusesToCreateAUser_TheFailureNamesIdentitysCode_AndNeverTheAddress()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();

        // CreateAsync answers a failure here and throws nothing. A builder that did not read the
        // answer would go on to give a role to a user that was never written, and report whatever
        // that ran into.
        var refusal = new RefusingAtCreationUserValidator();
        database.AlsoRegister = services => services.AddSingleton<IUserValidator<ApplicationUser>>(refusal);

        var summary = await database.SeedPoolAsync(2);

        refusal.Refusals.Should().Be(3, "each copy stops at its first user, and the run stops at three copies in a row");
        (summary.Seeded, summary.BuildFailed, summary.Free, summary.ExitCode).Should().Be((0, 3, 0, 12));
        summary.Failures.Should().HaveCount(3).And.OnlyContain(
            f => f.Message.Contains(RefusingAtCreationUserValidator.Code), "the failure says what Identity answered when the user was created");
        summary.Failures.Should().OnlyContain(
            f => !f.Message.Contains('@'),
            "a failure's message is logged, so it carries Identity's code and never its description, which can quote the address");
        (await CountAsync(database)).Total.Should().Be(0, "the attempt rolled back whole");
    }

    /// <summary>
    /// Another run of the Seeder, at the worst moment: when this run sends the insert of a role it
    /// looked for and did not find, the same role is inserted first, through a context of its own.
    /// </summary>
    /// <remarks>
    /// SQL Server then raises the real 2601 against the real <c>RoleNameIndex</c>, as it does for
    /// the run that loses the race. Only for the roles the test names, and once for each: the next
    /// time this run looks for that role, it is there.
    /// </remarks>
    private sealed class RoleCreatedFirstInterceptor(
        Func<AzureBankDbContext> anotherRun, IReadOnlyCollection<string> roles) : DbCommandInterceptor
    {
        private readonly ConcurrentDictionary<string, bool> _created = new();

        /// <summary>The roles created ahead of the run. Empty means the test proved nothing.</summary>
        public IReadOnlyCollection<string> Created => [.. _created.Keys];

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            await CreateFirstAsync(command, cancellationToken);
            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            await CreateFirstAsync(command, cancellationToken);
            return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private async Task CreateFirstAsync(DbCommand command, CancellationToken cancellationToken)
        {
            if (!command.CommandText.Contains("INSERT INTO [AspNetRoles]", StringComparison.Ordinal))
            {
                return;
            }

            // By value, not by parameter name: EF names insert parameters by position.
            var name = command.Parameters.Cast<DbParameter>()
                .Select(parameter => parameter.Value as string)
                .FirstOrDefault(value => value is not null && roles.Contains(value));
            if (name is null || !_created.TryAdd(name, true))
            {
                return;
            }

            await using var db = anotherRun();
            db.Roles.Add(new IdentityRole<Guid> { Id = Guid.CreateVersion7(), Name = name, NormalizedName = name.ToUpperInvariant() });
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// A seed-pool and a scheduled recycle that start together on a database that was only
    /// migrated: both look for a role, both find none, and this run's insert arrives second.
    /// </summary>
    /// <remarks>
    /// Two cases, because each stops a different mistake. With ONE role lost, this run goes on to
    /// create the other one itself: the row it could not write must be off the context by then, or
    /// that save sends it again. With BOTH lost, it has to look a third time, which is as often as
    /// it can happen to one run.
    /// </remarks>
    [SqlServerTheory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task WhenAnotherRunCreatesARoleFirst_ThisRunLooksAgain_AndBuildsItsCopies(int rolesLost)
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var log = new RecordingLoggerProvider();
        database.Log = log;

        var lost = Roles.All.Take(rolesLost).ToArray();
        var race = new RoleCreatedFirstInterceptor(database.NewContext, lost);
        var summary = await database.SeedPoolAsync(2, interceptors: race);

        race.Created.Should().BeEquivalentTo(
            lost, "the race must actually have been lost, for each of these roles, else the test proves nothing");
        (summary.Seeded, summary.BuildFailed, summary.Free, summary.ExitCode).Should().Be(
            (2, 0, 2, 0), "the run lost a race and nothing else: the roles are there, which is all it needed of them");
        (await CountAsync(database)).Should().Be(new Counts(Copies: 2, Users: 6, RoleRows: 6, Accounts: 8, LedgerRows: 52));

        await using (var db = database.NewContext())
        {
            (await db.Roles.Select(r => r.Name).ToListAsync()).Should().BeEquivalentTo(
                Roles.All, "each role once: the row this run could not write is not sent again with a later save");
        }

        // Each lost race is one warning under EF's own error line, so that line is not read as a
        // failure of the run.
        log.Lines
            .Where(line => line.Level == LogLevel.Warning
                && line.Message.Contains("role", StringComparison.OrdinalIgnoreCase)
                && line.Message.Contains("2601", StringComparison.Ordinal))
            .Should().HaveCount(rolesLost, "one line for each role another run created first");
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
    public async Task FailuresThatAreNotInARow_DoNotEndTheRun_AndEachIsLoggedByItsCopysId()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var log = new RecordingLoggerProvider();
        database.Log = log;

        // Of the copies the run tries, the first two fail, the third is built, the next two fail and
        // the rest are built: four failures, and never three in a row.
        var refused = FailingCommandInterceptor.OnTurnsOfText(turn => turn is 1 or 2 or 4 or 5, "INSERT INTO [DemoCopies]");
        var summary = await database.SeedPoolAsync(4, interceptors: refused);

        refused.Failures.Should().Be(4, "the failures must actually have been injected, else the test proves nothing");
        (summary.Seeded, summary.BuildFailed, summary.Free, summary.ExitCode).Should().Be(
            (4, 4, 4, 0),
            "a copy that is built ends the run of failures before it; and a pool that reached its target is not a signal, whatever failed on the way");

        // Each copy that failed is named by its id, in the summary and in the log: the id is all
        // that is left of it, so it is all an operator has to look for.
        summary.Failures.Should().HaveCount(4);
        summary.Failures.Select(f => f.CopyId).Should().OnlyHaveUniqueItems();
        foreach (var failure in summary.Failures)
        {
            failure.CopyId.Version.Should().Be(7);
            log.Lines.Should().Contain(
                line => line.Level == LogLevel.Error && line.Message.Contains(failure.CopyId.ToString(), StringComparison.Ordinal),
                "the run writes down each copy it could not build");
        }

        (await database.CopiesAsync()).Select(c => c.Id).Should().NotIntersectWith(
            summary.Failures.Select(f => f.CopyId), "a copy that failed is not in the pool");
    }

    [SqlServerFact]
    public async Task ARunThatIsStopped_WhileACopyIsBeingWritten_Ends_AndBlamesNoCopy()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var log = new RecordingLoggerProvider();
        database.Log = log;

        // Another session holds the ledger's table, so the copy's ledger rows are sent and then
        // wait for it. The job is stopped while they wait: the stop reaches a statement that is
        // already with the database.
        using var stop = new CancellationTokenSource();
        Exception ended;
        await using (var held = await database.HoldAsync("SELECT COUNT(*) FROM [Transactions] WITH (TABLOCKX, HOLDLOCK)"))
        {
            var run = database.SeedPoolAsync(2, stop: stop.Token);
            await held.AStatementWaitsAsync();
            await stop.CancelAsync();

            ended = (await FluentActions.Awaiting(() => run).Should().ThrowAsync<Exception>(
                "a run that is stopped ends: it prints no summary, and the next run tops the pool up from the rows as they are")).Which;
        }

        log.Lines.Should().NotContain(
            line => line.Level == LogLevel.Error && line.Message.Contains("could not be built", StringComparison.Ordinal),
            "being stopped is nothing a copy did: it is not written down as a copy that could not be built");
        ended.GetBaseException().Should().BeOfType<SqlException>(
            "ARRANGE: a stop that reaches a statement on the server comes back as the database's own error, not as a cancellation");
        (await CountAsync(database)).Total.Should().Be(0, "the copy that was under way rolled back whole");
    }

    [SqlServerFact]
    public async Task ACopyThatFailed_LeavesNothingForALaterSaveToWrite()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();

        // The fault lands on the ledger insert: the attempt's pool row, users and accounts were sent
        // and rolled back, and its ledger rows never left the context.
        var refused = FailingCommandInterceptor.OnText("INSERT INTO [Transactions]");
        using var scope = database.Seeder(interceptors: refused).CreateScope();
        var summary = await scope.ServiceProvider.GetRequiredService<DemoCopyBuilder>().SeedPoolAsync(1);

        refused.Failures.Should().Be(3, "the failure must actually have been injected, else the test proves nothing");
        (summary.Seeded, summary.BuildFailed).Should().Be((0, 3));

        // The context is the scope's, and `recycle` goes on using it after its top-up. Rows still
        // tracked there would be written by the next save, outside the transaction that failed.
        var context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        context.ChangeTracker.Entries().Should().BeEmpty("the rows of an attempt that failed are not left waiting on the context");
        (await context.SaveChangesAsync()).Should().Be(0);
        (await CountAsync(database)).Total.Should().Be(0, "a copy that could not be built leaves nothing behind, now or at the next save");
    }

    [SqlServerFact]
    public async Task ACopyThatWasBuilt_IsNotKeptOnTheContext_SoALaterReadAnswersFromTheDatabase()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        using var scope = database.Seeder().CreateScope();
        var summary = await scope.ServiceProvider.GetRequiredService<DemoCopyBuilder>().SeedPoolAsync(1);

        summary.Seeded.Should().Be(1, "ARRANGE: the run built a copy");

        // The context is the scope's, and `recycle` goes on using it after its top-up. The rows of
        // the copy it just built, still tracked there, would answer a later read in place of the
        // database: as they were written, whatever has happened to the copy since.
        var context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        context.ChangeTracker.Entries().Should().BeEmpty("the rows of a copy that was built are not kept on the context");

        // A visitor claims the copy, on a connection of their own, and the run's context is asked.
        var copy = (await database.CopiesAsync()).Should().ContainSingle().Which;
        await database.MarkClaimedAsync(copy.Id, DateTime.UtcNow);

        var asTheRunReadsIt = await context.Set<DemoCopy>().SingleAsync(c => c.Id == copy.Id);
        asTheRunReadsIt.ClaimedAt.Should().NotBeNull(
            "the run reads the copy as the database holds it, claimed, and not as it wrote it, free");
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

    // ── What a run writes down ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// The lines that carry an address on the demo's domain, or the random part of one of
    /// <paramref name="emails"/>, whatever its case: Identity stores an address a second time in
    /// upper case.
    /// </summary>
    private static List<string> LinesNaming(
        IEnumerable<(LogLevel Level, string Message)> lines, IReadOnlyCollection<string> emails) =>
        [.. lines
            .Where(line => line.Message.Contains($"@{DemoCredentials.EmailDomain}", StringComparison.OrdinalIgnoreCase)
                || emails.Any(email => line.Message.Contains(email[..email.IndexOf('@')], StringComparison.OrdinalIgnoreCase)))
            .Select(line => $"{line.Level}: {line.Message}")];

    /// <summary>
    /// A copy's email address is the name its visitor signs in with, and a job's log is kept
    /// somewhere else, for longer, and read by other people than the database is. A run names a
    /// copy by its id.
    /// </summary>
    /// <remarks>
    /// The recorder takes every line of every category at every level, with the text of any
    /// exception attached: the builder's own lines, Identity's, and EF's. Two runs, so that each of
    /// the builder's log statements has a reason to speak and each way an address could reach the
    /// log is taken: Identity refuses a user with a description that quotes the address, the
    /// database refuses the insert of a user, a copy collides and is drawn again, copies are built.
    /// The environment is Production, as a deployment's is; the test below is the other one.
    /// </remarks>
    [SqlServerFact]
    public async Task ARun_NeverWritesACopysEmailAddressToItsLog()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var log = new RecordingLoggerProvider();
        database.Log = log;
        await database.BuildCopiesAsync(1);
        string taken;
        await using (var db = database.NewContext())
        {
            taken = await db.Accounts.Select(a => a.AccountNumber).FirstAsync();
        }

        // Identity answers a refusal whose description quotes the address it refused.
        var refusal = new RefusingAtCreationUserValidator();
        database.AlsoRegister = services => services.AddSingleton<IUserValidator<ApplicationUser>>(refusal);
        var refused = await database.SeedPoolAsync(2);
        database.AlsoRegister = null;

        // Then the database refuses the first insert of a user, and the copy after it collides.
        var failing = FailingCommandInterceptor.OnTurnsOfText(turn => turn == 1, "INSERT INTO [AspNetUsers]");
        var collision = new CollidingAccountNumberInterceptor(taken);
        var built = await database.SeedPoolAsync(3, interceptors: [failing, collision]);

        (refusal.Refusals, failing.Failures, collision.Fired).Should().Be(
            (3, 1, true), "ARRANGE: every fault was actually injected");
        (refused.Seeded, refused.BuildFailed).Should().Be((0, 3), "ARRANGE: Identity refused each copy's first user");
        (built.Seeded, built.BuildFailed, built.Free).Should().Be(
            (2, 1, 3), "ARRANGE: one copy failed on its first user, one collided and was drawn again, two were built");

        var lines = log.Lines;
        lines.Should().Contain(
            line => line.Message.Contains("[AspNetUsers]", StringComparison.Ordinal),
            "the recorder saw the statements that carried the addresses: a recorder that heard nothing reports clean for ever");
        foreach (var failure in refused.Failures.Concat(built.Failures))
        {
            lines.Should().Contain(
                line => line.Level == LogLevel.Error && line.Message.Contains(failure.CopyId.ToString(), StringComparison.Ordinal),
                "ARRANGE: the builder wrote down each copy it could not build");
        }

        lines.Should().Contain(
            line => line.Level == LogLevel.Warning && line.Message.Contains("2601", StringComparison.Ordinal),
            "ARRANGE: the builder wrote down the collision");

        var everyCopy = (await database.CopiesAsync()).SelectMany(c => c.Users).Select(u => u.Email!).ToList();
        everyCopy.Should().HaveCount(9, "ARRANGE: three copies, three users each");
        LinesNaming(lines, everyCopy).Should().BeEmpty(
            "a run names a copy by its id, never by the address its visitor signs in with");
    }

    /// <summary>
    /// In Development EF logs a statement with the values it carried, so there a statement that
    /// fails puts an address in the log. It is the address of an attempt that rolled back, and it
    /// must not also be the address of a copy that exists.
    /// </summary>
    [SqlServerFact]
    public async Task WhereAStatementIsLoggedWithItsValues_AnAttemptThatIsRunAgainDrawsNewAddresses()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var log = new RecordingLoggerProvider();
        database.Log = log;
        database.EnvironmentName = Environments.Development;

        // A transient fault on the insert of the copy's first user: the strategy runs the whole
        // attempt again, on the same context.
        var transient = new TransientFailureInterceptor("INSERT INTO [AspNetUsers]");
        var summary = await database.SeedPoolAsync(1, interceptors: transient);

        transient.Fired.Should().BeTrue("the transient fault must actually have been injected, else the test proves nothing");
        (summary.Seeded, summary.BuildFailed).Should().Be((1, 0));

        // What the Seeder prints: its appsettings.json holds EF and Identity at Warning. Below
        // that, in Development, every statement that ran is written with its values, and the
        // statements that built the copy are among them.
        var printed = log.Lines.Where(line => line.Level >= LogLevel.Warning).ToList();
        printed.Should().Contain(
            line => line.Message.Contains($"@{DemoCredentials.EmailDomain}", StringComparison.OrdinalIgnoreCase),
            "ARRANGE: in this environment the statement that failed is logged with the address it carried");

        var copy = (await database.CopiesAsync()).Should().ContainSingle().Which;
        var itsAddresses = copy.Users.Select(u => u.Email![..u.Email!.IndexOf('@')]).ToList();
        itsAddresses.Should().HaveCount(3);
        printed
            .Where(line => itsAddresses.Any(address => line.Message.Contains(address, StringComparison.OrdinalIgnoreCase)))
            .Select(line => $"{line.Level}: {line.Message}")
            .Should().BeEmpty("the address in the log was drawn for the attempt that rolled back, and the copy that was built drew its own");
    }

    [SqlServerFact]
    public async Task TheSummary_CountsWhatThePoolHeldAtTheStart_AndWhatItHoldsAtTheEnd()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var copies = await database.BuildCopiesAsync(7);
        var busy = Enumerable.Repeat((byte)0xA1, 32).ToArray();
        var quiet = Enumerable.Repeat((byte)0xB2, 32).ToArray();
        await database.MarkClaimedAsync(copies[0].Id, DateTime.UtcNow.AddHours(-1), busy);
        await database.MarkClaimedAsync(copies[1].Id, DateTime.UtcNow.AddHours(-2), busy);
        await database.MarkClaimedAsync(copies[2].Id, DateTime.UtcNow.AddHours(-3), quiet);
        // The quiet client's second claim is 24 hours and 2 minutes old: outside the rolling day, so
        // it is neither a claim of the day nor what would put that client at a cap of two.
        await database.MarkClaimedAsync(copies[3].Id, DateTime.UtcNow.AddHours(-24).AddMinutes(-2), quiet);
        // And the record of a copy claimed three days ago and deleted since: no client key, as the
        // recycler leaves it.
        await database.MarkClaimedAsync(copies[4].Id, DateTime.UtcNow.AddDays(-3));
        await using (var db = database.NewContext())
        {
            DateTime? deletedAt = DateTime.UtcNow.AddDays(-2);
            var marked = await db.Set<DemoCopy>()
                .Where(c => c.Id == copies[4].Id)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.DeletedAt, deletedAt));
            marked.Should().Be(1, "ARRANGE: the claimed copy becomes a record");
        }

        var summary = await database.SeedPoolAsync(3, settings: new() { ["Demo:Claim:MaxPerClientPerDay"] = "2" });

        (summary.RowsAtStart, summary.FreeAtStart, summary.Claims24h, summary.ClientsAtCap, summary.ForeignUsers).Should().Be(
            (6, 2, 3, 1, 0),
            "before the run: six rows that are not a record, two of them free, three claims in the rolling day, one client with its two");
        (summary.Target, summary.Seeded, summary.Free, summary.Claimed, summary.Tombstones).Should().Be(
            (3, 1, 3, 4, 1),
            "after it: the one copy that was missing, four claimed copies whose users exist, one record");
        summary.ExitCode.Should().Be(0);
    }

    [SqlServerFact]
    public async Task BesideAPool_AUserOutsideEveryCopyIsReported_AndThePoolIsFilledAllTheSame()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();

        // A pool whose only row is the record of a deleted copy, and one user outside every copy.
        // Not the wrong database: that is users and not one pool row, records included.
        var copy = (await database.BuildCopiesAsync(1)).Single();
        await database.MarkClaimedAsync(copy.Id, DateTime.UtcNow.AddDays(-3));
        await using (var db = database.NewContext())
        {
            DateTime? deletedAt = DateTime.UtcNow.AddDays(-2);
            var marked = await db.Set<DemoCopy>()
                .Where(c => c.Id == copy.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.DeletedAt, deletedAt));
            marked.Should().Be(1, "ARRANGE: the claimed copy becomes a record");
        }

        using var client = database.Api().CreateClient();
        await DemoVisitor.RegisterAsync(client, "resident");

        var summary = await database.SeedPoolAsync(2);

        (summary.RowsAtStart, summary.Tombstones, summary.ForeignUsers).Should().Be((0, 1, 1), "ARRANGE: a record, no live row, one user outside");
        (summary.Seeded, summary.Free, summary.ExitCode).Should().Be(
            (2, 2, 13), "the pool is the demo's, so it is filled; the user that should not be there is what the exit code says");
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
