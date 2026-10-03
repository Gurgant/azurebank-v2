using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using AzureBank.Api.Observability;
using AzureBank.Api.Security;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Options;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.AspNetCore.Identity;
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
    /// (<c>READ_COMMITTED_SNAPSHOT</c>), and reads the setting back. On: Azure SQL's default. Off:
    /// SQL Server's, which a database created under compose has.
    /// </summary>
    private static async Task SetRowVersioningAsync(DemoPoolDatabase database, bool on)
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

    private static async Task<bool> RowVersioningAsync(DemoPoolDatabase database)
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
    /// that deadlocks is answered 503 and says so only in the log (error 1205), and a 503 with
    /// another cause must not be read as one.
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
        private int _taken;
        private int _statements;

        /// <summary>Copies taken out of band.</summary>
        public int Taken => Volatile.Read(ref _taken);

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
                    Interlocked.Add(ref _taken, await claim.ExecuteNonQueryAsync(cancellationToken));
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

    [SqlServerTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EightParallelClaims_OnAPoolOfFive_GiveFiveDifferentCopies_AndThree429s(bool rowVersioning)
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        await SetRowVersioningAsync(database, rowVersioning);
        var copies = await database.BuildCopiesAsync(5);
        var api = database.DemoApi();
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
    // show that they do not, by running them; a deadlock that needs an unlucky moment can pass
    // there. These two hold, on one claim's own statements, the two rules that leave no such
    // moment, each found by a deadlock the theories did show (error 1205, with row versioning off).

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
            // A column the index does not hold is fetched from the row, with the index entry still
            // locked. A claim taking that copy holds the row and waits for the index entry.
            asked.Should().NotBeEmpty().And.BeSubsetOf(
                held, "the read is answered from the index of free copies alone, {0}, and never from the rows", string.Join(", ", held));
            Regex.Replace(condition, @"\[\w+\]\.", string.Empty).Should().Be(
                free.GetFilter(), "the read's condition is the index's own, so the index answers all of it");
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
        // a visitor a copy that somebody else can already sign in to.
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
}
