using AzureBank.Infrastructure.Data;
using AzureBank.Seeder.Seeders;
using AzureBank.Shared.Constants;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Shared.Options;
using AzureBank.Shared.Services.Interfaces;
using AzureBank.Shared.Utilities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzureBank.Seeder.Pool;

/// <summary>What a top-up did: the copies it built, and each copy it could not build.</summary>
internal sealed record TopUpResult(int Seeded, IReadOnlyList<PoolCopyFailure> Failures);

/// <summary>Builds free demo copies: what <c>seed-pool</c> runs, and what <c>recycle</c> tops up with.</summary>
/// <remarks>
/// <para>
/// A COPY is the demo, private to one visitor: the demo user with two accounts and two months of
/// history, the two contacts that history pays, and the pool row all three users point at. 37 rows:
/// one pool row, three users, three role rows, four accounts, twenty-six ledger rows.
/// </para>
/// <para>
/// A FREE COPY HAS NO PASSWORD. Its users are created through Identity with no password at all, so
/// there is no hash for a sign-in to match, whatever is typed. The claim sets one when a visitor
/// takes the copy. So nothing this class writes can be signed in to with a value that is written
/// down somewhere.
/// </para>
/// <para>
/// ONE COPY, ONE TRANSACTION. A copy is whole or absent: a demo user whose contacts or history
/// are missing would be handed to a visitor like any other. Each copy is built inside one
/// transaction, run through the execution strategy as <c>TransactionSeeder</c> and registration
/// are, and for the reasons given there.
/// </para>
/// <para>
/// ONE COPY'S FAILURE IS NOT THE RUN'S. A copy that cannot be built is counted, logged by its id
/// and left out; the run goes on to the next, and stops only after three in a row.
/// </para>
/// <para>
/// A RUN NAMES A COPY BY ITS ID, never by the address its visitor signs in with: a job's log is
/// kept somewhere else, for longer, and read by other people than the database is. Nothing here
/// logs an address, and outside Development EF logs no statement's values. In Development it
/// does: there a statement that fails is written with the values it carried, and those belong to
/// an attempt that rolled back (see <see cref="BuildAttemptAsync"/>).
/// </para>
/// <para>
/// No audit row is written: the Seeder holds no audit chain key, and the context refuses an audit
/// row without one.
/// </para>
/// </remarks>
public sealed class DemoCopyBuilder
{
    /// <summary>Copies that fail one after another before a run stops trying.</summary>
    private const int FailuresInARowBeforeGivingUp = 3;

    /// <summary>Times one copy is drawn again after a value of it was already taken.</summary>
    private const int CollisionRetries = 3;

    private readonly AzureBankDbContext _context;
    private readonly UserManager<ApplicationUser> _users;
    private readonly RoleSeeder _roles;
    private readonly RunCancellation _run;
    private readonly IPasswordHasher _pinHasher;
    private readonly DemoOptions _options;
    private readonly ILogger<DemoCopyBuilder> _logger;
    private readonly TimeProvider _clock;

    public DemoCopyBuilder(
        AzureBankDbContext context,
        UserManager<ApplicationUser> users,
        RoleSeeder roles,
        RunCancellation run,
        IPasswordHasher pinHasher,
        IOptions<DemoOptions> options,
        ILogger<DemoCopyBuilder> logger,
        TimeProvider? clock = null)
    {
        _context = context;
        _users = users;
        _roles = roles;
        _run = run;
        _pinHasher = pinHasher;
        _options = options.Value;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Tops the pool up to <paramref name="target"/> fresh free copies, or to the configured
    /// target when none is given.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Up to, not by: a pool that already holds its target is left as it is, so the command can be
    /// run again at any time.
    /// </para>
    /// <para>
    /// REFUSED OUTSIDE THE DEMO, here and not only in the command that calls this: a caller that
    /// forgot the flag must not be able to write demo users into a database that is not the demo's.
    /// </para>
    /// <para>
    /// AND REFUSED ON THE WRONG DATABASE: users, and not one pool row. Nothing is written, the roles
    /// included, and the summary carries the count that says why.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException"><c>Demo:Enabled</c> is off.</exception>
    public async Task<PoolRunSummary> SeedPoolAsync(int? target = null, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            throw new InvalidOperationException(
                "seed-pool builds demo copies only in demo mode: Demo:Enabled is not true.");
        }

        var goal = target ?? _options.Pool.TargetFree;
        var start = await PoolCounts.ReadAsync(_context, _options, Now(), cancellationToken);
        if (start.IsTheWrongDatabase)
        {
            return Summary(start, start, goal, new TopUpResult(0, []));
        }

        var topUp = await BuildAsync(goal - start.Free, cancellationToken);
        var end = await PoolCounts.ReadAsync(_context, _options, Now(), cancellationToken);
        return Summary(start, end, goal, topUp);
    }

