using System.Data.Common;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzureBank.Api.Services.Interfaces;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.Enums;
using AzureBank.Shared.Exceptions;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Events;

namespace AzureBank.Tests.Integration;

/// <summary>
/// The grant of 06 §4 against REAL SQL Server: renewal sends no write, eight concurrent renewals all
/// succeed and change nothing, a revoke racing a renewal's read is not theft, the tripwire fires on a
/// real replay, a revoke whose write fails answers 503, the renewal-rate detector's event costs no
/// write, and the lifetime and the access-token cap hold for rows SQL Server hands back.
/// <i>(Until PR-1 this class proved rotation: the self-referencing
/// successor write, the rowversion guard against a forked chain, and reuse revoking the family.)</i>
/// </summary>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class RefreshTokenRotationSqlServerTests : IDisposable
{
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private const string Password = "SecurePass123!";
    private CustomWebApplicationFactory? _factory;

    [SqlServerFact]
    public async Task EightConcurrentRenewalsOfOneGrant_AllSucceed_AndChangeNothing()
    {
        /*
          06 §10 O2g, replacing the two rotation proofs. Under rotation, eight renewals of one token
          raced a rowversion guard: one won, seven got 401 — and a BFF whose answer was lost was one of
          the seven. Now each is a read: 8 × 200, one row, the same rowversion before and after.
        */
        var client = CreateSqlClient();
        var (userId, _, grant) = await RegisterAsync(client);
        var before = await ReadGrantsAsync(userId);

        const int parallel = 8;
        var responses = await Task.WhenAll(Enumerable.Range(0, parallel)
            .Select(_ => client.PostAsJsonAsync("/api/auth/refresh",
                new RefreshRequest { RefreshToken = grant }, Json)));

        responses.Select(r => r.StatusCode).Should().AllBeEquivalentTo(HttpStatusCode.OK,
            "a renewal consumes nothing, so eight at once all renew");
        var after = await ReadGrantsAsync(userId);
        after.Should().ContainSingle("no renewal mints a successor");
        after[0].RowVersion.Should().Equal(before[0].RowVersion, "no renewal wrote the row");
        after[0].RevokedAt.Should().BeNull();
        after[0].ExpiresAt.Should().Be(before[0].ExpiresAt, "the grant's expiry is never extended");
    }

    [SqlServerFact]
    public async Task ARevokeCommittedWhileARenewalWaitsToRead_IsNotTheTripwire()
    {
        /*
          06 §10 O2b, replacing the "reuse revoke racing rotations" proof. The renewal ARRIVES first
          (and is stamped), is then held before its read of the grant, the grant is revoked through
          /revoke in the gap, and the renewal reads a grant revoked after it arrived. That is a
          renewal in flight when the session ended: 401, an Info line, and no RefreshTokenReuse.
        */
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        _factory.CaptureLog(LogEventLevel.Information);
        var client = _factory.CreateClient();
        var (userId, _, grant) = await RegisterAsync(client);

        var hold = new HoldingReadInterceptor("[TokenHash] = ");
        _factory.AddInterceptor(hold);

        var renewal = client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest { RefreshToken = grant }, Json);
        (await Task.WhenAny(hold.Held, Task.Delay(TimeSpan.FromSeconds(10)))).Should().BeSameAs(hold.Held,
            "the renewal must be parked before its read, or the order below is not the one under test");

        var revoke = await client.PostAsJsonAsync("/api/auth/revoke",
            new RevokeRequest { RefreshTokens = [grant] }, Json);
        revoke.StatusCode.Should().Be(HttpStatusCode.OK, "the revoke commits while the renewal waits");
        hold.Release();
        var renewed = await renewal;

        renewed.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the grant it read was revoked");
        (await ReadGrantsAsync(userId)).Single().RevokedReason.Should().Be(RefreshTokenRevokedReason.SessionEnded);
        _factory.CapturedLog.Should().ContainSingle(l => l.Contains("while its renewal was in flight"),
            "the renewal arrived before the revoke, so it was in flight (06 §4.3 row 3)");
        _factory.CapturedLog.Should().NotContain(l => l.Contains(SecurityEvents.RefreshTokenReuse));
        (await CountAuditAsync(userId, SecurityEvents.RefreshTokenReuse)).Should().Be(0);
    }

    [SqlServerFact]
    public async Task AnEndedSessionsGrantPresentedAgain_TripsTheTripwire_AndTheOtherSessionStillRenews()
    {
        /*
          06 §10 O2a, the positive control the zeros elsewhere rest on. Two sessions of one user; A
          ends through /revoke; A's grant presented again is the tripwire: 401, one RefreshTokenReuse
          log line and audit row — and B's grant still active and renewing, because the tripwire
          revokes nothing (F3).
        */
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        _factory.CaptureLog(LogEventLevel.Warning);
        var client = _factory.CreateClient();
        var (userId, email, sessionA) = await RegisterAsync(client);
        var sessionB = await LoginAsync(client, email);

        (await client.PostAsJsonAsync("/api/auth/revoke", new RevokeRequest { RefreshTokens = [sessionA] }, Json))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var ended = (await ReadGrantsAsync(userId)).Single(g => g.RevokedAt != null);

        var replay = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest { RefreshToken = sessionA }, Json);

        replay.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await replay.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("errorCode").GetString()
            .Should().Be(ErrorCodes.RefreshTokenInvalid, "the tripwire answers the same uniform 401");
        (await CountAuditAsync(userId, SecurityEvents.RefreshTokenReuse)).Should().Be(1);
        _factory.CapturedLog.Count(l => l.Contains(SecurityEvents.RefreshTokenReuse)).Should().Be(1);

        var grants = await ReadGrantsAsync(userId);
        grants.Count(g => g.RevokedAt == null).Should().Be(1, "session B's grant is untouched");
        grants.Single(g => g.Id == ended.Id).RowVersion.Should().Equal(ended.RowVersion,
            "presenting the ended grant again wrote nothing to it");
        (await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest { RefreshToken = sessionB }, Json))
            .StatusCode.Should().Be(HttpStatusCode.OK, "session B still renews");
    }

    [SqlServerFact]
    public async Task ARevokeWhoseWriteFails_Answers503WithRetryAfter_AndTheRetryRevokes()
    {
        // 06 §4.4, F12: the database failing is a 503 the BFF retries, never a 500. The fault is a
        // one-shot TimeoutException on the revoke's own UPDATE, on the relational path the unit
        // tests cannot reach.
        var client = CreateSqlClient();
        var (userId, _, grant) = await RegisterAsync(client);
        var fault = new TransientFailureInterceptor("UPDATE [r]");
        _factory!.AddInterceptor(fault);

        var failed = await client.PostAsJsonAsync("/api/auth/revoke", new RevokeRequest { RefreshTokens = [grant] }, Json);

        fault.Fired.Should().BeTrue("the test proves nothing if the revoke's UPDATE never failed");
        failed.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        failed.Headers.RetryAfter.Should().NotBeNull("the client is told when to retry");
        (await failed.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("errorCode").GetString()
            .Should().Be(ErrorCodes.ServiceUnavailable);
        (await ReadGrantsAsync(userId)).Single().RevokedAt.Should().BeNull("the failed revoke recorded nothing");

        var retried = await client.PostAsJsonAsync("/api/auth/revoke", new RevokeRequest { RefreshTokens = [grant] }, Json);

        retried.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadGrantsAsync(userId)).Single().RevokedReason.Should().Be(RefreshTokenRevokedReason.SessionEnded);
    }

    [SqlServerFact]
    public async Task TheGrantLivesSixtyMinutesFromSignIn_AndCapsTheAccessTokensMintedFromIt()
    {
        /*
          PR-1's lifetime oracle and 06 §10 O2e, on the rows SQL Server hands back. The grant's
          expiry in SQL is its sign-in plus 60 minutes, and login answers that same instant. Then the
          row is backdated to two minutes from now — the option refuses a lifetime that short (F11) —
          and a renewal's access token must expire no later than it, although SQL Server returns the
          expiry as DateTimeKind.Unspecified.
        */
        var client = CreateSqlClient();
        var (userId, email, _) = await RegisterAsync(client);
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest { Email = email, Password = Password }, Json);
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>(Json))!.Data!.Token;

        var loginGrant = (await ReadGrantsAsync(userId)).OrderBy(g => g.CreatedAt).Last();
        (loginGrant.ExpiresAt - loginGrant.CreatedAt).Should().Be(TimeSpan.FromMinutes(60));
        DateTime.SpecifyKind(loginGrant.ExpiresAt, DateTimeKind.Utc).Should().Be(token.RefreshTokenExpiresAt!.Value,
            "login answers the stored expiry (refreshTokenExpiresAt, 06 §4.1)");

        var cap = DateTime.UtcNow.AddMinutes(2);
        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
            await db.RefreshTokens.Where(g => g.Id == loginGrant.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(g => g.ExpiresAt, cap));
        }

        var renewal = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest { RefreshToken = token.RefreshToken! }, Json);
        renewal.EnsureSuccessStatusCode();
        var renewed = (await renewal.Content.ReadFromJsonAsync<ApiResponse<RefreshResponse>>(Json))!.Data!;
        var exp = new JwtSecurityTokenHandler().ReadJwtToken(renewed.AccessToken).ValidTo;

        exp.Should().BeOnOrBefore(cap, "no access token outlives the grant it came from");
        exp.Should().BeAfter(cap.AddSeconds(-2), "and the cap is the grant's expiry, not some other instant");
        renewed.ExpiresAt.Should().Be(exp);
    }

    [SqlServerFact]
    public async Task LoginAfterFailedAttempt_IssuesRefreshToken_NoDuplicateUserInsert()
    {
        var client = CreateSqlClient();
        var (_, email, _) = await RegisterAsync(client);

        // One wrong password bumps AccessFailedCount to 1, so the next CORRECT login takes the
        // lockout-RESET branch — which detaches the tracked user on SQL Server.
        (await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest { Email = email, Password = "WrongPass999!" }, Json)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);

        // The correct login must succeed and issue a refresh token: issuance sets only the
        // UserId FK, so the detached principal is NOT re-INSERTed (which would be a dup-key 500).
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest { Email = email, Password = Password }, Json);
        login.StatusCode.Should().Be(HttpStatusCode.OK,
            "issuing a refresh token after a lockout reset must not re-insert the detached user");
        var body = await login.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>(Json);
        body!.Data!.Token.RefreshToken.Should().NotBeNullOrEmpty();
    }

    [SqlServerFact]
    public async Task ARenewal_SendsNoWriteCommand_OnSqlServer()
    {
        // 06 §10 O0-2 item 2. A renewal that writes nothing cannot lose a write: no answer lost
        // after a commit, no commit EF retries as if it had rolled back, nothing for a hung database
        // to hold (06 §1). Red on main, where every renewal rotates: an INSERT of the successor and
        // an UPDATE of the presented row.
        var client = CreateSqlClient();
        var (_, _, refresh) = await RegisterAsync(client);

        // Added after the registration, which writes plenty; the list is read per DbContext build,
        // so it applies from the next request on (CustomWebApplicationFactory.AddInterceptor).
        var commands = new CommandRecordingInterceptor();
        _factory!.AddInterceptor(commands);

        commands.Start();
        var renewal = await client.PostAsJsonAsync("/api/auth/refresh",
            new RefreshRequest { RefreshToken = refresh }, Json);
        commands.Stop();

        renewal.StatusCode.Should().Be(HttpStatusCode.OK,
            $"the renewal itself must succeed, or its commands say nothing about a renewal (body: {await renewal.Content.ReadAsStringAsync()})");
        commands.Selects.Should().NotBeEmpty(
            "the recorder must have seen the renewal's own read of the grant, or the zero below proves nothing");
        commands.Writes.Should().BeEmpty(
            "a renewal only reads the grant and mints an access token in memory (06 §4.3: Writes = none)");
    }

    [SqlServerFact]
    public async Task FourRenewalsThatRaiseTheRateEvent_SendNoWriteCommand_AndWriteNoAuditRow()
    {
        /*
          06 §6, anomaly 2: the detector counts in memory and writes nothing. Four renewals of one
          grant within one token lifetime, recorded on the wire: all 200, one RefreshRenewalRateHigh
          log line, and not one command that is not a SELECT. The event line is what makes the zero
          mean something: the recorder was listening across the very renewal that raised it.
        */
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        _factory.CaptureLog(LogEventLevel.Warning);
        var client = _factory.CreateClient();
        var (userId, _, grant) = await RegisterAsync(client);
        var auditRowsBefore = await CountAllAuditRowsAsync();

        var commands = new CommandRecordingInterceptor();
        _factory.AddInterceptor(commands);

        commands.Start();
        for (var i = 0; i < 4; i++)
        {
            var renewal = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest { RefreshToken = grant }, Json);
            renewal.StatusCode.Should().Be(HttpStatusCode.OK, $"renewal {i + 1}: the detector refuses nothing");
        }

        commands.Stop();

        _factory.CapturedLog.Count(l => l.Contains(SecurityEvents.RefreshRenewalRateHigh)).Should().Be(1,
            "the fourth renewal within one token lifetime raises the event, once");
        commands.Selects.Should().HaveCountGreaterThanOrEqualTo(4,
            "each renewal reads its grant; a recorder that saw fewer was not listening");
        commands.Writes.Should().BeEmpty("the detector counts in memory; nothing reaches the database");
        (await CountAllAuditRowsAsync()).Should().Be(auditRowsBefore, "the event is a log line, never an audit row");
        (await CountAuditAsync(userId, SecurityEvents.RefreshRenewalRateHigh)).Should().Be(0);
    }

    [SqlServerFact]
    public async Task AnUnknownGrantsAuditRow_IsWrittenEvenWhenTheCallerHangsUpDuringIt()
    {
        /*
          The unknown grant's refusal on real SQL Server, through the real audit service. The caller's
          token is cancelled as the row's INSERT is about to go out, and SqlClient refuses a command
          whose token is already cancelled: a write given the caller's token writes nothing. The row
          must be there all the same (IRefreshTokenService: the token cancels the read only).
        */
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        _factory.CreateClient().Dispose();
        using var caller = new CancellationTokenSource();
        var hangUp = new HangUpAtTheAuditInsert(caller);
        var before = await CountAllAuditRowsAsync(SecurityEvents.RefreshTokenUnknown);
        _factory.AddInterceptor(hangUp);

        using (var scope = _factory.Services.CreateScope())
        {
            var grants = scope.ServiceProvider.GetRequiredService<IRefreshTokenService>();
            var renewal = () => grants.RenewAsync($"never-issued-{Guid.NewGuid():N}", DateTime.UtcNow, caller.Token);

            await renewal.Should().ThrowAsync<AuthenticationException>("the refusal is still the uniform 401");
        }

        hangUp.Fired.Should().BeTrue("the caller must have hung up during the write, or the row proves nothing");
        (await CountAllAuditRowsAsync(SecurityEvents.RefreshTokenUnknown)).Should().Be(before + 1,
            "the refusal's row is written although the caller hung up");
    }

    /// <summary>Cancels the caller's token as the first AuditEvents INSERT is about to be sent.</summary>
    private sealed class HangUpAtTheAuditInsert(CancellationTokenSource caller) : DbCommandInterceptor
    {
        private int _armed = 1;

        public bool Fired => Volatile.Read(ref _armed) == 0;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            HangUpAt(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            HangUpAt(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void HangUpAt(DbCommand command)
        {
            if (command.CommandText.Contains("INSERT INTO [AuditEvents]", StringComparison.Ordinal)
                && Interlocked.CompareExchange(ref _armed, 0, 1) == 1)
            {
                caller.Cancel();
            }
        }
    }

    private async Task<int> CountAllAuditRowsAsync(string securityEvent)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        return await db.AuditEvents.AsNoTracking().CountAsync(e => e.Event == securityEvent);
    }

    private async Task<int> CountAllAuditRowsAsync()
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        return await db.AuditEvents.AsNoTracking().CountAsync();
    }

    private sealed record GrantRow(
        Guid Id, DateTime CreatedAt, DateTime ExpiresAt, DateTime? RevokedAt,
        RefreshTokenRevokedReason? RevokedReason, byte[] RowVersion);

    private async Task<List<GrantRow>> ReadGrantsAsync(Guid userId)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        return await db.RefreshTokens.AsNoTracking()
            .Where(t => t.UserId == userId)
            .Select(t => new GrantRow(t.Id, t.CreatedAt, t.ExpiresAt, t.RevokedAt, t.RevokedReason, t.RowVersion))
            .ToListAsync();
    }

    private async Task<int> CountAuditAsync(Guid userId, string securityEvent)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        return await db.AuditEvents.AsNoTracking()
            .CountAsync(e => e.ActorUserId == userId && e.Event == securityEvent);
    }

    private HttpClient CreateSqlClient()
    {
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        return _factory.CreateClient();
    }

    private static async Task<(Guid UserId, string Email, string Refresh)> RegisterAsync(HttpClient client)
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var email = $"rtrot{uniqueId}@example.com";
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = $"rtrot_{uniqueId}",
            Email = email,
            Password = Password,
            FirstName = "Rot",
            LastName = "Ate"
        }, Json);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<RegisterResponse>>(Json);
        // The API always populates RefreshToken on a successful register (nullable only so a
        // cross-boundary consumer degrades gracefully) — assert non-null for the test tuple.
        return (result!.Data!.User.Id, email, result.Data.Token.RefreshToken!);
    }

    private static async Task<string> LoginAsync(HttpClient client, string email)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest { Email = email, Password = Password }, Json);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>(Json))!.Data!.Token.RefreshToken!;
    }

    public void Dispose() => _factory?.Dispose();
}
