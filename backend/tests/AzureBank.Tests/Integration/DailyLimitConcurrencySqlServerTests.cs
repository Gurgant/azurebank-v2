using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzureBank.Api.Services.Implementations;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Account;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.DTOs.Transaction;
using AzureBank.Shared.DTOs.Transfer;
using AzureBank.Shared.DTOs.User;
using AzureBank.Shared.Enums;
using AzureBank.Shared.Options;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace AzureBank.Tests.Integration;

/// <summary>
/// The day's ceiling on outgoing external transfers holds under real contention on real SQL Server
/// (ADR-0050 D5): the per-user application lock is what makes it true across two accounts of one
/// user, where no row is shared and the balance's RowVersion never fires.
/// </summary>
/// <remarks>
/// <para>
/// WRITTEN AS A REPRODUCTION FIRST, in ADR-0044's posture — and the reproduction corrected the
/// plan. With the <c>sp_getapplock</c> statement commented out of <c>TransferService</c>, the
/// planned two-account, ONE-payee shape stayed green on every run: a transfer also updates the
/// payee's Accounts row, whose RowVersion serialises the losers into a retry that re-sums after the
/// winner's commit. The race the lock closes exists between transfers to DIFFERENT payees, so
/// <c>EightAccountsOfOneUser_OneTransferEach_NeverExceedTheDay</c> (eight payees, the sum-to-commit
/// window stretched by <c>SlowAuditTailInterceptor</c>) is the deterministic red — 8 x 201 and a
/// day's sum of 800 against a ceiling of 500 — pasted into ADR-0050 D5; the two-account proof
/// with distinct payees went red once and green once. A proof that was only ever seen green would
/// not be known to be a proof of anything.
/// </para>
/// <para>
/// Every host here mirrors production's retrying strategy (<c>EnableSqlRetryOnFailure</c>): the
/// raw statement runs inside <c>strategy.ExecuteAsync</c>, and the lesson of the audited-save proof
/// in <c>AuditChainSqlServerTests</c> (AnAuditedSave_WorksUnderTheRetryingStrategy...) is that only
/// that configuration can produce the "does not support user-initiated transactions" 500.
/// SQL-gated because InMemory has no locks: set <c>AZUREBANK_TEST_SQLSERVER</c> or these skip.
/// </para>
/// </remarks>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class DailyLimitConcurrencySqlServerTests : IDisposable
{
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private const string Pin = "123456";
    private const decimal Limit = 500m;
    private const decimal Amount = 100m;
    private const int Burst = 8;

    /// <summary>
    /// How long the first writer holds the audit tail in the cross-account proofs — long enough for
    /// every other request to reach its in-transaction sum, short enough to stay inside the tail's
    /// five-second bound (Audit:TailTimeoutSeconds) with room to spare.
    /// </summary>
    private static readonly TimeSpan StretchedWindow = TimeSpan.FromSeconds(1);

    private readonly ITestOutputHelper _output;
    private CustomWebApplicationFactory? _factory;

    public DailyLimitConcurrencySqlServerTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Phase 0.1 / 0.2 — the applock spike, kept as the parse/retry/release sanity test
    // ─────────────────────────────────────────────────────────────────────────

    [SqlServerFact]
    public async Task TheApplock_ParsesUnderTheRetryingStrategy_BlocksASecondConnection_AndReleasesOnCommitAndRollback()
    {
        /*
          Three things a comment could only claim, measured: (1) the exact statement TransferService
          runs parses and returns >= 0 on this server, inside strategy.ExecuteAsync + an explicit
          transaction under EnableRetryOnFailure — the configuration that once answered 500 on every
          audited request; (2) while held, a second connection asking for the same resource with a
          zero timeout is refused (-1); (3) Commit releases it, and so does Rollback, with no
          sp_releaseapplock — the lock's owner is the transaction.
        */
        var client = CreateSqlClient();
        _ = client;

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var connectionString = db.Database.GetConnectionString()!;
        var resource = TransferService.DailyLimitLockResource(Guid.NewGuid());

        // Phase 0.2: the isolation this proof ran under, so CI's container value lands in its log.
        var isolation = await db.Database
            .SqlQueryRaw<IsolationRow>(
                "SELECT CAST(is_read_committed_snapshot_on AS int) AS Rcsi, "
                + "CAST(snapshot_isolation_state AS int) AS SnapshotState, name AS Name "
                + "FROM sys.databases WHERE name = DB_NAME()")
            .SingleAsync();
        _output.WriteLine(
            $"sys.databases [{isolation.Name}]: is_read_committed_snapshot_on={isolation.Rcsi} "
            + $"snapshot_isolation_state={isolation.SnapshotState}");

        var strategy = db.Database.CreateExecutionStrategy();

        // Held, then committed.
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();

            // ToListAsync, not SingleAsync: a DECLARE/EXEC batch is non-composable SQL, and EF
            // would wrap SingleAsync as a subquery over it, which its SQL generator refuses
            // ("'SqlQuery' was called with non-composable SQL and with a query composing over it"
            // — observed while writing the test, NOT measured: no file under plans/daily-limit/
            // carries that line, so it is not a value the house rule lets this comment claim).
            // The isolation query above does compose under SingleAsync because it starts with
            // SELECT.
            var granted = (await db.Database
                .SqlQueryRaw<int>(
                    "DECLARE @r int; EXEC @r = sp_getapplock @Resource = {0}, @LockMode = N'Exclusive', "
                    + "@LockOwner = N'Transaction'; SELECT @r AS [Value]",
                    resource)
                .ToListAsync()).Single();
            _output.WriteLine($"sp_getapplock (first connection, inside the transaction) returned {granted}");
            granted.Should().BeGreaterThanOrEqualTo(0, "0 = granted, 1 = granted after a wait");

            // The production batch as TransferService issues it: parses, and raises nothing on a
            // lock this transaction already holds (a re-request by the same owner is granted). The
            // second argument is the wait bound in milliseconds, the unit sp_getapplock takes.
            await db.Database.ExecuteSqlRawAsync(
                TransferService.DailyLimitLockSql, resource, LockTimeoutMilliseconds());

            var whileHeld = await ProbeAsync(connectionString, resource);
            _output.WriteLine($"sp_getapplock (second connection, @LockTimeout = 0) while held returned {whileHeld}");
            whileHeld.Should().Be(-1, "-1 is a timeout: the second connection could not take the lock");

            await tx.CommitAsync();
        });

        (await ProbeAsync(connectionString, resource)).Should().Be(0, "commit released it");

        // Held, then rolled back — the loser's path.
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlRawAsync(
                TransferService.DailyLimitLockSql, resource, LockTimeoutMilliseconds());
            (await ProbeAsync(connectionString, resource)).Should().Be(-1);
            await tx.RollbackAsync();
        });

        (await ProbeAsync(connectionString, resource)).Should().Be(0, "rollback released it too");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // The wait behind the lock is bounded, and the timeout is a fault
    // ─────────────────────────────────────────────────────────────────────────

    [SqlServerFact]
    public async Task TheApplock_RefusesInsideTheConfiguredBound_WithAMessageThatNamesTheTimeout()
    {
        /*
          THE BOUND, PROVEN BY THE CLOCK RATHER THAN BY THE EXCEPTION TYPE. Without @LockTimeout the
          batch waits at @@LOCK_TIMEOUT — measured -1 on LocalDB, i.e. forever — and only the global
          30-second CommandTimeout ends it, holding a transaction and a pooled connection for half a
          minute per queued same-payer transfer
          (plans/daily-limit/measure-cr1-2026-09-07.txt, run 1). An assertion on the throw alone
          would be green under either regime, which is why the ELAPSED time is the assertion and the
          message is the corroboration.

          The host is built with a two-second bound so the proof costs two seconds rather than the
          production ten; the value is read back OUT of the host's own options, so the test cannot
          drift from what the transfer would actually send.

          The holder is a raw SqlConnection rather than a second DbContext: an application lock
          owned by a TRANSACTION is released by that transaction, and a raw connection is the
          shortest way to hold one open across another connection's attempt.
        */
        var client = CreateSqlClient(lockTimeoutSeconds: 2);
        _ = client;

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var connectionString = db.Database.GetConnectionString()!;
        var resource = TransferService.DailyLimitLockResource(Guid.NewGuid());
        var boundMilliseconds = LockTimeoutMilliseconds();
        boundMilliseconds.Should().Be(
            2_000, "the host bound the wait at the two seconds asked for");

        await using var holder = new SqlConnection(connectionString);
        await holder.OpenAsync();
        await using (var holderTx = (SqlTransaction)await holder.BeginTransactionAsync())
        {
            await using (var take = holder.CreateCommand())
            {
                take.Transaction = holderTx;
                take.CommandText =
                    "DECLARE @r int; EXEC @r = sp_getapplock @Resource = @resource, "
                    + "@LockMode = N'Exclusive', @LockOwner = N'Transaction'; SELECT @r";
                take.Parameters.AddWithValue("@resource", resource);
                ((int)(await take.ExecuteScalarAsync())!).Should().BeGreaterThanOrEqualTo(
                    0, "the holder must actually hold it, or the waiter below proves nothing");
            }

            var strategy = db.Database.CreateExecutionStrategy();
            var started = Stopwatch.StartNew();

            var waiting = async () => await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await db.Database.BeginTransactionAsync();
                await db.Database.ExecuteSqlRawAsync(
                    TransferService.DailyLimitLockSql, resource, boundMilliseconds);
                await tx.RollbackAsync();
            });

            var thrown = (await waiting.Should().ThrowAsync<SqlException>(
                "a lock the payer cannot take is a fault the transfer must not swallow")).Which;
            started.Stop();
            _output.WriteLine(
                $"waiter refused after {started.ElapsedMilliseconds} ms against a bound of "
                + $"{boundMilliseconds} ms; SqlException {thrown.Number}: {thrown.Message}");

            thrown.Number.Should().Be(50_000, "the batch's own THROW, not a lower-level error");
            thrown.Message.Should().Contain(
                $"timed out after {boundMilliseconds} ms",
                "-1 must be named as the wait expiring, not printed as a bare negative number: it "
                + "is the return value every other refusal shares");
            thrown.Message.Should().NotContain(
                ErrorCodes.DailyLimitExceeded,
                "a timeout says the server was busy, never that the payer's day is full");

            started.Elapsed.Should().BeGreaterThanOrEqualTo(
                TimeSpan.FromMilliseconds(boundMilliseconds * 0.75),
                "it must WAIT for the bound rather than fail fast — a zero-wait bound would be a "
                + "different bug with the same exception");
            started.Elapsed.Should().BeLessThan(
                TimeSpan.FromSeconds(15),
                "and it must not ride the 30-second CommandTimeout, which was the only bound "
                + "before DailyLimit:LockTimeoutSeconds existed");

            await holderTx.RollbackAsync();
        }

        (await ProbeAsync(connectionString, resource)).Should().Be(
            0, "the holder's rollback released it: the bound refused a real wait, not a leak");
    }

    /// <summary>
    /// The wait bound the HOST is running with, in the unit <c>sp_getapplock</c> takes — read from
    /// the composition root rather than restated, so these proofs send what a transfer sends.
    /// </summary>
    private int LockTimeoutMilliseconds()
        => _factory!.Services.GetRequiredService<IOptions<DailyLimitOptions>>()
            .Value.LockTimeoutMilliseconds;

    /// <summary>
    /// What each loser saw as <c>used</c> when it was refused — the sum it lost to.
    /// </summary>
    private static string UsedSeenByLosers(IEnumerable<string> bodies)
    {
        var seen = new List<string>();
        foreach (var body in bodies)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("used", out var used))
                {
                    seen.Add(used.GetRawText());
                }
            }
            catch (JsonException)
            {
                // a 201 body has no `used`; a non-JSON body is reported by the status assertions
            }
        }

        return string.Join(",", seen);
    }

    private sealed class IsolationRow
    {
        public int Rcsi { get; set; }
        public int SnapshotState { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    /// <summary>
    /// A SECOND connection asking for the resource with a zero timeout, in its own transaction it
    /// then rolls back: -1 while the first holds it, 0 once released.
    /// </summary>
    private static async Task<int> ProbeAsync(string connectionString, string resource)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var tx = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText =
            "DECLARE @r int; EXEC @r = sp_getapplock @Resource = @resource, @LockMode = N'Exclusive', "
            + "@LockOwner = N'Transaction', @LockTimeout = 0; SELECT @r";
        command.Parameters.AddWithValue("@resource", resource);
        var result = (int)(await command.ExecuteScalarAsync())!;
        await tx.RollbackAsync();
        return result;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Phase 5.4 — two accounts of one user, 8-way, under mixed load
    // ─────────────────────────────────────────────────────────────────────────

    [SqlServerFact]
    public async Task TwoAccountsOfOneUser_ParallelTransfers_NeverExceedTheDay()
    {
        /*
          THE RACE THE PRE-CHECK CANNOT SEE. One user, two accounts funded 1,000 each, a ceiling of
          500, eight authorisations of 100 (four per source) minted first — the mint reserves
          nothing, so all eight mint against a used of 0. Then all eight transfers fire together
          with distinct idempotency keys, WHILE the same user deposits, renames their handle and
          mints with a wrong PIN — every writer that touches the rows the alternatives would have
          locked.

          Exactly floor(500 / 100) = 5 may land; the other three must be refused
          DAILY_LIMIT_EXCEEDED inside the transaction, after the lock, with nothing written; nothing
          may answer 500 and no deadlock may escape the retry. Without the lock, transfers from
          account A and account B share no row, both sum a day that does not yet contain the other,
          and both commit.

          EIGHT PAYEES, NOT ONE, and the reason is measured (see the eight-account proof below): a
          transfer also UPDATEs the payee's account row, which carries a RowVersion, so every
          transfer to the SAME payee is serialised by the payee's row — the loser retries, re-enters
          the delegate and re-sums after the winner's commit, and the lock under test never decides
          anything. Distinct payees are what leave no shared row between the two sources.
        */
        var client = CreateSqlClient();
        var (token, userId, accountA) = await RegisterFundedAsync(client, funding: 1_000m);
        var accountB = await OpenSecondFundedAccountAsync(client, token, funding: 1_000m);

        var authorisations = new List<(Guid Account, string Payee, Guid Authorization)>();
        foreach (var account in new[] { accountA, accountB })
        {
            for (var i = 0; i < Burst / 2; i++)
            {
                var payee = await RegisterRecipientAsync(client);
                authorisations.Add((account, payee, await MintAsync(client, token, account, payee)));
            }
        }

        // Everything above is warm-up. From here the FIRST writer to reach the audit tail holds its
        // lock for a second — see the eight-account proof below for why the window is stretched.
        var stall = new SlowAuditTailInterceptor(StretchedWindow);
        _factory!.AddInterceptor(stall);

        var transfers = authorisations.Select(a =>
            TransferAsync(client, token, a.Account, a.Payee, a.Authorization, Guid.NewGuid()));
        var deposit = DepositAsync(client, token, accountA, 50m);
        var rename = RenameAsync(client, token);
        var wrongPinMint = MintRawAsync(client, token, accountB, authorisations[0].Payee, pin: "000000");

        var everything = await Task.WhenAll(transfers.Concat([deposit, rename, wrongPinMint]));
        stall.Fired.Should().BeTrue("a run in which nothing stalled would not have measured the race");
        var responses = everything[..Burst];
        var deposited = everything[Burst];
        var renamed = everything[Burst + 1];
        var wrongPin = everything[Burst + 2];

        var statuses = responses.Select(r => (int)r.StatusCode).ToArray();
        _output.WriteLine("transfer statuses: " + string.Join(",", statuses));
        _output.WriteLine($"concurrent deposit {(int)deposited.StatusCode}, rename {(int)renamed.StatusCode}, "
                          + $"wrong-PIN mint {(int)wrongPin.StatusCode}");

        var sum = await TodaysExternalOutflowAsync(userId);
        _output.WriteLine($"ledger sum of today's external TransferOut for the user: {sum}");

        var bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync()));
        _output.WriteLine("used seen by the losers: " + UsedSeenByLosers(bodies));
        var deadlockLines = _factory!.CapturedLog.Where(l => l.Contains("1205") || l.Contains("deadlock", StringComparison.OrdinalIgnoreCase)).ToList();
        _output.WriteLine($"captured warning+ lines: {_factory.CapturedLog.Count}; mentioning 1205/deadlock: {deadlockLines.Count}");
        foreach (var line in deadlockLines)
        {
            _output.WriteLine("  " + line);
        }

        statuses.Should().NotContain(500,
            "nothing may surface as a 500; bodies: " + string.Join(" | ", bodies.Select(b => b[..Math.Min(160, b.Length)])));
        ((int)deposited.StatusCode).Should().Be(201, "the concurrent deposit by the same user must land");
        ((int)renamed.StatusCode).Should().BeLessThan(500, "the concurrent rename must not deadlock into a 500");
        ((int)wrongPin.StatusCode).Should().BeOneOf([401, 422], "a wrong PIN is 401, or 422 once the day is full — never 500");
        deadlockLines.Should().OnlyContain(
            l => l.Contains("retried", StringComparison.OrdinalIgnoreCase),
            "a 1205 may only appear as a transient the strategy retried; an escaped one is a defect");

        statuses.Count(s => s == 201).Should().Be(5,
            "exactly floor(limit / amount) transfers may land in the day");
        statuses.Count(s => s == 422).Should().Be(3, "every loser is refused, not dropped");
        bodies.Where(b => b.Contains("\"status\":422")).Should().OnlyContain(
            b => b.Contains(ErrorCodes.DailyLimitExceeded),
            "the losers lose to the day, not to the balance or the authorisation");

        sum.Should().Be(Limit, "the ledger holds exactly the day, never more");

        var pending = await PendingCountAsync(authorisations.Select(a => a.Authorization));
        pending.Should().Be(3, "the three losers' authorisations are left spendable, as after any refusal");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // The cross-account race with NOTHING else serialising it: eight accounts, one transfer each
    // ─────────────────────────────────────────────────────────────────────────

    [SqlServerFact]
    public async Task EightAccountsOfOneUser_OneTransferEach_NeverExceedTheDay()
    {
        /*
          THE REPRODUCTION VEHICLE, and what it took to make one. No two transfers share a row here:
          eight accounts of one user, funded 100 each, one transfer of 100 from each, to EIGHT
          different payees — so no RowVersion fires on either side and nothing but the lock under
          test can serialise the day's sum across the accounts.

          MEASURED 2026-09-07, with the sp_getapplock statement commented out of TransferService.
          The first shape — eight sources, ONE payee — answered 5 × 201 and a day of 500 on every
          run: green without the lock. A second mutant with the in-transaction check removed too
          answered 8 × 201 and 800 every time, so the requests do overlap at the pre-check; raising
          the test process's thread-pool minimum to 128 changed nothing; and holding the audit tail
          for a second on the first writer changed nothing either — the three losers still reported
          `used` = 500, i.e. they re-summed AFTER five commits. What re-summed them was not a lock
          but the PAYEE's account row: a transfer credits it with an UPDATE guarded by its
          RowVersion, ConcurrencyRetry accepts a conflict on any Account, and the loser re-enters
          the delegate — where the check runs again. Transfers to one payee are serialised by that
          row whether or not the applock exists. The two-account premise in ADR-0050 ("no shared
          row") is therefore true only for transfers to DIFFERENT payees, which is the shape here.

          The window is still STRETCHED, with the fixture ADR-0044's contention proof already
          uses: SlowAuditTailInterceptor holds the audit tail lock for a second on the first writer
          to reach it, so every other transfer reaches its sum during that second and then queues
          behind the tail. Without the applock all of them sum a day of 0 and commit once the stall
          ends. With it, the seven wait on the applock BEFORE summing, and after the first commits
          each sums the day as it then stands: exactly five land. The stall changes nothing about
          the mechanism, only the odds (the natural window is a few milliseconds on LocalDB);
          `Fired` is asserted so a run where nothing stalled cannot pass as a measurement.
        */
        var client = CreateSqlClient();
        var (token, userId, first) = await RegisterFundedAsync(client, funding: Amount);
        var accounts = new List<Guid> { first };
        for (var i = 1; i < Burst; i++)
        {
            accounts.Add(await OpenSecondFundedAccountAsync(client, token, funding: Amount));
        }

        var authorisations = new List<(Guid Account, string Payee, Guid Authorization)>();
        foreach (var account in accounts)
        {
            var payee = await RegisterRecipientAsync(client);
            authorisations.Add((account, payee, await MintAsync(client, token, account, payee)));
        }

        // Everything above is warm-up; every mint and deposit took the tail too. From here the
        // first tail read taken holds the lock for StretchedWindow.
        var stall = new SlowAuditTailInterceptor(StretchedWindow);
        _factory!.AddInterceptor(stall);

        var responses = await Task.WhenAll(authorisations.Select(a =>
            TransferAsync(client, token, a.Account, a.Payee, a.Authorization, Guid.NewGuid())));
        stall.Fired.Should().BeTrue("a run in which nothing stalled would not have measured the race");

        var statuses = responses.Select(r => (int)r.StatusCode).ToArray();
        _output.WriteLine("transfer statuses: " + string.Join(",", statuses));
        var bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync()));
        var sum = await TodaysExternalOutflowAsync(userId);
        _output.WriteLine($"ledger sum of today's external TransferOut for the user: {sum}");
        _output.WriteLine("used seen by the losers: " + UsedSeenByLosers(bodies));

        statuses.Should().NotContain(500,
            "bodies: " + string.Join(" | ", bodies.Select(b => b[..Math.Min(160, b.Length)])));
        statuses.Count(s => s == 201).Should().Be(5,
            "exactly floor(limit / amount) transfers may land in the day; more means the sum ran unserialised");
        statuses.Count(s => s == 422).Should().Be(3);
        bodies.Where(b => b.Contains("\"status\":422")).Should().OnlyContain(
            b => b.Contains(ErrorCodes.DailyLimitExceeded));
        sum.Should().Be(Limit, "the ledger holds exactly the day, never more");
        (await PendingCountAsync(authorisations.Select(a => a.Authorization))).Should().Be(3);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Phase 5.5 — one account, 8-way: the RowVersion retry re-enters the delegate and re-sums
    // ─────────────────────────────────────────────────────────────────────────

    [SqlServerFact]
    public async Task OneAccount_ParallelTransfers_NeverExceedTheDay_ThroughTheRowVersionRetry()
    {
        /*
          The race the balance guard's loop already handles, driven through the day's check: eight
          transfers of 100 from ONE account funded 1,000 (so the balance never refuses) against a
          ceiling of 500. Every loser of the RowVersion race re-enters the delegate and re-sums —
          and the sum it sees after the fifth winner refuses it. Same expected shape as the
          two-account proof, reached by a different path.

          WHAT THIS DOES NOT PROVE IS THE LOCK, and the earlier wording here claimed it did. One
          payee is registered for all eight transfers, so by the finding recorded on the
          eight-account proof above the payee's Accounts row serialises this shape whether or not
          the applock exists — and no SlowAuditTailInterceptor stretches the window here. The
          eight-account proof is the lock's tripwire; this one is the retry's. ADR-0050's
          Verification says the same beside it.
        */
        var client = CreateSqlClient();
        var (token, userId, account) = await RegisterFundedAsync(client, funding: 1_000m);
        var payeeTag = await RegisterRecipientAsync(client);

        var authorisations = new List<Guid>();
        for (var i = 0; i < Burst; i++)
        {
            authorisations.Add(await MintAsync(client, token, account, payeeTag));
        }

        var responses = await Task.WhenAll(authorisations.Select(a =>
            TransferAsync(client, token, account, payeeTag, a, Guid.NewGuid())));

        var statuses = responses.Select(r => (int)r.StatusCode).ToArray();
        _output.WriteLine("transfer statuses: " + string.Join(",", statuses));
        var bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync()));
        var sum = await TodaysExternalOutflowAsync(userId);
        _output.WriteLine($"ledger sum of today's external TransferOut for the user: {sum}");
        _output.WriteLine("used seen by the losers: " + UsedSeenByLosers(bodies));

        statuses.Should().NotContain(500,
            "bodies: " + string.Join(" | ", bodies.Select(b => b[..Math.Min(160, b.Length)])));
        statuses.Count(s => s == 201).Should().Be(5);
        statuses.Count(s => s == 422).Should().Be(3);
        bodies.Where(b => b.Contains("\"status\":422")).Should().OnlyContain(
            b => b.Contains(ErrorCodes.DailyLimitExceeded));
        sum.Should().Be(Limit);
        (await PendingCountAsync(authorisations)).Should().Be(3);
    }

    // ─────────────────────────────────────────────────────────────────────────

    /// <param name="lockTimeoutSeconds">
    /// Overrides <c>DailyLimit:LockTimeoutSeconds</c> when the proof is about the wait bound
    /// itself. Left null everywhere else, so every other proof here runs on the production
    /// default and a change to that default would be felt by them rather than hidden behind a
    /// test value.
    /// </param>
    private HttpClient CreateSqlClient(int? lockTimeoutSeconds = null)
    {
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        _factory.EnableSqlRetryOnFailure();
        _factory.SetDailyLimit(Limit);
        if (lockTimeoutSeconds is { } seconds)
        {
            _factory.SetDailyLimitLockTimeoutSeconds(seconds);
        }

        _factory.CaptureLog();
        return _factory.CreateClient();
    }

    private async Task<decimal> TodaysExternalOutflowAsync(Guid userId)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var start = DateTime.UtcNow.Date;
        return await db.Transactions.AsNoTracking().IgnoreQueryFilters()
            .Where(t => t.Account.UserId == userId
                        && t.Type == TransactionType.TransferOut
                        && t.RecipientAzureTag != null
                        && t.Status == TransactionStatus.Completed
                        && t.CreatedAt >= start)
            .SumAsync(t => t.Amount);
    }

    private async Task<int> PendingCountAsync(IEnumerable<Guid> authorisations)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var ids = authorisations.ToList();
        return await db.StepUpAuthorizations.AsNoTracking()
            .CountAsync(a => ids.Contains(a.Id) && a.Status == StepUpAuthorizationStatus.Pending);
    }

    private static async Task<(string Token, Guid UserId, Guid AccountId)> RegisterFundedAsync(
        HttpClient client, decimal funding)
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = $"dailyc_{unique}",
            Email = $"dailyc{unique}@example.com",
            Password = "TestPass123!",
            FirstName = "Daily",
            LastName = "Limit"
        }, Json);
        response.EnsureSuccessStatusCode();
        var registered = await response.Content.ReadFromJsonAsync<ApiResponse<RegisterResponse>>(Json);
        var token = registered!.Data!.Token.AccessToken;
        var accountId = registered.Data.Account.Id;

        (await SendAsync(client, token, HttpMethod.Post, "/api/auth/pin",
            new SetPinRequest { Pin = Pin, Password = "TestPass123!" })).EnsureSuccessStatusCode();
        (await DepositAsync(client, token, accountId, funding)).EnsureSuccessStatusCode();

        return (token, registered.Data.User.Id, accountId);
    }

    private static async Task<Guid> OpenSecondFundedAccountAsync(HttpClient client, string token, decimal funding)
    {
        var created = await SendAsync(client, token, HttpMethod.Post, "/api/accounts",
            new CreateAccountRequest { Name = "Second", Type = AccountType.Savings });
        created.EnsureSuccessStatusCode();
        var accountId = (await created.Content.ReadFromJsonAsync<ApiResponse<AccountResponse>>(Json))!.Data!.Id;
        (await DepositAsync(client, token, accountId, funding)).EnsureSuccessStatusCode();
        return accountId;
    }

    private static async Task<string> RegisterRecipientAsync(HttpClient client)
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        var tag = $"dailyr_{unique}";
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = tag,
            Email = $"dailyr{unique}@example.com",
            Password = "TestPass123!",
            FirstName = "Payee",
            LastName = "User"
        }, Json);
        response.EnsureSuccessStatusCode();
        return tag;
    }

    private static async Task<Guid> MintAsync(HttpClient client, string token, Guid accountId, string payeeTag)
    {
        var response = await MintRawAsync(client, token, accountId, payeeTag, Pin);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<StepUpAuthorizationResponse>>(Json);
        return body!.Data!.AuthorizationId;
    }

    private static Task<HttpResponseMessage> MintRawAsync(
        HttpClient client, string token, Guid accountId, string payeeTag, string pin)
        => SendAsync(client, token, HttpMethod.Post, "/api/transfers/authorizations",
            new TransferAuthorizationRequest
            {
                FromAccountId = accountId, RecipientAzureTag = payeeTag, Amount = Amount, Pin = pin
            });

    private static Task<HttpResponseMessage> TransferAsync(
        HttpClient client, string token, Guid accountId, string payeeTag, Guid authorization, Guid key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/transfers")
        {
            Content = JsonContent.Create(new TransferRequest
            {
                FromAccountId = accountId,
                RecipientAzureTag = payeeTag,
                Amount = Amount,
                Description = "daily-limit proof"
            }, options: Json)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add(IdempotencyConstants.HeaderName, key.ToString());
        request.Headers.Add(StepUpConstants.HeaderName, authorization.ToString());
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> DepositAsync(HttpClient client, string token, Guid accountId, decimal amount)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/transactions/deposit")
        {
            Content = JsonContent.Create(
                new DepositRequest { AccountId = accountId, Amount = amount, Description = "funding" },
                options: Json)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add(IdempotencyConstants.HeaderName, Guid.NewGuid().ToString());
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> RenameAsync(HttpClient client, string token)
        => SendAsync(client, token, HttpMethod.Patch, "/api/users/me/azuretag",
            new UpdateAzureTagRequest { AzureTag = $"dailyx_{Guid.NewGuid():N}"[..20] });

    private static async Task<HttpResponseMessage> SendAsync<T>(
        HttpClient client, string token, HttpMethod method, string url, T payload)
    {
        using var request = new HttpRequestMessage(method, url)
        {
            Content = JsonContent.Create(payload, options: Json)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    public void Dispose()
    {
        _factory?.Dispose();
    }
}