    private static PoolRunSummary Summary(PoolCounts start, PoolCounts end, int target, TopUpResult topUp)
    {
        var summary = new PoolRunSummary
        {
            RowsAtStart = start.Rows,
            FreeAtStart = start.FreeOfAnyAge,
            Free = end.Free,
            Target = target,
            Claimed = end.Claimed,
            Claims24h = start.Claims24h,
            ClientsAtCap = start.ClientsAtCap,
            Seeded = topUp.Seeded,
            BuildFailed = topUp.Failures.Count,
            Tombstones = end.Tombstones,
            ForeignUsers = start.ForeignUsers,
            Failures = topUp.Failures,
        };
        return summary with { ExitCode = PoolExitCodes.ForSeedPool(summary) };
    }

    /// <summary>
    /// Builds <paramref name="copies"/> free copies, each in its own try, and stops after three
    /// failures in a row. Zero or fewer builds nothing and writes nothing.
    /// </summary>
    /// <remarks>
    /// THE RUN'S TOKEN IS HANDED TO IDENTITY HERE, where both commands reach it. Identity's managers
    /// take no token: the two this tool registers read the scope's <see cref="RunCancellation"/>,
    /// which says "none" until it is set. Unset, a stop that came while a copy's users were being
    /// created went unseen by every statement Identity sent, and the run ended only at the next
    /// check of its own (<c>DemoPoolCommandSqlServerTests</c>: three statements to the roles table
    /// sent with no token, where one, cancelled, is right).
    /// </remarks>
    internal async Task<TopUpResult> BuildAsync(int copies, CancellationToken cancellationToken)
    {
        if (copies <= 0)
        {
            return new TopUpResult(0, []);
        }

        _run.Token = cancellationToken;
        await CreateTheRolesAsync(cancellationToken);

        // One hash for the run, shared by every user of every copy it builds. The PIN is the same
        // on every copy and it is no secret, so a hash per user would buy nothing and cost one
        // Argon2id each: 150 for a pool of 50.
        var pinHash = _pinHasher.HashPin(DemoCopyDefaults.Pin);

        var seeded = 0;
        var inARow = 0;
        var failures = new List<PoolCopyFailure>();
        while (seeded < copies && inARow < FailuresInARowBeforeGivingUp)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Minted here, by the Seeder: the id is what the failure below is logged by, and what
            // the strategy asks for when the answer to a commit is lost.
            var copyId = Guid.CreateVersion7();
            try
            {
                await BuildOneAsync(copyId, pinHash, cancellationToken);
                seeded++;
                inARow = 0;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Not when the run was stopped, which is read from the token and not from the
                // kind of failure: a stop that reaches a statement the database already has
                // comes back as the database's own error, not as a cancellation. That is
                // nothing the copy did, and it ends the run.
                inARow++;

                // The root cause, not EF's "an error occurred while saving the entity changes",
                // and SQL Server's number when the failure was the database's.
                var number = SqlErrorNumber(ex);
                failures.Add(new PoolCopyFailure(copyId, number, ex.GetBaseException().Message));
                _logger.LogError(ex, "Demo copy {CopyId} could not be built (error {ErrorNumber})", copyId, number);
            }
            finally
            {
                // Built or not, nothing of the copy stays on the context, which the caller goes
                // on using. The rows of an attempt that failed are still tracked as Added: a
                // later save would write half a copy. The rows of a copy that was built are
                // tracked as they were written: a later read through this context would answer
                // with them and not with the database, and a copy a visitor has claimed since
                // would still read as free.
                _context.ChangeTracker.Clear();
            }
        }

