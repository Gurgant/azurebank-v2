using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AzureBank.Api.Observability;
using AzureBank.Api.Security;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Account;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.DTOs.Transaction;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Options;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Serilog.Events;

namespace AzureBank.Tests.Integration;

/// <summary>
/// The public demo's API on SQL Server, on the database the pool's commands fill: each test has a
/// scratch database of its own (<see cref="DemoPoolDatabase"/>), copies built by the Seeder's own
/// builder, and an API with the demo on.
/// </summary>
/// <remarks>
/// <para>
/// ON SQL SERVER, because what a claim promises is a property of its statements: one copy is given
/// to one visitor however many ask at once, and a claim that fails leaves nothing behind. The
/// InMemory provider has no transaction, no set-based statement and no second connection, so it
/// can show neither.
/// </para>
/// <para>
/// A row of the pool with no users behind it (<see cref="AddBareCopiesAsync"/>) stands in where
/// only the pool's table is read: the daily cap counts rows, and the candidates are rows.
/// </para>
/// </remarks>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class DemoClaimSqlServerTests
{
    private const string Visitor = "203.0.113.7";
    private const string AnotherVisitor = "203.0.113.8";

    private static DemoOptions DemoOf(CustomWebApplicationFactory api) =>
        api.Services.GetRequiredService<IOptions<DemoOptions>>().Value;

    private static string DatabaseOf(CustomWebApplicationFactory api)
    {
        using var scope = api.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AzureBankDbContext>().Database.GetDbConnection().Database;
    }

    /// <summary>The key the API stores for a client at <paramref name="address"/>, under the test host's secret.</summary>
    private static byte[] KeyOf(string address) =>
        DemoClientKey.Of(CustomWebApplicationFactory.DemoClientKeySecret, address);

    private static async Task<int> GrantsOfAsync(DemoPoolDatabase database, params Guid[] userIds) =>
        (await database.RowsOfAsync(userIds))["RefreshTokens"];

    private static async Task<List<DemoCopy>> RowsAsync(DemoPoolDatabase database)
    {
        await using var db = database.NewContext();
        return await db.DemoCopies.AsNoTracking().OrderBy(c => c.CreatedAt).ToListAsync();
    }

    /// <summary>A refusal of the claim: its status, its code, and no wait named anywhere.</summary>
    private static async Task ShouldBePoolEmptyAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, text);
        using var body = JsonDocument.Parse(text);
        body.RootElement.GetProperty("errorCode").GetString().Should().Be(ErrorCodes.DemoPoolEmpty);
        body.RootElement.TryGetProperty("retryAfterSeconds", out _).Should().BeFalse();
        response.Headers.Contains("Retry-After").Should().BeFalse();
    }

    /// <summary>
    /// Adds <paramref name="count"/> rows to the pool with no users behind them, each as
    /// <paramref name="make"/> shapes it from its position.
    /// </summary>
    private static async Task<List<DemoCopy>> AddBareCopiesAsync(DemoPoolDatabase database, int count, Action<DemoCopy, int> make)
    {
        var rows = Enumerable.Range(0, count)
            .Select(i =>
            {
                var row = new DemoCopy { Id = Guid.CreateVersion7(), OwnerUserId = Guid.CreateVersion7(), CreatedAt = DateTime.UtcNow };
                make(row, i);
                return row;
            })
            .ToList();
        await using var db = database.NewContext();
        db.DemoCopies.AddRange(rows);
        await db.SaveChangesAsync();
        return rows;
    }

    /// <summary>
    /// Sets whether the scratch database reads committed data from row versions
    /// (<c>READ_COMMITTED_SNAPSHOT</c>), and reads the setting back. On: Azure SQL's default, and
    /// what a database EF creates on any other SQL Server starts with, the scratch database among
    /// them (<c>DemoClaimService.ReadCandidatesAsync</c> says where that was read and measured).
    /// Off: SQL Server's own default, which a database made some other way has.
    /// </summary>
    internal static async Task SetRowVersioningAsync(DemoPoolDatabase database, bool on)
    {
        // The name is the fixture's own: "AzureBankPoolProof_" and 32 hexadecimal digits.
        var name = new SqlConnectionStringBuilder(database.ConnectionString).InitialCatalog;

        // The statement needs the database to itself; the connections the migration used are idle
        // in the pool.
        SqlConnection.ClearAllPools();
        await using var master = new SqlConnection(
            new SqlConnectionStringBuilder(database.ConnectionString) { InitialCatalog = "master", Pooling = false }.ConnectionString);
        await master.OpenAsync();
        await using (var alter = master.CreateCommand())
        {
            alter.CommandText =
                $"ALTER DATABASE [{name}] SET READ_COMMITTED_SNAPSHOT {(on ? "ON" : "OFF")} WITH ROLLBACK IMMEDIATE";
            await alter.ExecuteNonQueryAsync();
        }

        (await RowVersioningAsync(database)).Should().Be(on, "ARRANGE: the database reads committed data the way this row of the theory says");
    }

    internal static async Task<bool> RowVersioningAsync(DemoPoolDatabase database)
    {
        await using var db = database.NewContext();
        return await db.Database
            .SqlQueryRaw<bool>("SELECT is_read_committed_snapshot_on AS Value FROM sys.databases WHERE name = DB_NAME()")
            .SingleAsync();
    }

    /// <summary>Claims for every address at once, from one client, and returns the answers in the addresses' order.</summary>
    private static async Task<HttpResponseMessage[]> ClaimAtOnceAsync(HttpClient client, IReadOnlyList<string> addresses)
    {
        // One request first, so that the host's first-use work is done before the claims start.
        using (await client.GetAsync("/health/live"))
        {
        }

        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var claims = addresses
            .Select(address => Task.Run(async () =>
            {
                await go.Task;
                return await DemoVisitor.ClaimAsync(client, address);
            }))
            .ToArray();
        go.SetResult();
        return await Task.WhenAll(claims);
    }

    /// <summary>
    /// The lines the API wrote for the requests it answered 5xx, for a failure's message: a claim
    /// that loses a deadlock on a host that does not run it again is answered 503 and says so only
    /// in the log (error 1205), and a 503 with another cause must not be read as one.
    /// </summary>
    private static string WhatTheApiSaidOfItsFailures(CustomWebApplicationFactory api)
    {
        var lines = api.CapturedLog
            .Where(line => line.Contains("Service unavailable", StringComparison.Ordinal)
                || line.Contains("Unhandled", StringComparison.OrdinalIgnoreCase)
                || line.Contains("responded 5", StringComparison.Ordinal))
            .Select(line => line.Length <= 400 ? line : line[..400])
            .ToList();
        return lines.Count == 0 ? string.Empty : $". The API logged: {string.Join(" || ", lines)}";
    }

    /// <summary>Counts <c>azurebank.demo.claims</c> on the API's meter, by outcome.</summary>
    /// <remarks>
    /// The counter is one for the process. The tests of this collection run one at a time, so in a
    /// run of this category a count is this test's own; assertions say "at least" all the same, so
    /// a claim made by another class in a wider run cannot fail one.
    /// </remarks>
    private sealed class ClaimOutcomes : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentDictionary<string, long> _counts = new(StringComparer.Ordinal);

        public ClaimOutcomes()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == ApiMetrics.MeterName && instrument.Name == "azurebank.demo.claims")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (tag.Key == "azurebank.outcome" && tag.Value is string outcome)
                    {
                        _counts.AddOrUpdate(outcome, value, (_, sum) => sum + value);
                    }
                }
            });
            _listener.Start();
        }

        public long Of(string outcome) => _counts.GetValueOrDefault(outcome);

        public void Dispose() => _listener.Dispose();
    }

    /// <summary>
    /// Takes, from a second connection, every copy the claim is about to take: it fires on each of
    /// the claim's conditional updates and claims the copy that update names before it runs.
    /// </summary>
    /// <remarks>
    /// <see cref="OutOfBandClaimInterceptor"/> takes one copy, chosen by the test, once. This one
    /// takes whichever copy each statement names, every time, so a claim loses every candidate of
    /// every round. The copy's id is one of the statement's two <see cref="Guid"/> parameters; the
    /// other is the claim's own id, which names no row.
    /// </remarks>
    private sealed class EveryCandidateTakenFirstInterceptor(string connectionString) : DbCommandInterceptor
    {
        private readonly ConcurrentQueue<Guid> _takenInOrder = new();
        private int _taken;
        private int _statements;

        /// <summary>Copies taken out of band.</summary>
        public int Taken => Volatile.Read(ref _taken);

        /// <summary>The copies taken out of band, in the order the claim tried them.</summary>
        public IReadOnlyList<Guid> TakenInOrder => [.. _takenInOrder];

        /// <summary>The claim's conditional updates that were seen.</summary>
        public int Statements => Volatile.Read(ref _statements);

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var text = command.CommandText;
            if (text.Contains("UPDATE", StringComparison.Ordinal)
                && text.Contains("[DemoCopies]", StringComparison.Ordinal)
                && text.Contains("[ClaimedAt] IS NULL", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _statements);
                await using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken);
                foreach (var id in command.Parameters.Cast<DbParameter>().Select(p => p.Value).OfType<Guid>())
                {
                    await using var claim = connection.CreateCommand();
                    claim.CommandText =
                        "UPDATE [DemoCopies] SET [ClaimedAt] = SYSUTCDATETIME(), [ClaimId] = NEWID() "
                        + "WHERE [Id] = @id AND [ClaimedAt] IS NULL";
                    claim.Parameters.Add(new SqlParameter("@id", id));
                    if (await claim.ExecuteNonQueryAsync(cancellationToken) == 1)
                    {
                        Interlocked.Increment(ref _taken);
                        _takenInOrder.Enqueue(id);
                    }
                }
            }

            return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>Every statement a host sends, in the order it sends them, from <see cref="Start"/> on.</summary>
    private sealed class StatementsInOrder : DbCommandInterceptor
    {
        private readonly ConcurrentQueue<string> _texts = new();
        private int _recording;

        public void Start() => Volatile.Write(ref _recording, 1);

        public IReadOnlyList<string> Texts => [.. _texts];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData,
            InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Record(DbCommand command)
        {
            if (Volatile.Read(ref _recording) == 1)
            {
                _texts.Enqueue(command.CommandText.Trim());
            }
        }
    }

    /// <summary>The statements of one claim that is answered 200, on a pool of one.</summary>
    private static async Task<IReadOnlyList<string>> StatementsOfOneClaimAsync(DemoPoolDatabase database)
    {
        await database.BuildCopiesAsync(1);
        var api = database.DemoApi();
        var statements = new StatementsInOrder();
        api.AddInterceptor(statements);
        using var client = api.CreateClient();

        // From here: what the host sent while it started is not the claim's.
        statements.Start();
        using var response = await DemoVisitor.ClaimAsync(client, Visitor);
        await DemoVisitor.ClaimedAsync(response);
        return statements.Texts;
    }

    private static bool IsSelect(string statement) => statement.StartsWith("SELECT", StringComparison.Ordinal);

    /// <summary>
    /// A text that, of a claim's statements, only the read of candidates holds: it is the one that
    /// sorts by the seed instant. For an interceptor that picks a statement by one text; the tests
    /// that use it check what it picked with <see cref="IsTheCandidatesRead"/>.
    /// </summary>
    private const string OnlyInTheCandidatesRead = "[CreatedAt] DESC";

    private static bool IsTheCandidatesRead(string statement) =>
        IsSelect(statement)
        && statement.Contains("[DemoCopies]", StringComparison.Ordinal)
        && statement.Contains("[ClaimedAt] IS NULL", StringComparison.Ordinal);

    /// <summary>The statement that takes a copy if nobody has: the conditional update of the pool's row.</summary>
    private static bool IsTheConditionalClaim(string statement) =>
        statement.StartsWith("UPDATE", StringComparison.Ordinal)
        && statement.Contains("[DemoCopies]", StringComparison.Ordinal)
        && statement.Contains("[ClaimedAt] IS NULL", StringComparison.Ordinal);

    // ── The hosts ────────────────────────────────────────────────────────────────────────────────

    [SqlServerFact]
    public async Task TheDemoApi_StartsOnThePoolsDatabase_WithTheFlagOn_AndTheOrdinaryApiKeepsItOff()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var scratch = new SqlConnectionStringBuilder(database.ConnectionString).InitialCatalog;

        var demoApi = database.DemoApi(("Demo:Claim:MaxPerClientPerDay", "3"));
        var ordinaryApi = database.Api();

        var demo = DemoOf(demoApi);
        demo.Enabled.Should().BeTrue();
        demo.ClientKeySecret.Should().Be(CustomWebApplicationFactory.DemoClientKeySecret);
        demo.Claim.MaxPerClientPerDay.Should().Be(3, "the demo API carries the settings a test gives it");
        DatabaseOf(demoApi).Should().Be(scratch, "both hosts are on the database this test created");

        // The host the pool's own tests register users and sign in through. It stays as it was.
        DemoOf(ordinaryApi).Enabled.Should().BeFalse();
        DatabaseOf(ordinaryApi).Should().Be(scratch);
    }

    // ── One claim ────────────────────────────────────────────────────────────────────────────────

    [SqlServerFact]
    public async Task OnAPoolOfOne_TheFirstClaimIs200_AndTheSecond429PoolEmpty()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var free = (await database.BuildCopiesAsync(1)).Single();
        var api = database.DemoApi();
        api.CaptureLog(LogEventLevel.Information);
        using var client = api.CreateClient();
        using var outcomes = new ClaimOutcomes();

        var before = DateTime.UtcNow;
        using var first = await DemoVisitor.ClaimAsync(client, Visitor);
        var after = DateTime.UtcNow;

        var claim = await DemoVisitor.ClaimedAsync(first);
        var claimed = (await database.CopyAsync(free.Id))!;
        using (new AssertionScope())
        {
            claim.User.Id.Should().Be(free.Owner.Id);
            claim.Copy.Email.Should().Be(free.Owner.Email);
            claim.Copy.Contacts.Should().Equal(
                new[] { free.Jane.AzureTag, free.Mike.AzureTag }.Order(StringComparer.Ordinal),
                "the two users the owner can pay, sorted");

            // The row: claimed now, by this request, for this client.
            claimed.Row.ClaimedAt.Should().NotBeNull();
            claimed.Row.ClaimedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
            claimed.Row.ClaimId.Should().NotBeNull();
            claimed.Row.ClientKey.Should().Equal(KeyOf(Visitor));
            claimed.Row.ClientKey.Should().NotEqual(KeyOf(AnotherVisitor), "CONTROL: another address has another key");
            claimed.Row.Writes.Should().Be(0);
            claimed.Row.DeletedAt.Should().BeNull();

            // The owner: a password that is the answered one, and stamps that say the row changed.
            claimed.Owner.PasswordHash.Should().NotBeNullOrEmpty();
            new PasswordHasher<ApplicationUser>()
                .VerifyHashedPassword(claimed.Owner, claimed.Owner.PasswordHash!, claim.Copy.Password)
                .Should().NotBe(PasswordVerificationResult.Failed, "the hash stored is the hash of the password answered");
            claimed.Owner.SecurityStamp.Should().NotBeNullOrEmpty().And.NotBe(free.Owner.SecurityStamp);
            claimed.Owner.ConcurrencyStamp.Should().NotBeNullOrEmpty().And.NotBe(free.Owner.ConcurrencyStamp);

            // The two contacts: as they were. Nobody signs in as them.
            foreach (var (now, then) in new[] { (claimed.Jane, free.Jane), (claimed.Mike, free.Mike) })
            {
                now.PasswordHash.Should().BeNull();
                now.SecurityStamp.Should().Be(then.SecurityStamp);
                now.ConcurrencyStamp.Should().Be(then.ConcurrencyStamp);
            }

            (await GrantsOfAsync(database, free.Owner.Id)).Should().Be(1, "the claim opens one session");
            (await GrantsOfAsync(database, free.Jane.Id, free.Mike.Id)).Should().Be(0);
        }

        using var second = await DemoVisitor.ClaimAsync(client, AnotherVisitor);

        await ShouldBePoolEmptyAsync(second);
        using (new AssertionScope())
        {
            (await database.CopyAsync(free.Id))!.Row.ClientKey.Should().Equal(KeyOf(Visitor), "the refused claim changed nothing");
            outcomes.Of("claimed").Should().BeGreaterThanOrEqualTo(1);
            outcomes.Of("pool_empty").Should().BeGreaterThanOrEqualTo(1);

            // What is logged: the copy and the user by their ids, and neither the address a visitor
            // signs in with nor the password.
            api.CapturedLog.Should().Contain(line =>
                line.Contains($"Demo copy {free.Id} claimed for user {free.Owner.Id}", StringComparison.Ordinal));
            api.CapturedLog.Should().NotContain(line =>
                line.Contains(claim.Copy.Email, StringComparison.OrdinalIgnoreCase)
                || line.Contains(claim.Copy.Password, StringComparison.Ordinal));
        }
    }

    [SqlServerFact]
    public async Task TheAnsweredPassword_SignsIn_TheStampIsTheOwners_AndTheGrantEndsSixtyMinutesAfterTheClaim()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var free = (await database.BuildCopiesAsync(1)).Single();
        await using (var db = database.NewContext())
        {
            // A stamp no new user has, so the answer's can only have been read from this row.
            (await db.Users.Where(u => u.Id == free.Owner.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.SessionStamp, 7)))
                .Should().Be(1, "ARRANGE");
        }

        var api = database.DemoApi();
        DemoOf(api).Enabled.Should().BeTrue();
        api.Services.GetRequiredService<IOptions<JwtOptions>>().Value.RefreshTokenLifetimeMinutes
            .Should().Be(60, "ARRANGE: the test host's grant lives the default 60 minutes");
        using var client = api.CreateClient();

        // The real clock: a grant's expiry is counted from the wall clock, whatever the host's
        // TimeProvider says.
        var before = DateTime.UtcNow;
        using var response = await DemoVisitor.ClaimAsync(client, Visitor);
        var after = DateTime.UtcNow;

        var claim = await DemoVisitor.ClaimedAsync(response);
        using (new AssertionScope())
        {
            (response.Headers.CacheControl?.NoStore).Should().BeTrue("the answer carries a password");
            claim.Token.SessionStamp.Should().Be(7);
            claim.Token.RefreshToken.Should().NotBeNullOrWhiteSpace();
            claim.Token.RefreshTokenExpiresAt.Should().NotBeNull();
            claim.Token.RefreshTokenExpiresAt.Should().BeOnOrAfter(before.AddMinutes(60)).And.BeOnOrBefore(after.AddMinutes(60));

            await using var db = database.NewContext();
            var grant = await db.RefreshTokens.AsNoTracking().SingleAsync(t => t.UserId == free.Owner.Id);
            grant.ExpiresAt.Should().Be(claim.Token.RefreshTokenExpiresAt!.Value, "the answer names the expiry of the grant that was written");
            grant.RevokedAt.Should().BeNull();
        }

        using var signIn = await DemoVisitor.TrySignInAsync(client, claim.Copy.Email, claim.Copy.Password);
        signIn.StatusCode.Should().Be(
            HttpStatusCode.OK, "the answered password signs in to the copy ({0})", await signIn.Content.ReadAsStringAsync());
        var signedIn = (await signIn.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>(DemoVisitor.Json))!.Data!;
        signedIn.User.Id.Should().Be(free.Owner.Id);
        signedIn.Token.SessionStamp.Should().Be(7);
    }

    [SqlServerFact]
    public async Task ClaimedAt_AndTheCopysEnd_ComeFromTheHostsClock_InUtc()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var free = (await database.BuildCopiesAsync(1)).Single();
        var api = database.DemoApi();

        // Three hours and some ticks ahead of the wall clock: an instant no other clock gives.
        var instant = new DateTimeOffset(DateTime.UtcNow.AddHours(3).Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond + 1234567, TimeSpan.Zero);
        api.UseFakeClock(instant);
        using var client = api.CreateClient();

        using var response = await DemoVisitor.ClaimAsync(client, Visitor);

        var text = await response.Content.ReadAsStringAsync();
        var claim = await DemoVisitor.ClaimedAsync(response);
        var row = (await database.CopyAsync(free.Id))!.Row;
        using (new AssertionScope())
        {
            row.ClaimedAt.Should().Be(instant.UtcDateTime, "the claim is stamped by the host's clock, to the tick");
            claim.Copy.ExpiresAt.Should().Be(instant.UtcDateTime.AddHours(24), "a copy lives Demo:CopyLifetimeHours from its claim, 24 by default");

            using var body = JsonDocument.Parse(text);
            body.RootElement.GetProperty("data").GetProperty("copy").GetProperty("expiresAt").GetString()
                .Should().Be(instant.UtcDateTime.AddHours(24).ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    [SqlServerFact]
    public async Task ACopysEnd_FollowsTheLifetimeTheHostIsGiven()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var free = (await database.BuildCopiesAsync(1)).Single();
        using var client = database.DemoApi(("Demo:CopyLifetimeHours", "5")).CreateClient();

        using var response = await DemoVisitor.ClaimAsync(client, Visitor);

        var claim = await DemoVisitor.ClaimedAsync(response);
        var row = (await database.CopyAsync(free.Id))!.Row;
        claim.Copy.ExpiresAt.Should().Be(row.ClaimedAt!.Value.AddHours(5));
    }

    // ── Many claims at once ──────────────────────────────────────────────────────────────────────

    // Each of the two theories runs under both ways a database reads committed data, and its two
    // rows do not prove the same thing.
    //
    // WITH ROW VERSIONING (READ_COMMITTED_SNAPSHOT on: Azure SQL's default, and what a database EF
    // creates starts with) the host is the plain test host, which runs nothing again: a claim the
    // server refused as the victim of a deadlock would be answered 503 and fail the row. So a
    // green row shows that, read this way, no claim of that run was refused as a deadlock's victim
    // (DemoClaimService.ReadCandidatesAsync says in how many runs none was).
    //
    // WITHOUT IT (SQL Server's own default, for a database made some other way) two claims can
    // deadlock, rarely: the read of candidates goes from an entry of the index of free copies to
    // its row, and can hold the entry while it waits for a row that a claim taking that copy
    // holds, which then waits for the entry. The server refuses the read with error 1205.
    // DemoClaimService.ReadCandidatesAsync says what was measured. A deployed host runs the
    // refused claim again, and so does the host of this row (EnableSqlRetryOnFailure). What the
    // row proves is the outcome on such a host: each visitor is served or refused as the pool
    // allows, and no copy is given twice, even when a claim was run again on the way. On the plain
    // host the same row is red whenever the deadlock happens.

    [SqlServerTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EightParallelClaims_OnAPoolOfFive_GiveFiveDifferentCopies_AndThree429s(bool rowVersioning)
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        await SetRowVersioningAsync(database, rowVersioning);
        var copies = await database.BuildCopiesAsync(5);
        var api = database.DemoApi();
        if (!rowVersioning)
        {
            // As a deployed host: a claim that lost a deadlock is run again. Five different copies
            // and three refusals are due all the same.
            api.EnableSqlRetryOnFailure();
        }

        api.CaptureLog();
        using var client = api.CreateClient();
        var addresses = Enumerable.Range(1, 8).Select(i => $"198.51.100.{i}").ToList();

        var responses = await ClaimAtOnceAsync(client, addresses);

        responses.Select(r => (int)r.StatusCode).Order().Should().Equal(
            [200, 200, 200, 200, 200, 429, 429, 429],
            "five copies for eight visitors: five are served and three are told the pool is empty{0}",
            WhatTheApiSaidOfItsFailures(api));

        var served = new Dictionary<string, DemoClaimResponse>();
        for (var i = 0; i < responses.Length; i++)
        {
            if (responses[i].StatusCode == HttpStatusCode.OK)
            {
                served[addresses[i]] = await DemoVisitor.ClaimedAsync(responses[i]);
            }
            else
            {
                await ShouldBePoolEmptyAsync(responses[i]);
            }
        }

        var rows = await RowsAsync(database);
        using (new AssertionScope())
        {
            served.Values.Select(c => c.Copy.Email).Distinct().Should().HaveCount(5, "no copy is given to two visitors");
            served.Values.Select(c => c.User.Id).Should().BeEquivalentTo(copies.Select(c => c.Owner.Id));

            rows.Should().HaveCount(5).And.OnlyContain(row => row.ClaimedAt != null);
            rows.Select(row => row.ClaimId).Distinct().Should().HaveCount(5);

            // Each visitor was answered for the copy that carries that visitor's key.
            foreach (var (address, claim) in served)
            {
                rows.Single(row => row.OwnerUserId == claim.User.Id).ClientKey.Should().Equal(KeyOf(address));
            }

            (await GrantsOfAsync(database, [.. copies.Select(c => c.Owner.Id)])).Should().Be(5, "one session per copy");
            (await RowVersioningAsync(database)).Should().Be(rowVersioning, "the claims ran under the setting this row names");
        }
    }

    [SqlServerTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TwelveParallelClaims_OnAPoolOfTwenty_AllGetDifferentCopies(bool rowVersioning)
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        await SetRowVersioningAsync(database, rowVersioning);
        var copies = await database.BuildCopiesAsync(20);
        var api = database.DemoApi();
        if (!rowVersioning)
        {
            // As a deployed host: a claim that lost a deadlock is run again. Twelve different
            // copies are due all the same.
            api.EnableSqlRetryOnFailure();
        }

        // Twelve requests at once would each wait for one of the twelve connections the test host's
        // pool holds, which is another test's subject; this one is given room.
        api.SetConnectionString(new SqlConnectionStringBuilder(database.ConnectionString) { MaxPoolSize = 30 }.ConnectionString);
        api.CaptureLog();
        using var client = api.CreateClient();
        var addresses = Enumerable.Range(1, 12).Select(i => $"198.51.100.{i}").ToList();

        var responses = await ClaimAtOnceAsync(client, addresses);

        responses.Select(r => (int)r.StatusCode).Should().OnlyContain(
            status => status == 200, "twenty copies for twelve visitors: nobody is refused{0}", WhatTheApiSaidOfItsFailures(api));
        var claims = new List<DemoClaimResponse>();
        foreach (var response in responses)
        {
            claims.Add(await DemoVisitor.ClaimedAsync(response));
        }

        var rows = await RowsAsync(database);
        using (new AssertionScope())
        {
            claims.Select(c => c.Copy.Email).Distinct().Should().HaveCount(12, "no copy is given to two visitors");
            claims.Select(c => c.User.Id).Should().BeSubsetOf(copies.Select(c => c.Owner.Id));
            rows.Count(row => row.ClaimedAt != null).Should().Be(12);
            rows.Count(row => row.ClaimedAt == null).Should().Be(8);
            rows.Where(row => row.ClaimedAt != null).Select(row => row.ClaimId).Distinct().Should().HaveCount(12);
            (await RowVersioningAsync(database)).Should().Be(rowVersioning);
        }
    }

    // Two claims at once deadlock when each holds what the other waits for. The two theories above
    // do not show that claims never do: a deadlock needs an unlucky moment, and without row
    // versioning their host runs the refused claim again. These two hold, on one claim's own
    // statements, two rules about what a claim asks for and when, each found by a deadlock the
    // theories did show while their host ran nothing again (error 1205, with row versioning off).
    // The rule about users leaves its deadlock no moment: a claim that has written a user reads no
    // other user. The rule about the candidates does not: the read asks for nothing the index of
    // free copies lacks, and the server goes from the index to the row all the same, so that
    // deadlock is rare and not gone (DemoClaimService.ReadCandidatesAsync).

    [SqlServerFact]
    public async Task TheCandidatesRead_AsksOnlyForWhatTheIndexOfFreeCopiesHolds()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();

        var statements = await StatementsOfOneClaimAsync(database);

        var read = statements.Should().ContainSingle(
            t => IsSelect(t) && t.Contains("[DemoCopies]") && t.Contains("[ClaimedAt] IS NULL"),
            "one round of candidates is read for a claim that takes its first candidate").Subject;
        var asked = Regex.Matches(read[..read.IndexOf("FROM", StringComparison.Ordinal)], @"\[\w+\]\.\[(\w+)\]")
            .Select(m => m.Groups[1].Value)
            .ToList();
        var condition = Regex.Match(read, @"WHERE (.*?)\s+ORDER BY", RegexOptions.Singleline).Groups[1].Value;

        await using var db = database.NewContext();
        var entity = db.Model.FindEntityType(typeof(DemoCopy))!;
        var free = entity.GetIndexes().Single(i => i.GetDatabaseName() == "IX_DemoCopies_Free");
        var held = free.Properties.Concat(entity.FindPrimaryKey()!.Properties).Select(p => p.GetColumnName()).ToList();

        using (new AssertionScope())
        {
            // The statement asks for nothing that is only in the row, and its condition is the
            // index's own filter. A statement can do no more, and it does not keep SQL Server in
            // the index: the index does not hold ClaimedAt, and the plan looks each row up to
            // check it there (DemoClaimService.ReadCandidatesAsync says what was measured). Held
            // all the same: a column asked for from the row would send the read there whatever
            // the index held.
            asked.Should().NotBeEmpty().And.BeSubsetOf(
                held, "the read asks only for what the index of free copies holds, {0}", string.Join(", ", held));
            Regex.Replace(condition, @"\[\w+\]\.", string.Empty).Should().Be(
                free.GetFilter(), "the read's condition is the index's own filter");
        }
    }

    [SqlServerFact]
    public async Task OnceAClaimHasWrittenAUser_ItReadsNoUserButItsOwnerByKey()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();

        var statements = await StatementsOfOneClaimAsync(database);

        var firstWrite = statements.ToList().FindIndex(
            t => t.StartsWith("UPDATE", StringComparison.Ordinal) && t.Contains("[AspNetUsers]", StringComparison.Ordinal));
        firstWrite.Should().BeGreaterThanOrEqualTo(0, "the claim gives the owner a password");
        using (new AssertionScope())
        {
            // A read of users by anything but a key can be answered by reading every user, another
            // copy's owner among them. A claim that already holds its own owner's row would then
            // wait for a claim that holds that one, and that claim for this one.
            statements.Take(firstWrite).Should().Contain(
                t => IsSelect(t) && t.Contains("[DemoCopyId] = @"),
                "the copy's contacts are read before the claim writes any user");
            var later = statements.Skip(firstWrite + 1)
                .Where(t => IsSelect(t) && t.Contains("[AspNetUsers]", StringComparison.Ordinal))
                .ToList();
            later.Should().NotBeEmpty("CONTROL: the owner is read after its password is written");
            later.Should().OnlyContain(
                t => Regex.IsMatch(t, @"WHERE \[\w+\]\.\[Id\] = @\w+$"),
                "after its first write to a user, a claim reads its own owner by key and nobody else");
        }
    }

    // ── A copy taken by somebody else first ──────────────────────────────────────────────────────

    [SqlServerFact]
    public async Task AClaimWhoseOnlyCandidateIsTakenFirst_Is429_AndChangesNothingElse()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var free = (await database.BuildCopiesAsync(1)).Single();
        var api = database.DemoApi();

        // Another visitor's claim commits between this claim's choice of the copy and its own
        // conditional update, which then affects no row.
        var race = new OutOfBandClaimInterceptor(database.ConnectionString, free.Id);
        api.AddInterceptor(race);
        using var client = api.CreateClient();

        using var response = await DemoVisitor.ClaimAsync(client, Visitor);

        race.Fired.Should().BeTrue("the out-of-band claim must actually have run, else the test proves nothing");
        race.OutOfBandRowsAffected.Should().Be(1, "and it must have won: the copy was still free when it ran");
        await ShouldBePoolEmptyAsync(response);

        var after = (await database.CopyAsync(free.Id))!;
        using (new AssertionScope())
        {
            after.Row.ClaimedAt.Should().NotBeNull("the claim that stands is the other visitor's");
            after.Row.ClientKey.Should().BeNull("this request's key is on no row");
            after.Owner.PasswordHash.Should().BeNull("the loser set no password on a copy it did not get");
            after.Owner.SecurityStamp.Should().Be(free.Owner.SecurityStamp);
            (await GrantsOfAsync(database, free.UserIds)).Should().Be(0, "and opened no session");
        }
    }

    [SqlServerFact]
    public async Task AClaimWhoseCandidateIsTakenFirst_TakesAnotherCopy()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var copies = await database.BuildCopiesAsync(2);
        var (contested, other) = (copies[0], copies[1]);

        // The other copy is too old to count, so the claim tries the contested one first, whatever
        // the shuffle: a fresh copy goes before an old one.
        await database.BackdateSeedAsync(other.Id, DateTime.UtcNow.AddHours(-50));
        var api = database.DemoApi();
        var race = new OutOfBandClaimInterceptor(database.ConnectionString, contested.Id);
        api.AddInterceptor(race);
        using var client = api.CreateClient();

        using var response = await DemoVisitor.ClaimAsync(client, Visitor);

        race.Fired.Should().BeTrue("the out-of-band claim must actually have run, else the test proves nothing");
        race.OutOfBandRowsAffected.Should().Be(1);
        var claim = await DemoVisitor.ClaimedAsync(response);
        var (lost, won) = ((await database.CopyAsync(contested.Id))!, (await database.CopyAsync(other.Id))!);
        using (new AssertionScope())
        {
            claim.User.Id.Should().Be(other.Owner.Id, "the copy somebody else took is not this visitor's");
            claim.Copy.Email.Should().Be(other.Owner.Email);

            won.Row.ClientKey.Should().Equal(KeyOf(Visitor));
            won.Owner.PasswordHash.Should().NotBeNullOrEmpty();

            lost.Row.ClaimedAt.Should().NotBeNull();
            lost.Row.ClientKey.Should().BeNull();
            lost.Owner.PasswordHash.Should().BeNull("nothing of this claim is on the copy it lost");
            (await GrantsOfAsync(database, contested.UserIds)).Should().Be(0);
            (await GrantsOfAsync(database, other.Owner.Id)).Should().Be(1);
        }
    }

    [SqlServerFact]
    public async Task AClaimThatLosesEveryCandidateOfThreeRounds_Is429PoolEmpty_AndStopsThere()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();

        // Sixty-one free rows: three rounds of twenty candidates, and one row more. A claim that
        // went on to a fourth round would reach it.
        var seededAt = DateTime.UtcNow.AddMinutes(-90);
        await AddBareCopiesAsync(database, 61, (row, i) => row.CreatedAt = seededAt.AddMinutes(i));
        var api = database.DemoApi();
        api.CaptureLog(LogEventLevel.Warning);
        var thief = new EveryCandidateTakenFirstInterceptor(database.ConnectionString);
        api.AddInterceptor(thief);
        using var client = api.CreateClient();
        using var outcomes = new ClaimOutcomes();

        using var response = await DemoVisitor.ClaimAsync(client, Visitor);

        await ShouldBePoolEmptyAsync(response);
        var rows = await RowsAsync(database);
        using (new AssertionScope())
        {
            thief.Statements.Should().Be(60, "twenty candidates a round, three rounds, and no more");
            thief.Taken.Should().Be(60, "each was taken first, or the test proves nothing");
            rows.Count(row => row.ClaimedAt == null).Should().Be(1, "the sixty-first row was never tried");
            rows.Should().NotContain(row => row.ClientKey != null, "this request's key is on no row");
            api.CapturedLog.Should().Contain(line =>
                line.Contains("A demo claim lost every candidate in 3 rounds", StringComparison.Ordinal));
            outcomes.Of("lost_races").Should().BeGreaterThanOrEqualTo(1);
        }
    }

    // ── A claim that fails part of the way ───────────────────────────────────────────────────────

    [SqlServerFact]
    public async Task AGrantThatCannotBeWritten_LeavesTheCopyFree_AndWithoutAPassword()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var free = (await database.BuildCopiesAsync(1)).Single();
        var api = database.DemoApi();
        var refused = FailingCommandInterceptor.OnText("INSERT", "[RefreshTokens]");
        api.AddInterceptor(refused);
        using var client = api.CreateClient();

        using var response = await DemoVisitor.ClaimAsync(client, Visitor);

        refused.Failures.Should().BeGreaterThan(0, "the grant's insert must actually have been refused, else the test proves nothing");
        ((int)response.StatusCode).Should().BeGreaterThanOrEqualTo(500, await response.Content.ReadAsStringAsync());

        var after = (await database.CopyAsync(free.Id))!;
        using (new AssertionScope())
        {
            after.Row.ClaimedAt.Should().BeNull("the claim and the password were written before the grant, and went back with it");
            after.Row.ClaimId.Should().BeNull();
            after.Row.ClientKey.Should().BeNull();
            after.Owner.PasswordHash.Should().BeNull();
            after.Owner.SecurityStamp.Should().Be(free.Owner.SecurityStamp);
            after.Owner.ConcurrencyStamp.Should().Be(free.Owner.ConcurrencyStamp);
            (await GrantsOfAsync(database, free.UserIds)).Should().Be(0);
        }
    }

    [SqlServerFact]
    public async Task AFreeCopyWhoseOwnerAlreadyHasAPassword_Is500_AndStaysFree()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var free = (await database.BuildCopiesAsync(1)).Single();

        // Something other than a claim gave the owner a password. Handing the copy out would hand
        // a visitor a copy that somebody else holds a password for.
        await database.GiveOwnerAPasswordAsync(free);
        var before = (await database.CopyAsync(free.Id))!;
        before.Owner.PasswordHash.Should().NotBeNullOrEmpty("ARRANGE");
        var api = database.DemoApi();
        api.CaptureLog(LogEventLevel.Error);
        using var client = api.CreateClient();

        using var response = await DemoVisitor.ClaimAsync(client, Visitor);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError, await response.Content.ReadAsStringAsync());
        var after = (await database.CopyAsync(free.Id))!;
        using (new AssertionScope())
        {
            after.Row.ClaimedAt.Should().BeNull("the claim was rolled back");
            after.Row.ClaimId.Should().BeNull();
            after.Row.ClientKey.Should().BeNull();
            after.Owner.PasswordHash.Should().Be(before.Owner.PasswordHash, "the password it had is the password it has");
            after.Owner.SecurityStamp.Should().Be(before.Owner.SecurityStamp);
            (await GrantsOfAsync(database, free.UserIds)).Should().Be(0);
            api.CapturedLog.Should().Contain(line =>
                line.Contains($"Demo copy {free.Id} is free and its owner has a password; the claim was rolled back", StringComparison.Ordinal));
        }
    }

    [SqlServerFact]
    public async Task ACommitWhoseAnswerIsLost_ClaimsOneCopy_AndAnswersForIt()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var copies = await database.BuildCopiesAsync(2);
        var api = database.DemoApi();

        // The claim's transaction commits, and the caller is told it failed, with a fault the
        // retrying strategy runs the work again for unless it is told the work landed.
        api.EnableSqlRetryOnFailure();
        var lost = new LostCommitAnswerInterceptor("UPDATE", "[DemoCopies]");
        api.AddInterceptor(lost);
        using var client = api.CreateClient();

        using var response = await DemoVisitor.ClaimAsync(client, Visitor);

        lost.Fired.Should().BeTrue("the answer must actually have been lost, else the test proves nothing");
        var rows = await RowsAsync(database);
        rows.Count(row => row.ClaimedAt != null).Should().Be(1, "a claim whose commit landed is not run again: one visitor, one copy");
        var taken = rows.Single(row => row.ClaimedAt != null);

        var claim = await DemoVisitor.ClaimedAsync(response);
        using (new AssertionScope())
        {
            claim.User.Id.Should().Be(taken.OwnerUserId, "the answer is for the copy that was claimed");
            taken.ClientKey.Should().Equal(KeyOf(Visitor));
            (await GrantsOfAsync(database, [.. copies.Select(c => c.Owner.Id)])).Should().Be(1, "and one session");
        }

        using var signIn = await DemoVisitor.TrySignInAsync(client, claim.Copy.Email, claim.Copy.Password);
        signIn.StatusCode.Should().Be(HttpStatusCode.OK, "the password answered is the one that was committed");
    }

    [SqlServerFact]
    public async Task AClaimRunAgainAfterATransientFault_ClaimsOneCopy_AndWritesOneGrant()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var copies = await database.BuildCopiesAsync(2);
        var api = database.DemoApi();

        // The grant's insert fails once, with a fault the retrying strategy runs the whole claim
        // again for. By then the first attempt had taken a copy, written a password and handed the
        // context a grant to insert: the first two went back with the transaction, and the third
        // stays in the context unless the next attempt starts by forgetting it.
        api.EnableSqlRetryOnFailure();
        var fault = new TransientFailureInterceptor("INSERT INTO [RefreshTokens]");
        api.AddInterceptor(fault);
        using var client = api.CreateClient();

        using var response = await DemoVisitor.ClaimAsync(client, Visitor);

        fault.Fired.Should().BeTrue("the grant's insert must actually have failed once, else the test proves nothing");
        var claim = await DemoVisitor.ClaimedAsync(response);
        var rows = await RowsAsync(database);
        var after = await database.CopiesAsync();
        using (new AssertionScope())
        {
            rows.Where(row => row.ClaimedAt != null).Select(row => row.OwnerUserId).Should().Equal(
                [claim.User.Id], "one copy is claimed, the one the answer is for");
            after.Where(copy => copy.Owner.PasswordHash != null).Select(copy => copy.Owner.Id).Should().Equal(
                [claim.User.Id], "and no password is left on a copy the first attempt took");
            (await GrantsOfAsync(database, [.. copies.Select(c => c.Owner.Id)])).Should().Be(
                1, "the first attempt's grant was never written, and is not written with the second's");
        }

        using var signIn = await DemoVisitor.TrySignInAsync(client, claim.Copy.Email, claim.Copy.Password);
        signIn.StatusCode.Should().Be(HttpStatusCode.OK, "the password answered is the one the second attempt committed");
    }

    // Without row versioning the server can refuse a claim's read of candidates as the victim of a
    // deadlock with another claim (the note above the two theories). To EF's retrying strategy
    // that refusal, error 1205, is a transient fault: one it runs the work again for
    // (ServiceUnavailableExceptionHandlerTests asks EF's own list about 1205). The two tests below
    // do not make a deadlock. They refuse the read with the error SqlClient reports to a
    // deadlock's victim (number 1205, class 13), built as ServiceUnavailableExceptionHandlerTests
    // builds its errors (SqlErrors) and thrown before the statement reaches the server, and hold
    // what a host does with a read refused that way. A deadlock of the server's own is met, when
    // one happens, by the two theories.

    /// <summary>The error SqlClient reports to the victim of a deadlock, built here: no deadlock happens.</summary>
    private static SqlException TheErrorOfADeadlocksVictim() => SqlErrors.Of(number: 1205, errorClass: 13);

    [SqlServerFact]
    public async Task AClaimWhoseReadOfCandidatesIsRefusedOnce_IsRunAgain_AndClaimsOneCopy()
    {
        // CONTROL: green as written (the product already ran a claim again). Seen red with
        // the strategy taken off this host: 503 where 200 was due.
        await using var database = await DemoPoolDatabase.CreateAsync();
        var copies = await database.BuildCopiesAsync(2);
        var api = database.DemoApi();

        // As a deployed host: work the database refuses with an error on EF's list is run again.
        api.EnableSqlRetryOnFailure();
        var statements = new StatementsInOrder();
        var fault = new TransientFailureInterceptor(OnlyInTheCandidatesRead, TheErrorOfADeadlocksVictim);

        // In this order: a statement is recorded before it is refused.
        api.AddInterceptor(statements);
        api.AddInterceptor(fault);
        using var client = api.CreateClient();

        statements.Start();
        using var response = await DemoVisitor.ClaimAsync(client, Visitor);
        var issued = statements.Texts;

        fault.Fired.Should().BeTrue("the read must actually have been refused once, else the test proves nothing");
        var claim = await DemoVisitor.ClaimedAsync(response);
        var rows = await RowsAsync(database);
        var after = await database.CopiesAsync();

        // What the host issued about the pool, in order: the claim's statements that name it, the
        // refused one among them (recorded, and never sent), and nothing a background service of
        // the host issued in between (none of them names the pool).
        var aboutThePool = issued.Where(t => t.Contains("[DemoCopies]", StringComparison.Ordinal)).ToList();
        using (new AssertionScope())
        {
            issued.Where(t => t.Contains(OnlyInTheCandidatesRead, StringComparison.Ordinal)).Should()
                .HaveCount(2, "the statement that was refused is issued a second time")
                .And.OnlyContain(t => IsTheCandidatesRead(t), "and it is the read of candidates");
            aboutThePool.Skip(2).Take(2).Should().Equal(
                aboutThePool.Take(2), "the claim is run again from its first statement, not from the one that was refused");
            issued.Count(IsTheConditionalClaim).Should().Be(1, "the second attempt takes its first candidate, and the first took none");

            rows.Where(row => row.ClaimedAt != null).Select(row => row.OwnerUserId).Should().Equal(
                [claim.User.Id], "one copy is claimed, the one the answer is for");
            rows.Single(row => row.OwnerUserId == claim.User.Id).ClientKey.Should().Equal(KeyOf(Visitor));
            after.Where(copy => copy.Owner.PasswordHash != null).Select(copy => copy.Owner.Id).Should().Equal(
                [claim.User.Id], "one owner has a password, the answer's");
            (await GrantsOfAsync(database, [.. copies.Select(c => c.Owner.Id)])).Should().Be(1, "and one session is open");
        }

        using var signIn = await DemoVisitor.TrySignInAsync(client, claim.Copy.Email, claim.Copy.Password);
        signIn.StatusCode.Should().Be(HttpStatusCode.OK, "the answered password signs in");
    }

    [SqlServerFact]
    public async Task OnAHostThatRunsNothingAgain_TheSameRefusalIs503_AndNothingIsClaimed()
    {
        // CONTROL: green as written. The plain test host has no retrying strategy: the
        // refusal the test above survives reaches the visitor here, as the outage 503, so what
        // answers 200 there is the strategy.
        await using var database = await DemoPoolDatabase.CreateAsync();
        var copies = await database.BuildCopiesAsync(2);
        var api = database.DemoApi();
        var statements = new StatementsInOrder();
        var fault = new TransientFailureInterceptor(OnlyInTheCandidatesRead, TheErrorOfADeadlocksVictim);
        api.AddInterceptor(statements);
        api.AddInterceptor(fault);
        api.CaptureLog();
        using var client = api.CreateClient();

        statements.Start();
        using var response = await DemoVisitor.ClaimAsync(client, Visitor);
        var issued = statements.Texts;

        fault.Fired.Should().BeTrue("the read must actually have been refused, else the test proves nothing");
        await DatabaseUnavailableSqlServerTests.AssertServiceUnavailableAsync(response, applied: null);
        WhatTheApiSaidOfItsFailures(api).Should().Contain(
            "SQL errors [1205]", "what the visitor was answered 503 for is the refusal of a deadlock's victim");
        var after = await database.CopiesAsync();
        using (new AssertionScope())
        {
            issued.Where(t => t.Contains(OnlyInTheCandidatesRead, StringComparison.Ordinal)).Should()
                .HaveCount(1, "the statement that was refused is not issued a second time")
                .And.OnlyContain(t => IsTheCandidatesRead(t), "and it is the read of candidates");
            issued.Count(IsTheConditionalClaim).Should().Be(0, "no copy was tried");

            after.Should().HaveCount(2).And.OnlyContain(
                copy => copy.Row.ClaimedAt == null && copy.Row.ClaimId == null && copy.Row.ClientKey == null,
                "both copies are still free");
            after.Should().OnlyContain(copy => copy.Owner.PasswordHash == null, "and no owner has a password");
            (await GrantsOfAsync(database, [.. copies.Select(c => c.Owner.Id)])).Should().Be(0, "and no session is open");
        }
    }

    [SqlServerFact]
    public async Task AWholeRowWriteOfAnOwnerReadBeforeTheClaim_IsRefused_AndThePasswordStillSignsIn()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var free = (await database.BuildCopiesAsync(1)).Single();
        var api = database.DemoApi();
        using var client = api.CreateClient();

        // Identity writes every column of a user it read earlier. A copy of the owner read while
        // the copy was free still says "no password".
        using var scope = api.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var readBefore = await users.FindByIdAsync(free.Owner.Id.ToString());
        readBefore!.PasswordHash.Should().BeNull("ARRANGE");

        using var response = await DemoVisitor.ClaimAsync(client, Visitor);
        var claim = await DemoVisitor.ClaimedAsync(response);

        var written = await users.UpdateAsync(readBefore);

        using (new AssertionScope())
        {
            written.Succeeded.Should().BeFalse("the row changed since it was read, and the write says so instead of taking the password back");
            written.Errors.Select(e => e.Code).Should().Contain("ConcurrencyFailure");
            (await database.CopyAsync(free.Id))!.Owner.PasswordHash.Should().NotBeNullOrEmpty();
        }

        using var signIn = await DemoVisitor.TrySignInAsync(client, claim.Copy.Email, claim.Copy.Password);
        signIn.StatusCode.Should().Be(HttpStatusCode.OK, await signIn.Content.ReadAsStringAsync());
    }

    // ── The daily cap of one client ──────────────────────────────────────────────────────────────

    [SqlServerFact]
    public async Task PastItsCap_AClientIs429DailyLimit_WithRetryAfter_AndAnotherAddressStillClaims()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        await database.BuildCopiesAsync(5);
        using var client = database.DemoApi(("Demo:Claim:MaxPerClientPerDay", "3")).CreateClient();
        using var outcomes = new ClaimOutcomes();

        for (var i = 1; i <= 3; i++)
        {
            using var allowed = await DemoVisitor.ClaimAsync(client, Visitor);
            allowed.StatusCode.Should().Be(HttpStatusCode.OK, "claim {0} of 3 is inside the cap", i);
        }

        using var fourth = await DemoVisitor.ClaimAsync(client, Visitor);

        var text = await fourth.Content.ReadAsStringAsync();
        fourth.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, text);
        using var body = JsonDocument.Parse(text);
        using (new AssertionScope())
        {
            body.RootElement.GetProperty("errorCode").GetString().Should().Be(ErrorCodes.DemoDailyLimit);

            // The first of the three leaves the 24 hours a day after it was made, moments ago.
            var seconds = body.RootElement.GetProperty("retryAfterSeconds").GetInt32();
            seconds.Should().BeInRange(86_340, 86_400);
            fourth.Headers.GetValues("Retry-After").Should().Equal(
                seconds.ToString(System.Globalization.CultureInfo.InvariantCulture));

            var rows = await RowsAsync(database);
            rows.Count(row => row.ClaimedAt != null).Should().Be(3, "the refused claim took no copy");
            rows.Where(row => row.ClaimedAt != null).Select(row => Convert.ToHexString(row.ClientKey ?? []))
                .Should().OnlyContain(key => key == Convert.ToHexString(KeyOf(Visitor)));
            outcomes.Of("daily_limit").Should().BeGreaterThanOrEqualTo(1);
        }

        using var other = await DemoVisitor.ClaimAsync(client, AnotherVisitor);
        other.StatusCode.Should().Be(HttpStatusCode.OK, "the cap is one client's, and two copies are still free");
    }

    [SqlServerFact]
    public async Task TheDailyLimitsRetryAfter_IsWhenTheClientIsBackUnderItsCap()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var copies = await database.BuildCopiesAsync(5);

        // Four claims of this client inside the last 24 hours, against a cap of 3: one more than
        // the cap, as two claims at once can leave it. The oldest leaves the 24 hours in 1 hour, and
        // the client is then still at its cap. It is under it when the second oldest leaves, in 4.
        var now = DateTime.UtcNow;
        var agesInHours = new[] { 23, 20, 2, 1 };
        for (var i = 0; i < agesInHours.Length; i++)
        {
            await database.MarkClaimedAsync(copies[i].Id, now.AddHours(-agesInHours[i]), KeyOf(Visitor));
        }

        using var client = database.DemoApi(("Demo:Claim:MaxPerClientPerDay", "3")).CreateClient();

        using var response = await DemoVisitor.ClaimAsync(client, Visitor);

        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, text);
        using var body = JsonDocument.Parse(text);
        using (new AssertionScope())
        {
            body.RootElement.GetProperty("errorCode").GetString().Should().Be(ErrorCodes.DemoDailyLimit);
            body.RootElement.GetProperty("retryAfterSeconds").GetInt32().Should().BeInRange(
                (4 * 3600) - 60, 4 * 3600, "in 1 hour the client would only be refused again");
            (await database.CopyAsync(copies[4].Id))!.Row.ClaimedAt.Should().BeNull("the one free copy is still free");
        }
    }

    [SqlServerFact]
    public async Task AClaimOlderThan24Hours_IsNotCounted_AndAnotherClientsNeverIs()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var copies = await database.BuildCopiesAsync(3);

        // Against a cap of 1: this client's claim of 24 hours and a minute ago, and another
        // client's of a minute ago. Neither is this client's claim of today.
        var now = DateTime.UtcNow;
        await database.MarkClaimedAsync(copies[0].Id, now.AddHours(-24).AddMinutes(-1), KeyOf(Visitor));
        await database.MarkClaimedAsync(copies[1].Id, now.AddMinutes(-1), KeyOf(AnotherVisitor));
        using var client = database.DemoApi(("Demo:Claim:MaxPerClientPerDay", "1")).CreateClient();

        using var first = await DemoVisitor.ClaimAsync(client, Visitor);
        using var second = await DemoVisitor.ClaimAsync(client, Visitor);

        using (new AssertionScope())
        {
            first.StatusCode.Should().Be(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
            second.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "CONTROL: the claim just made is counted");
            (await DemoVisitor.ErrorCodeOfAsync(second)).Should().Be(ErrorCodes.DemoDailyLimit);
        }
    }

    [SqlServerFact]
    public async Task WithTheCapAt1000_FiftyEarlierClaimsOfOneClient_DoNotStopTheNext()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        await database.BuildCopiesAsync(1);

        // Fifty claims of this client in the last hour. The cap counts rows of the pool, so a row
        // needs no users behind it to be counted.
        var now = DateTime.UtcNow;
        await AddBareCopiesAsync(database, 50, (row, i) =>
        {
            row.CreatedAt = now.AddHours(-2);
            row.ClaimedAt = now.AddMinutes(-(i + 1));
            row.ClaimId = Guid.CreateVersion7();
            row.ClientKey = KeyOf(Visitor);
        });

        // CONTROL: under the default cap of 10 the same fifty refuse this client, so they are counted.
        using (var defaultCap = database.DemoApi().CreateClient())
        {
            using var refused = await DemoVisitor.ClaimAsync(defaultCap, Visitor);
            refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, await refused.Content.ReadAsStringAsync());
            (await DemoVisitor.ErrorCodeOfAsync(refused)).Should().Be(ErrorCodes.DemoDailyLimit);
        }

        using var client = database.DemoApi(("Demo:Claim:MaxPerClientPerDay", "1000")).CreateClient();

        using var response = await DemoVisitor.ClaimAsync(client, Visitor);

        response.StatusCode.Should().Be(
            HttpStatusCode.OK, "1000 is the cap a deployment behind one shared address sets ({0})", await response.Content.ReadAsStringAsync());
    }

    // ── Which copy ───────────────────────────────────────────────────────────────────────────────

    [SqlServerFact]
    public async Task AFreshCopyIsHandedOutBeforeOneTooOldToCount_AndAnOldOneBeforeNone()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var copies = await database.BuildCopiesAsync(6);
        var fresh = copies.Take(3).ToList();
        var old = copies.Skip(3).ToList();

        // Older than Demo:Pool:MaxFreeAgeHours, 44 by default: a run of the pool's job no longer
        // counts them as free copies, and they are still there to be claimed.
        foreach (var copy in old)
        {
            await database.BackdateSeedAsync(copy.Id, DateTime.UtcNow.AddHours(-50));
        }

        using var client = database.DemoApi().CreateClient();
        var owners = new List<Guid>();
        for (var i = 1; i <= 6; i++)
        {
            using var response = await DemoVisitor.ClaimAsync(client, $"198.51.100.{i}");
            owners.Add((await DemoVisitor.ClaimedAsync(response)).User.Id);
        }

        using (new AssertionScope())
        {
            owners.Take(3).Should().BeEquivalentTo(
                fresh.Select(c => c.Owner.Id), "while a fresh copy is free, no visitor is given a history that ends two days ago");
            owners.Skip(3).Should().BeEquivalentTo(
                old.Select(c => c.Owner.Id), "and an old copy is better than none");
        }

        using var seventh = await DemoVisitor.ClaimAsync(client, "198.51.100.7");
        await ShouldBePoolEmptyAsync(seventh);
    }

    [SqlServerFact]
    public async Task HowOldAFreeCopyStillGoesFirst_IsWhatTheHostIsGiven()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var copies = await database.BuildCopiesAsync(3);

        // Fifty hours old: too old to count under the default of 44, young enough under 60.
        foreach (var copy in copies)
        {
            await database.BackdateSeedAsync(copy.Id, DateTime.UtcNow.AddHours(-50));
        }

        // Sixteen rows of seventy hours, too old under either, with no users behind them: a claim
        // that took one of these is answered 500. Nineteen free rows, so one round reads them all.
        // Were the three no fresher than the sixteen, three claims would take the three first in
        // one run of 969 (3/19 x 2/18 x 1/17).
        var seededAt = DateTime.UtcNow.AddHours(-70);
        await AddBareCopiesAsync(database, 16, (row, i) => row.CreatedAt = seededAt.AddMinutes(-i));
        using var client = database.DemoApi(("Demo:Pool:MaxFreeAgeHours", "60")).CreateClient();

        var owners = new List<Guid>();
        for (var i = 1; i <= 3; i++)
        {
            using var response = await DemoVisitor.ClaimAsync(client, $"198.51.100.{i}");
            owners.Add((await DemoVisitor.ClaimedAsync(response)).User.Id);
        }

        owners.Should().BeEquivalentTo(
            copies.Select(c => c.Owner.Id), "under Demo:Pool:MaxFreeAgeHours of 60 a copy of 50 hours is fresh, and goes before one of 70");
    }

    [SqlServerFact]
    public async Task WithMoreFreeCopiesThanARoundReads_TheFreshOneIsStillHandedOutFirst()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var fresh = (await database.BuildCopiesAsync(1)).Single();

        // Twenty-one rows too old to count, one more than a round reads, with no users behind them.
        // A round reads the newest twenty free rows, so the fresh copy is always among them. Read
        // oldest first, a round would hold none but these, and the claim would take one of them.
        var seededAt = DateTime.UtcNow.AddHours(-50);
        await AddBareCopiesAsync(database, 21, (row, i) => row.CreatedAt = seededAt.AddMinutes(-i));
        using var client = database.DemoApi().CreateClient();

        using var response = await DemoVisitor.ClaimAsync(client, Visitor);

        (await DemoVisitor.ClaimedAsync(response)).User.Id.Should().Be(
            fresh.Owner.Id, "a pool keeps more free copies than a round reads, and the fresh ones are the ones read");
    }

    [SqlServerFact]
    public async Task TheFreshCandidatesAreTriedFirst_AndEachGroupInAnOrderThatIsNotTheReads()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();

        // One round's twenty: ten fresh rows and ten too old to count, each list newest first,
        // which is the order the candidates' read answers them in.
        var now = DateTime.UtcNow;
        var fresh = await AddBareCopiesAsync(database, 10, (row, i) => row.CreatedAt = now.AddMinutes(-(i + 1)));
        var old = await AddBareCopiesAsync(database, 10, (row, i) => row.CreatedAt = now.AddHours(-50).AddMinutes(-(i + 1)));
        var api = database.DemoApi();

        // Each candidate is taken by somebody else as its turn comes, so the claim goes through all
        // twenty, and the order it tried them in can be read.
        var thief = new EveryCandidateTakenFirstInterceptor(database.ConnectionString);
        api.AddInterceptor(thief);
        using var client = api.CreateClient();

        using var response = await DemoVisitor.ClaimAsync(client, Visitor);

        await ShouldBePoolEmptyAsync(response);
        var tried = thief.TakenInOrder;
        tried.Should().HaveCount(20, "every candidate of the round was tried, and taken first");
        using (new AssertionScope())
        {
            tried.Take(10).Should().BeEquivalentTo(fresh.Select(row => row.Id), "the fresh ones are tried before any old one");
            tried.Skip(10).Should().BeEquivalentTo(old.Select(row => row.Id));

            // Shuffled, each group by itself: claims that arrive together then do not all try the
            // same copy first. A shuffle of ten leaves them as they were read once in 3,628,800.
            tried.Take(10).Should().NotEqual(fresh.Select(row => row.Id), "the fresh candidates are not tried in the order they were read");
            tried.Skip(10).Should().NotEqual(old.Select(row => row.Id), "nor are the old ones");
        }
    }

    // ── Who can sign in ──────────────────────────────────────────────────────────────────────────

    // On the demo, sign-in is for the owner of a claimed copy while the copy lives. Anybody else
    // the database holds is answered as an email nobody has, before the password is looked at.
    // Through the host and on SQL Server, because two of the things promised are rows: no grant is
    // written for a refused sign-in, and no failed attempt is counted.

    /// <summary>A password that passes the sign-in request's own rule and is nobody's.</summary>
    private const string WrongPassword = "Wrong-Pass-2026!";

    /// <summary>A response as status, media type and body, without the trace id, which differs between two requests.</summary>
    /// <remarks>
    /// Returned through <see cref="ComparableText"/>, so the text is exact. A failure shows
    /// <c>%7B</c> and <c>%7D</c> where the braces are, and the index counts the encoded text.
    /// (Until 2026-10-06 this said parentheses where the body has braces.)
    /// A brace in a text the assertion library compares
    /// makes a comparison that fails throw <see cref="FormatException"/> in place of its message
    /// (<c>DemoModeEndpointTests.ShapeOfAsync</c> says how it was measured).
    /// </remarks>
    private static async Task<string> AnswerOfAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (body.Length > 0 && JsonNode.Parse(body) is JsonObject json)
        {
            json.Remove("traceId");
            body = json.ToJsonString();
        }

        return ComparableText.Of(
            $"{(int)response.StatusCode} {response.Content.Headers.ContentType?.MediaType} {body}");
    }

    /// <summary>A user no failed sign-in was counted for, and whom nothing locks.</summary>
    private static readonly (int AccessFailedCount, DateTimeOffset? LockoutEnd) NothingCounted = (0, null);

    /// <summary>What the lock on sign-in holds for a user, read from the row.</summary>
    private static async Task<(int AccessFailedCount, DateTimeOffset? LockoutEnd)> LockOfAsync(DemoPoolDatabase database, Guid userId)
    {
        await using var db = database.NewContext();
        var row = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.AccessFailedCount, u.LockoutEnd })
            .SingleAsync();
        return (row.AccessFailedCount, row.LockoutEnd);
    }

    /// <summary>
    /// Registers a user of no copy through <paramref name="ordinary"/>, a host with the demo off,
    /// and returns what signs in as it.
    /// </summary>
    private static async Task<(Guid Id, string Email, string Password)> RegisterOutsideEveryCopyAsync(HttpClient ordinary)
    {
        const string password = "Outside-Pass-2026!";
        var unique = Guid.NewGuid().ToString("N")[..8];
        var email = $"outsider{unique}@example.com";
        using var response = await ordinary.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest
            {
                AzureTag = $"outsider_{unique}",
                Email = email,
                Password = password,
                FirstName = "Outside",
                LastName = "Anycopy",
            },
            DemoVisitor.Json);
        response.StatusCode.Should().Be(
            HttpStatusCode.Created, "ARRANGE: registration is open on a host with the demo off ({0})", await response.Content.ReadAsStringAsync());
        var registered = await response.Content.ReadFromJsonAsync<ApiResponse<RegisterResponse>>(DemoVisitor.Json);
        return (registered!.Data!.User.Id, email, password);
    }

    [SqlServerFact]
    public async Task ACopyPastItsLifetime_CannotSignIn_AndRecycleThenDeletesIt_WithoutTheBackstop()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var free = (await database.BuildCopiesAsync(1)).Single();
        var api = database.DemoApi();
        api.CaptureLog(LogEventLevel.Information);
        using var client = api.CreateClient();
        using var claimed = await DemoVisitor.ClaimAsync(client, Visitor);
        var claim = await DemoVisitor.ClaimedAsync(claimed);

        // While the copy lives: a wrong password is refused, and the right one signs in.
        using var wrongPassword = await DemoVisitor.TrySignInAsync(client, claim.Copy.Email, WrongPassword);
        wrongPassword.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "ARRANGE: a wrong password is refused");
        using (var live = await DemoVisitor.TrySignInAsync(client, claim.Copy.Email, claim.Copy.Password))
        {
            live.StatusCode.Should().Be(HttpStatusCode.OK, "CONTROL: inside its 24 hours the copy's password signs in");
        }

        // The copy's 24 hours ended six minutes ago, and the sessions opened in it ended with them:
        // a grant that is neither revoked nor expired would keep the copy from being deleted.
        await database.BackdateClaimAsync(free.Id, DateTime.UtcNow.AddHours(-24).AddMinutes(-6));
        await database.ExpireGrantsAsync(free.UserIds);
        var grantsBefore = await GrantsOfAsync(database, free.UserIds);
        grantsBefore.Should().Be(2, "ARRANGE: the claim's grant and the sign-in's");

        using var response = await DemoVisitor.TrySignInAsync(client, claim.Copy.Email, claim.Copy.Password);

        using (new AssertionScope())
        {
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the copy's time is over, and its password signs nobody in");
            (await DemoVisitor.ErrorCodeOfAsync(response)).Should().Be(ErrorCodes.InvalidCredentials);
            (await AnswerOfAsync(response)).Should().Be(
                await AnswerOfAsync(wrongPassword), "the answer says nothing a wrong password's does not: not that the copy was there, not that it ended");
            (await GrantsOfAsync(database, free.UserIds)).Should().Be(grantsBefore, "no session is opened past the copy's end");

            // What is logged: the user by its id and the reason, and not the address. (The host's
            // logger writes a text value in quotes.)
            api.CapturedLog.Should().Contain(line =>
                line.Contains($"Sign-in refused by the demo gate for user {free.Owner.Id} (\"CopyEnded\")", StringComparison.Ordinal));
            api.CapturedLog.Should().NotContain(line => line.Contains(claim.Copy.Email, StringComparison.OrdinalIgnoreCase));
        }

        // So the pool's job finds no live session in the copy and deletes it as a copy whose time
        // is over. The backstop, which deletes a copy under a live session and counts it apart, is
        // for a sign-in that went on being accepted past the end: there was none.
        var summary = await database.RecycleAsync();

        var after = await database.CopyAsync(free.Id);
        using (new AssertionScope())
        {
            summary.Failures.Should().BeEmpty();
            (summary.DeletedExpired, summary.DeletedHardStop, summary.DeleteFailed).Should().Be((1, 0, 0));
            after.Should().NotBeNull("a claimed copy that was deleted leaves its pool row as the record of it");
            (after?.Users).Should().BeEmpty();
            (after?.Row.DeletedAt).Should().NotBeNull();
        }
    }

    [SqlServerFact]
    public async Task AUserOutsideEveryCopy_AndAFreeCopysOwner_CannotSignInOnTheDemoHost()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var free = (await database.BuildCopiesAsync(1)).Single();

        // A free copy's owner has no password. This one was given one by something other than a
        // claim, so the password below is right, and what refuses it is that nobody claimed the copy.
        await database.GiveOwnerAPasswordAsync(free);
        using var ordinary = database.Api().CreateClient();
        var outsider = await RegisterOutsideEveryCopyAsync(ordinary);
        var demoApi = database.DemoApi();
        demoApi.CaptureLog(LogEventLevel.Information);
        using var demo = demoApi.CreateClient();

        using var unknownEmail = await DemoVisitor.TrySignInAsync(demo, "nobody@example.com", outsider.Password);
        using var asTheOutsider = await DemoVisitor.TrySignInAsync(demo, outsider.Email, outsider.Password);
        using var asTheFreeOwner = await DemoVisitor.TrySignInAsync(demo, free.Owner.Email!, DemoPoolDatabase.VisitorPassword);

        using (new AssertionScope())
        {
            unknownEmail.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "CONTROL: an email nobody has is refused");
            (await DemoVisitor.ErrorCodeOfAsync(unknownEmail)).Should().Be(ErrorCodes.InvalidCredentials);

            asTheOutsider.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a user of no copy does not sign in on the demo, with the right password");
            (await AnswerOfAsync(asTheOutsider)).Should().Be(await AnswerOfAsync(unknownEmail), "and is answered as an email nobody has");

            asTheFreeOwner.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "nor does the owner of a copy nobody claimed");
            (await AnswerOfAsync(asTheFreeOwner)).Should().Be(await AnswerOfAsync(unknownEmail));

            (await GrantsOfAsync(database, outsider.Id)).Should().Be(1, "the one grant is registration's: the refusal wrote none");
            (await GrantsOfAsync(database, free.UserIds)).Should().Be(0);
            (await LockOfAsync(database, outsider.Id)).Should().Be(NothingCounted, "and counted no failed attempt");
            (await LockOfAsync(database, free.Owner.Id)).Should().Be(NothingCounted);

            demoApi.CapturedLog.Should().Contain(line =>
                line.Contains($"Sign-in refused by the demo gate for user {outsider.Id} (\"OutsideEveryCopy\")", StringComparison.Ordinal));
            demoApi.CapturedLog.Should().Contain(line =>
                line.Contains($"Sign-in refused by the demo gate for user {free.Owner.Id} (\"CopyNotClaimed\")", StringComparison.Ordinal));
        }

        // CONTROL: the same two sign-ins through the host with the demo off, on the same database.
        // Both passwords were right, so what refused them above was the demo's gate.
        using var outsiderThere = await DemoVisitor.TrySignInAsync(ordinary, outsider.Email, outsider.Password);
        using var freeOwnerThere = await DemoVisitor.TrySignInAsync(ordinary, free.Owner.Email!, DemoPoolDatabase.VisitorPassword);
        using (new AssertionScope())
        {
            outsiderThere.StatusCode.Should().Be(HttpStatusCode.OK, await outsiderThere.Content.ReadAsStringAsync());
            freeOwnerThere.StatusCode.Should().Be(HttpStatusCode.OK, await freeOwnerThere.Content.ReadAsStringAsync());
        }
    }

    [SqlServerFact]
    public async Task FiveWrongPasswords_OnAGatedUser_LockNothing()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var copies = await database.BuildCopiesAsync(3);
        var (free, live, ended) = (copies[0], copies[1], copies[2]);

        // One user for each reason the gate refuses for: a user of no copy; the owner of a copy
        // nobody claimed, who was given a password; a contact of a claimed, living copy, who has
        // none; and the owner of a copy whose 24 hours ended an hour ago.
        await database.GiveOwnerAPasswordAsync(free);
        await database.ClaimForAVisitorAsync(live, DateTime.UtcNow);
        await database.ClaimForAVisitorAsync(ended, DateTime.UtcNow.AddHours(-25));
        Guid outsider;
        using (var ordinary = database.Api().CreateClient())
        {
            outsider = (await RegisterOutsideEveryCopyAsync(ordinary)).Id;
        }

        var gated = new (string Who, Guid Id)[]
        {
            ("a user of no copy", outsider),
            ("a free copy's owner", free.Owner.Id),
            ("a living copy's contact", live.Jane.Id),
            ("an ended copy's owner", ended.Owner.Id),
        };
        ValidationRules.MaxLoginAttempts.Should().Be(5, "ARRANGE: the fifth wrong password in a row is the one that locks");
        using var client = database.DemoApi().CreateClient();

        await using (var db = database.NewContext())
        {
            foreach (var (who, id) in gated)
            {
                var email = await db.Users.Where(u => u.Id == id).Select(u => u.Email).SingleAsync();
                for (var attempt = 1; attempt <= ValidationRules.MaxLoginAttempts; attempt++)
                {
                    using var refused = await DemoVisitor.TrySignInAsync(client, email!, WrongPassword);
                    refused.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "{0}, wrong password {1} of 5", who, attempt);
                }
            }
        }

        using (new AssertionScope())
        {
            foreach (var (who, id) in gated)
            {
                (await LockOfAsync(database, id)).Should().Be(
                    NothingCounted, "{0} is refused before the password is looked at, so no attempt is counted and nothing is locked", who);
            }
        }

        // CONTROL: the living copy's owner, whom the gate lets through. The same five wrong
        // passwords on the same host lock it, so the count is alive there and the zeros above are
        // the gate's.
        for (var attempt = 1; attempt <= ValidationRules.MaxLoginAttempts; attempt++)
        {
            using var refused = await DemoVisitor.TrySignInAsync(client, live.Owner.Email!, WrongPassword);
            refused.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        (await LockOfAsync(database, live.Owner.Id)).LockoutEnd.Should().NotBeNull(
            "five wrong passwords lock a user whose password is looked at");
    }

    // What the gate costs a sign-in, counted in the statements the host sends.

    /// <summary>
    /// The statements a host sends for one sign-in of <paramref name="copy"/>'s owner, a sign-in
    /// that is answered 200.
    /// </summary>
    private static async Task<IReadOnlyList<string>> StatementsOfOneSignInAsync(CustomWebApplicationFactory api, BuiltCopy copy)
    {
        var statements = new StatementsInOrder();
        api.AddInterceptor(statements);
        using var client = api.CreateClient();

        // One request first: what the host sends while it starts is not the sign-in's.
        using (await client.GetAsync("/health/live"))
        {
        }

        statements.Start();
        using var response = await DemoVisitor.TrySignInAsync(client, copy.Owner.Email!, DemoPoolDatabase.VisitorPassword);
        response.StatusCode.Should().Be(
            HttpStatusCode.OK, "ARRANGE: the owner of a claimed, living copy signs in, with the demo on or off ({0})", await AnswerOfAsync(response));
        return statements.Texts;
    }

    private static bool ReadsOrWritesThePool(string statement) => statement.Contains("[DemoCopies]", StringComparison.Ordinal);

    [SqlServerFact]
    public async Task WithTheDemoOff_ASignInSendsNoStatementAboutThePool_AndWithItOn_OneReadOfTheCopyByItsKey()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var copy = (await database.BuildCopiesAsync(1)).Single();
        await database.ClaimForAVisitorAsync(copy, DateTime.UtcNow);

        // The same user, the same password and the same database, through a host with the demo
        // off and through one with it on.
        var withTheDemoOff = await StatementsOfOneSignInAsync(database.Api(), copy);
        var withTheDemoOn = await StatementsOfOneSignInAsync(database.DemoApi(), copy);

        // CONTROL: with the demo on the gate asks when the copy was claimed, once, by the copy's
        // key. So the read is there to be heard, and its absence below is the flag's doing.
        // Recorded on LocalDB, between the read of the user and the grant's insert:
        //   SELECT TOP(1) [d].[ClaimedAt] FROM [DemoCopies] AS [d] WHERE [d].[Id] = @copyId
        var read = withTheDemoOn.Where(ReadsOrWritesThePool).Should().ContainSingle(
            "CONTROL: on the demo a sign-in reads its copy's row, once").Subject;
        read.Should().MatchRegex(
            @"^SELECT TOP\(1\) \[d\]\.\[ClaimedAt\]\s+FROM \[DemoCopies\] AS \[d\]\s+WHERE \[d\]\.\[Id\] = @\w+$",
            "CONTROL: one column of one row, found by the primary key");

        using (new AssertionScope())
        {
            // CONTROL: the recorder heard this sign-in from before the gate's place to after it,
            // the read of the user by its email and the grant's insert.
            withTheDemoOff.Should().Contain(
                statement => statement.Contains("FROM [AspNetUsers]", StringComparison.Ordinal)
                    && statement.Contains("[NormalizedEmail] = @", StringComparison.Ordinal),
                "CONTROL: the user was read while the recorder listened");
            withTheDemoOff.Should().Contain(
                statement => statement.Contains("INSERT INTO [RefreshTokens]", StringComparison.Ordinal),
                "CONTROL: and its grant was written");
            withTheDemoOff.Should().NotContain(
                statement => ReadsOrWritesThePool(statement), "with the demo off the gate reads nothing");
        }
    }

    // ── Through the BFF ──────────────────────────────────────────────────────────────────────────

    /// <summary>The real BFF, with the demo on, in front of <paramref name="api"/>.</summary>
    private static BffOverApiFactory DemoBffOver(CustomWebApplicationFactory api)
    {
        var bffHost = new BffOverApiFactory(api, CustomWebApplicationFactory.ServiceCredentialKey);
        bffHost.EnableDemo();
        return bffHost;
    }

    /// <summary>
    /// A client that keeps no cookies of its own, so every request carries exactly the session the
    /// test names.
    /// </summary>
    private static HttpClient BrowserOf(BffOverApiFactory bffHost) =>
        bffHost.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    /// <summary>A request as a browser sends it: the session cookie when it has one, JSON when it has a body.</summary>
    private static HttpRequestMessage FromTheBrowser(HttpMethod method, string path, string? cookie = null, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: DemoVisitor.Json);
        }

        return request;
    }

    /// <summary>The claim as the application sends it: an empty object.</summary>
    private static HttpRequestMessage BffClaim(string? cookie = null) =>
        FromTheBrowser(HttpMethod.Post, "/bff/auth/demo/claim", cookie, new { });

    /// <summary>The session cookie an answer of the BFF set, as the pair a browser sends back.</summary>
    private static string SessionCookieOf(HttpResponseMessage response)
    {
        response.Headers.TryGetValues("Set-Cookie", out var cookies).Should().BeTrue("the answer sets the session cookie");
        return cookies!.Single().Split(';')[0];
    }

    /// <summary>The accounts the session behind <paramref name="cookie"/> reads through the BFF's proxy.</summary>
    private static async Task<List<AccountResponse>> AccountsThroughTheProxyAsync(HttpClient browser, string cookie)
    {
        using var response = await browser.SendAsync(FromTheBrowser(HttpMethod.Get, "/api/accounts", cookie));
        response.StatusCode.Should().Be(
            HttpStatusCode.OK, "the session reads its accounts through the proxy ({0})", await AnswerOfAsync(response));
        return (await response.Content.ReadFromJsonAsync<ApiResponse<List<AccountResponse>>>(DemoVisitor.Json))!.Data!;
    }

    /// <summary>The balances every copy starts with, largest first: Main Savings and Checking.</summary>
    private static readonly decimal[] StartingBalances = [12450.00m, 2300.00m];

    [SqlServerFact]
    public async Task ThroughTheBff_AClaim_OpensASession_AndTheProxyReadsTheCopysTwoAccounts()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var free = (await database.BuildCopiesAsync(1)).Single();
        var api = database.DemoApi();
        using var bffHost = DemoBffOver(api);
        using var browser = BrowserOf(bffHost);

        using var response = await browser.SendAsync(BffClaim());

        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, "a free copy is there to be claimed ({0})", await AnswerOfAsync(response));
        var cookie = SessionCookieOf(response);
        using var claim = JsonDocument.Parse(text);
        var data = claim.RootElement.GetProperty("data");

        using var me = await browser.SendAsync(FromTheBrowser(HttpMethod.Get, "/bff/auth/me", cookie));
        var meText = await me.Content.ReadAsStringAsync();
        me.StatusCode.Should().Be(HttpStatusCode.OK, "the cookie the claim set names a session ({0})", await AnswerOfAsync(me));
        using var meBody = JsonDocument.Parse(meText);
        var accounts = await AccountsThroughTheProxyAsync(browser, cookie);
        var row = (await database.CopyAsync(free.Id))!.Row;

        using (new AssertionScope())
        {
            // The answer: the copy's owner, what signs in to the copy again, and no token.
            data.GetProperty("user").GetProperty("id").GetGuid().Should().Be(free.Owner.Id);
            data.GetProperty("copy").GetProperty("email").GetString().Should().Be(free.Owner.Email);
            data.GetProperty("copy").GetProperty("contacts").EnumerateArray().Select(c => c.GetString()).Should().Equal(
                new[] { free.Jane.AzureTag, free.Mike.AzureTag }.Order(StringComparer.Ordinal));
            text.Should().NotContain("oken", "the tokens stay in the BFF's store");
            (response.Headers.CacheControl?.NoStore).Should().BeTrue("the answer carries a password");

            // The session: the visitor is the copy's owner, and the proxy reads the owner's two accounts.
            meBody.RootElement.GetProperty("data").GetProperty("user").GetProperty("id").GetGuid().Should().Be(free.Owner.Id);
            accounts.Select(a => a.Balance).OrderDescending().Should().Equal(StartingBalances);
            accounts.Select(a => a.Name).Should().BeEquivalentTo(["Main Savings", "Checking"]);

            // The row: claimed, for the client the BFF named. Its test server gives a request no
            // address, and the key of no address is "unknown".
            row.ClaimedAt.Should().NotBeNull();
            row.ClientKey.Should().Equal(KeyOf("unknown"));
            row.ClientKey.Should().NotEqual(KeyOf(Visitor), "CONTROL: an address has another key");
            (await GrantsOfAsync(database, free.Owner.Id)).Should().Be(1, "the claim opens one session");
        }
    }

    [SqlServerFact]
    public async Task ThroughTheBff_StartingOver_GivesAnotherCopy_AndTheOldCookieIs401()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        await database.BuildCopiesAsync(2);
        var api = database.DemoApi();
        using var bffHost = DemoBffOver(api);
        using var browser = BrowserOf(bffHost);

        // The first copy, and a change in it: 10.00 into its savings. "The starting balances" of
        // the second copy below are then not what any copy would show.
        using var first = await browser.SendAsync(BffClaim());
        first.StatusCode.Should().Be(HttpStatusCode.OK, "ARRANGE: a free copy is there to be claimed ({0})", await AnswerOfAsync(first));
        var firstCookie = SessionCookieOf(first);
        using var firstClaim = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var firstOwner = firstClaim.RootElement.GetProperty("data").GetProperty("user").GetProperty("id").GetGuid();
        var firstEmail = firstClaim.RootElement.GetProperty("data").GetProperty("copy").GetProperty("email").GetString();
        var savings = (await AccountsThroughTheProxyAsync(browser, firstCookie)).Single(a => a.Balance == 12450.00m);
        using var deposit = FromTheBrowser(
            HttpMethod.Post,
            "/api/transactions/deposit",
            firstCookie,
            new DepositRequest { AccountId = savings.Id, Amount = 10.00m, Description = "Before starting over" });
        deposit.Headers.Add(IdempotencyConstants.HeaderName, Guid.NewGuid().ToString());
        using var deposited = await browser.SendAsync(deposit);
        deposited.IsSuccessStatusCode.Should().BeTrue("ARRANGE: the visitor changes the first copy ({0})", await AnswerOfAsync(deposited));
        (await AccountsThroughTheProxyAsync(browser, firstCookie)).Select(a => a.Balance).OrderDescending()
            .Should().Equal([12460.00m, 2300.00m], "ARRANGE: the first copy no longer shows the starting balances");

        // Starting over: a claim that arrives with the first session's cookie.
        using var second = await browser.SendAsync(BffClaim(firstCookie));

        second.StatusCode.Should().Be(HttpStatusCode.OK, "another free copy is there ({0})", await AnswerOfAsync(second));
        var secondCookie = SessionCookieOf(second);
        using var secondClaim = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        var secondOwner = secondClaim.RootElement.GetProperty("data").GetProperty("user").GetProperty("id").GetGuid();
        var secondEmail = secondClaim.RootElement.GetProperty("data").GetProperty("copy").GetProperty("email").GetString();
        var secondAccounts = await AccountsThroughTheProxyAsync(browser, secondCookie);
        using var withTheOldCookie = await browser.SendAsync(FromTheBrowser(HttpMethod.Get, "/bff/auth/me", firstCookie));
        using var withTheNewCookie = await browser.SendAsync(FromTheBrowser(HttpMethod.Get, "/bff/auth/me", secondCookie));
        var copies = await database.CopiesAsync();

        using (new AssertionScope())
        {
            secondCookie.Should().NotBe(firstCookie);
            secondEmail.Should().NotBe(firstEmail, "starting over gives another copy, not the same one again");
            secondOwner.Should().NotBe(firstOwner);
            secondAccounts.Select(a => a.Balance).OrderDescending().Should().Equal(
                StartingBalances, "the new copy is as every copy starts: the deposit was made in the other one");

            withTheOldCookie.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the session the old cookie named has ended");
            withTheNewCookie.StatusCode.Should().Be(HttpStatusCode.OK);

            // Both copies stay claimed: the first is not handed back to the pool, with its deposit
            // in it. The pool's job deletes it once its time is up.
            copies.Should().HaveCount(2);
            copies.Should().OnlyContain(c => c.Row.ClaimedAt != null);
            copies.Select(c => c.Row.ClaimId).Distinct().Should().HaveCount(2);
            copies.Select(c => c.Owner.Id).Should().BeEquivalentTo([firstOwner, secondOwner]);
        }

        // The first session's grant is revoked at the API, by the BFF's revoker on its own
        // thread, and the second's is not.
        async Task<int> LiveGrantsOfAsync(Guid userId)
        {
            await using var db = database.NewContext();
            return await db.RefreshTokens.CountAsync(t => t.UserId == userId && t.RevokedAt == null);
        }

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (await LiveGrantsOfAsync(firstOwner) != 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        using (new AssertionScope())
        {
            (await LiveGrantsOfAsync(firstOwner)).Should().Be(0, "ending the first session revokes its grant");
            (await LiveGrantsOfAsync(secondOwner)).Should().Be(1, "the new session's grant is left alone");
            await using var db = database.NewContext();
            (await db.Accounts.Where(a => a.UserId == firstOwner).Select(a => a.Balance).ToListAsync())
                .OrderDescending().Should().Equal([12460.00m, 2300.00m], "the first copy keeps what was done in it");
        }
    }
}