        return new TopUpResult(seeded, failures);
    }

    /// <summary>Creates the roles a copy's users are given, with the seeder <c>seed</c> uses.</summary>
    /// <remarks>
    /// <para>
    /// First, and outside every copy: Identity throws on a role that has no row, and on a database
    /// that was only migrated nothing else creates them.
    /// </para>
    /// <para>
    /// TWO RUNS CAN START TOGETHER on such a database: a <c>seed-pool</c> beside a scheduled
    /// <c>recycle</c>. Both look for a role, both find none, both insert it, and the database
    /// refuses the second insert (2601, on the role's name). The run that was refused has lost
    /// nothing: the role is there, which is all this step is for. It forgets the row it could not
    /// write, which the next save would otherwise send again, and looks once more. That can happen
    /// once for each role and no more often: a role that was lost to another run exists.
    /// </para>
    /// </remarks>
    private async Task CreateTheRolesAsync(CancellationToken cancellationToken)
    {
        for (var lost = 0; ; lost++)
        {
            try
            {
                await _roles.SeedAsync(cancellationToken);
                return;
            }
            catch (Exception ex) when (lost < Roles.All.Length && IsUniqueViolation(ex))
            {
                _context.ChangeTracker.Clear();

                // Without the exception, as below: EF has written it already.
                _logger.LogWarning(
                    "Another run created a role first (error {ErrorNumber}); looking for the roles again",
                    SqlErrorNumber(ex));
            }
        }
    }

    /// <summary>
    /// Builds one copy, and draws it again when a value of it was already taken.
    /// </summary>
    /// <remarks>
    /// A handle, an account number and a transaction number are each unique in the whole table, and
    /// each is drawn at random, so now and then one is held already: by another copy, or by a handle
    /// a visitor renamed to. SQL Server answers 2601 or 2627, the attempt rolls back whole, and the
    /// copy is tried again with every value drawn afresh. Three times at most: a copy that collides
    /// four times running is not unlucky, and it is reported as a failure with the database's number.
    /// The API's own collision retry is out of reach here: the Seeder references no API assembly.
    /// </remarks>
    private async Task BuildOneAsync(Guid copyId, string pinHash, CancellationToken cancellationToken)
    {
        for (var retries = 0; ; retries++)
        {
            try
            {
                await BuildAttemptAsync(copyId, pinHash, cancellationToken);
                return;
            }
            catch (Exception ex) when (retries < CollisionRetries && IsUniqueViolation(ex))
            {
                // Without the exception: EF has written it already, at Error, when the save
                // failed, with SQL Server's message and the value it refused. This line says what
                // the run did about it.
                _logger.LogWarning(
                    "Demo copy {CopyId} drew a value that was already taken (error {ErrorNumber}); drawing it again",
                    copyId,
                    SqlErrorNumber(ex));
            }
        }
    }

    /// <summary>One attempt at one copy: one transaction, every row or none.</summary>
    /// <remarks>
    /// <para>
    /// EACH RUN OF THE DELEGATE STARTS CLEAN. The strategy re-runs it on a transient fault, on this
    /// same context, and a failed save leaves its rows tracked as Added: without the
    /// <c>Clear()</c> the second run would insert the first run's rows beside its own. Everything
    /// the attempt inserts is created inside the delegate for the same reason.
    /// </para>
    /// <para>
    /// AND EACH RUN DRAWS ITS OWN HANDLES AND ADDRESSES. Where EF logs a statement with its values,
    /// which is Development, an insert of a user that fails is written to the log with the address
    /// it carried. The attempt it belonged to rolled back, and the next run of the delegate draws
    /// again, so an address in the log is never the address of a copy that exists.
    /// </para>
    /// <para>
    /// IDENTITY WRITES THROUGH THIS CONTEXT. It is registered with
    /// <c>AddEntityFrameworkStores&lt;AzureBankDbContext&gt;</c>, so <c>UserManager</c> saves through
    /// the scoped context this class holds and its rows join the transaction begun here.
    /// </para>
    /// <para>
    /// WHEN THE ANSWER TO THE COMMIT IS LOST, the strategy asks whether a pool row with this copy's
    /// id exists before it runs the delegate again. The row is written in the same transaction as
    /// everything else of the copy, so it is there exactly when the whole copy is.
    /// </para>
    /// </remarks>
    private async Task BuildAttemptAsync(Guid copyId, string pinHash, CancellationToken cancellationToken)
    {
        var strategy = _context.Database.CreateExecutionStrategy();
        await strategy.ExecuteInTransactionAsync(
            async token =>
            {
                _context.ChangeTracker.Clear();
                var cast = DemoCredentials.Create();

                // The seed instant: the pool row carries it, and the ledger's dates are offsets
                // from it. Read once, so the newest row of the history is the instant on the row.
                var now = Now();
                var ownerId = Guid.CreateVersion7();

                // The pool row first: the users' foreign key points at it.
                _context.DemoCopies.Add(new DemoCopy { Id = copyId, OwnerUserId = ownerId, CreatedAt = now });
                await _context.SaveChangesAsync(token);

                var john = await CreateUserAsync(ownerId, copyId, cast.Owner, pinHash);
                var jane = await CreateUserAsync(Guid.CreateVersion7(), copyId, cast.Jane, pinHash);
                var mike = await CreateUserAsync(Guid.CreateVersion7(), copyId, cast.Mike, pinHash);

                // The fixed demo's accounts and balances, with numbers from the real generator.
                var accounts = new Dictionary<DemoLedgerAccount, Account>
                {
                    [DemoLedgerAccount.OwnerSavings] = Open(john, "Main Savings", AccountType.Savings, 12450.00m, isPrimary: true),
                    [DemoLedgerAccount.OwnerChecking] = Open(john, "Checking", AccountType.Checking, 2300.00m, isPrimary: false),
                    [DemoLedgerAccount.JaneSavings] = Open(jane, "Personal Savings", AccountType.Savings, 8500.00m, isPrimary: true),
                    [DemoLedgerAccount.MikeInvestment] = Open(mike, "Investment Account", AccountType.Investment, 25000.00m, isPrimary: true),
                };
                _context.Accounts.AddRange(accounts.Values);
                await _context.SaveChangesAsync(token);

                var ledger = DemoLedger.Build(accounts, now);
                _context.Transactions.AddRange(ledger.Select(entry => entry.Row));
                await _context.SaveChangesAsync(token);
                await DemoLedger.AgeAndLinkAsync(_context, ledger, token);
            },
            token => _context.DemoCopies.AsNoTracking().AnyAsync(copy => copy.Id == copyId, token),
            cancellationToken);
    }

    /// <summary>
    /// One user of a copy, created the way registration creates one, and with no password.
    /// </summary>
    private async Task<ApplicationUser> CreateUserAsync(Guid id, Guid copyId, DemoIdentity who, string pinHash)
    {
        var user = new ApplicationUser
        {
            // The user name is the immutable id, never the handle (ADR-0015).
            Id = id,
            UserName = id.ToString(),
            Email = who.Email,
            EmailConfirmed = true,
            AzureTag = who.AzureTag,
            FirstName = who.FirstName,
            LastName = who.LastName,
            PinHash = pinHash,
            DemoCopyId = copyId,
        };

        // CreateAsync(user), not CreateAsync(user, password): the password hash stays null.
        Check(await _users.CreateAsync(user), $"create the copy's {who.FirstName}");

        // The answer is read: AddToRoleAsync can refuse without throwing, and a user without its
        // role is not a copy. Throwing here rolls the whole copy back.
        Check(await _users.AddToRoleAsync(user, Roles.User), $"give the copy's {who.FirstName} the role {Roles.User}");
        return user;
    }

    private static Account Open(ApplicationUser owner, string name, AccountType type, decimal balance, bool isPrimary) => new()
    {
        UserId = owner.Id,
        User = owner,
        AccountNumber = IdGenerator.GenerateAccountNumber(),
        Name = name,
        Type = type,
        Balance = balance,
        IsPrimary = isPrimary,
    };

    /// <summary>Throws when Identity answered a failure, naming its codes.</summary>
    /// <remarks>
    /// The codes, not the descriptions: a description can quote the value it refused
    /// ("Email '…' is already taken"), and this message is logged. For the same reason
    /// <paramref name="what"/> names a user by the part it plays, never by its handle or its address.
    /// </remarks>
    private static void Check(IdentityResult result, string what)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Identity refused to {what}: {string.Join(", ", result.Errors.Select(error => error.Code))}.");
        }
    }

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;

    /// <summary>SQL Server's error number, when the failure was the database's.</summary>
    internal static int? SqlErrorNumber(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sql)
            {
                return sql.Number;
            }
        }

        return null;
    }

    // 2601: a duplicate in a unique index. 2627: a duplicate in a primary key or a unique constraint.
    private static bool IsUniqueViolation(Exception exception) => SqlErrorNumber(exception) is 2601 or 2627;
}
