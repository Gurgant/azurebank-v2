extern alias bff;

using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
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
using Xunit;
using BffSessionOptions = bff::AzureBank.Bff.Options.BffSessionOptions;
using BffSessions = bff::AzureBank.Bff.Services.Interfaces.ISessionService;
using SessionStamps = bff::AzureBank.Bff.Services.SessionStamps;
using SessionStampWatcher = bff::AzureBank.Bff.Services.SessionStampWatcher;

namespace AzureBank.Tests.Integration;

/// <summary>How the stamp is raised in a proof of 06 §10 O2j.</summary>
public enum StampLever
{
    /// <summary><c>POST /api/auth/logout</c>, with the user's own access token, straight to the API.</summary>
    Logout,

    /// <summary>The SQL block of step 4 in <c>docs/runbooks/refresh-token-reuse-recorded.md</c>, as printed.</summary>
    RunbookSql,
}

/// <summary>
/// 06 §10 O2j on the EF InMemory database: the stamp raised through <c>/api/auth/logout</c> ends both
/// sessions of that user within one 15 s poll, and not the other user's, and no whole-row write of
/// the user loaded before it lowers the stamp again. The same proofs on SQL Server, with the
/// runbook's SQL as well, are <see cref="SessionStampLeverSqlServerTests"/>.
/// </summary>
public sealed class SessionStampLeverTests
{
    [Fact]
    public Task RaisedThroughLogout_EndsTheUsersSessionsWithinOnePoll_AndSparesTheOtherUsers() =>
        SessionStampLever.ProveAsync(StampLever.Logout, connectionString: null);

    [Fact]
    public Task RaisedThroughLogout_AWholeRowWriteOfTheUserLoadedBeforeIt_FailsAndCannotLowerTheStamp() =>
        SessionStampLever.ProveNoWholeRowWriteLowersTheStampAsync(StampLever.Logout, connectionString: null);
}

/// <summary>
/// 06 §10 O2j on real SQL Server, through both levers; no whole-row write of a user loaded before a
/// lever lowers the stamp; and a sign-out's revoke and its stamp raise commit together.
/// </summary>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class SessionStampLeverSqlServerTests
{
    [SqlServerTheory]
    [InlineData(StampLever.Logout)]
    [InlineData(StampLever.RunbookSql)]
    public Task TheLever_EndsTheUsersSessionsWithinOnePoll_AndSparesTheOtherUsers(StampLever lever) =>
        SessionStampLever.ProveAsync(lever, SqlServerFactAttribute.ConnectionString);

    [SqlServerTheory]
    [InlineData(StampLever.Logout)]
    [InlineData(StampLever.RunbookSql)]
    public Task AWholeRowWriteOfTheUserLoadedBeforeTheLever_FailsAndCannotLowerTheStamp(StampLever lever) =>
        SessionStampLever.ProveNoWholeRowWriteLowersTheStampAsync(lever, SqlServerFactAttribute.ConnectionString);

    [SqlServerFact]
    public async Task ASignOutWhoseStampRaiseFails_RevokesNothing()
    {
        /*
          "In the same transaction" (06 §5.3), shown by breaking the second half. The revoke's UPDATE
          runs and reports its rows; then the stamp's UPDATE throws. If the two were separate commits
          the grants would stay revoked with the stamp unraised: a lever that looks pulled and ends
          nobody within the poll. They must be back as they were.
        */
        using var api = new CustomWebApplicationFactory();
        api.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        var client = api.CreateClient();
        var (email, _, userId) = await SessionStampLever.RegisterAsync(client);
        var access = await SessionStampLever.SignInDirectlyAsync(client, email);

        var fault = new FailTheStampRaise();
        api.AddInterceptor(fault);
        using var logout = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        logout.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        var response = await client.SendAsync(logout);

        using (new AssertionScope())
        {
            fault.RevokedRows.Should().Be(2, "the revoke ran first and revoked both grants, register's and login's");
            fault.Thrown.Should().Be(1, "then the stamp raise failed");
            response.IsSuccessStatusCode.Should().BeFalse("a sign-out that did not happen must not say it did");
            var grants = await SessionStampLever.GrantsOfAsync(api, userId);
            grants.Should().HaveCount(2).And.OnlyContain(g => g.RevokedAt == null,
                "the revoke rolled back with the failed raise");
            (await SessionStampLever.StampOfAsync(api, userId)).Should().Be(0);
        }
    }

    /// <summary>Throws on the stamp raise; records what the revoke before it reported.</summary>
    private sealed class FailTheStampRaise : DbCommandInterceptor
    {
        public int RevokedRows { get; private set; }
        public int Thrown { get; private set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("[SessionStamp]", StringComparison.Ordinal))
            {
                Thrown++;
                throw new InvalidOperationException("injected: the stamp raise fails");
            }

            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<int> NonQueryExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("[RevokedReason]", StringComparison.Ordinal))
            {
                RevokedRows += result;
            }

            return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
        }
    }
}

/// <summary>
/// The proof itself (06 §10 O2j): the real BFF in front of the real API (<see cref="BffOverApiFactory"/>),
/// the BFF's watcher on a fake clock so one period is one <c>Advance</c>.
/// </summary>
internal static class SessionStampLever
{
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private const string Password = "SecurePass123!";

    /// <summary>What step 4 of the runbook prints where the user's id goes.</summary>
    private const string RunbookPlaceholder = "'<ActorUserId from the row>'";

    public static async Task ProveAsync(StampLever lever, string? connectionString)
    {
        using var api = new CustomWebApplicationFactory();
        if (connectionString is not null)
        {
            api.SetConnectionString(connectionString);
        }
        var apiClient = api.CreateClient();

        using var bffHost = new BffOverApiFactory(api, CustomWebApplicationFactory.ServiceCredentialKey);
        var clock = bffHost.UseFakeClock();
        var browser = bffHost.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var stamps = bffHost.Services.GetRequiredService<SessionStamps>();
        var sessions = bffHost.Services.GetRequiredService<BffSessions>();

        // Sessions A and B of one user, C of another, each signed in through the BFF as the SPA does.
        var (email, _, userId) = await RegisterAsync(apiClient);
        var (otherEmail, _, otherUserId) = await RegisterAsync(apiClient);
        var a = await SignInThroughTheBffAsync(bffHost, browser, email);
        var b = await SignInThroughTheBffAsync(bffHost, browser, email);
        var c = await SignInThroughTheBffAsync(bffHost, browser, otherEmail);

        // Positive control: every session answers before the lever.
        foreach (var session in new[] { a, b, c })
        {
            (await ProxiedReadAsync(bffHost, browser, session)).Should().Be(HttpStatusCode.OK,
                "each session works before the lever, or the 401s below prove nothing");
        }

        await PullAsync(lever, api, apiClient, email, userId, connectionString);

        // The lever did what it says at the API: the user's stamp is 1 and the other user's 0, and
        // every live grant of the user is revoked with the lever's reason.
        (await StampOfAsync(api, userId)).Should().Be(1);
        (await StampOfAsync(api, otherUserId)).Should().Be(0);
        var reason = lever == StampLever.Logout
            ? RefreshTokenRevokedReason.SignOutEverywhere
            : RefreshTokenRevokedReason.ReuseContainment;
        (await GrantsOfAsync(api, userId)).Should().NotBeEmpty()
            .And.OnlyContain(g => g.RevokedReason == reason);

        // One period, and the watcher has read the raised stamp.
        clock.Advance(SessionStampWatcher.Period);
        (await EventuallyAsync(() => stamps.Latest(userId) == 1)).Should().BeTrue(
            "one 15 s period after the lever the BFF knows the raised stamp");

        using (new AssertionScope())
        {
            (await ProxiedReadAsync(bffHost, browser, a)).Should().Be(HttpStatusCode.Unauthorized,
                "A's first request after the poll: its stamp is below the user's");
            (await ProxiedReadAsync(bffHost, browser, b)).Should().Be(HttpStatusCode.Unauthorized,
                "B's first request after the poll: the lever is per user, not per session");
            (await ProxiedReadAsync(bffHost, browser, c)).Should().Be(HttpStatusCode.OK,
                "C belongs to another user, whose stamp did not move");
            sessions.GetSession(a).Should().BeNull("A ended");
            sessions.GetSession(b).Should().BeNull("B ended");
        }

        // A sign-in after the lever is given the raised stamp, and the stamp does not end it.
        var d = await SignInThroughTheBffAsync(bffHost, browser, email);
        var signedInAfter = sessions.GetSession(d);
        signedInAfter.Should().NotBeNull("a sign-in after the lever is not ended by it");
        signedInAfter!.SessionStamp.Should().Be(1, "the sign-in carries the API's current stamp");
        (await ProxiedReadAsync(bffHost, browser, d)).Should().Be(HttpStatusCode.OK);
        (await ProxiedReadAsync(bffHost, browser, c)).Should().Be(HttpStatusCode.OK, "C keeps getting 200");
    }

    /// <summary>
    /// A user loaded before the lever and written back whole after it — what
    /// <c>AuthService.SetPinAsync</c> does around its hash time — must not put the old stamp back.
    /// </summary>
    /// <remarks>
    /// Identity's <c>UpdateAsync</c> writes every column of the row and checks only
    /// <c>ConcurrencyStamp</c>, so the lever has to change that column too. Measured 2026-09-28 on
    /// LocalDB before it did: the write succeeded and the stamp went from 1 back to 0.
    /// </remarks>
    public static async Task ProveNoWholeRowWriteLowersTheStampAsync(StampLever lever, string? connectionString)
    {
        using var api = new CustomWebApplicationFactory();
        if (connectionString is not null)
        {
            api.SetConnectionString(connectionString);
        }
        var client = api.CreateClient();
        var (email, _, userId) = await RegisterAsync(client);

        // Loaded the way SetPinAsync loads it, before the lever.
        using var scope = api.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var loaded = await users.FindByIdAsync(userId.ToString());
        loaded!.SessionStamp.Should().Be(0);

        await PullAsync(lever, api, client, email, userId, connectionString);
        (await StampOfAsync(api, userId)).Should().Be(1, "the lever raised the stamp");

        // Written back after it, the way SetPinAsync writes the new PIN.
        loaded.PinHash = "written-after-the-lever";
        var result = await users.UpdateAsync(loaded);

        using (new AssertionScope())
        {
            result.Errors.Select(e => e.Code).Should().Equal(
                ["ConcurrencyFailure"], "the lever gave the row a new ConcurrencyStamp");
            (await StampOfAsync(api, userId)).Should().Be(1, "no write of a user loaded earlier lowers the stamp");
        }
    }

    private static async Task PullAsync(
        StampLever lever, CustomWebApplicationFactory api, HttpClient apiClient, string email, Guid userId,
        string? connectionString)
    {
        switch (lever)
        {
            case StampLever.Logout:
                {
                    // As an operator would with Bruno: sign in straight at the API, then sign out everywhere.
                    var access = await SignInDirectlyAsync(apiClient, email);
                    using var logout = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
                    logout.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
                    (await apiClient.SendAsync(logout)).StatusCode.Should().Be(HttpStatusCode.OK);
                    return;
                }

            case StampLever.RunbookSql:
                {
                    // The block exactly as the runbook prints it, with the user's id pasted in.
                    var runbook = Path.Combine(
                        RunbookSqlParsesSqlServerTests.RepositoryRoot().FullName,
                        "docs", "runbooks", "refresh-token-reuse-recorded.md");
                    var block = RunbookMarkdown.Read(await File.ReadAllLinesAsync(runbook)).Fences
                        .Where(f => f.IsSql && f.Text.Contains("SessionStamp", StringComparison.Ordinal))
                        .Should().ContainSingle("step 4 is the runbook's one block that raises the stamp")
                        .Which.Text;
                    block.Split(RunbookPlaceholder).Length.Should().Be(3, "the id goes in two places");

                    await using var connection = new SqlConnection(connectionString);
                    await connection.OpenAsync();
                    await using var command = connection.CreateCommand();
                    command.CommandText = block.Replace(RunbookPlaceholder, $"'{userId}'", StringComparison.Ordinal);
                    await command.ExecuteNonQueryAsync();
                    return;
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(lever), lever, null);
        }
    }

    public static async Task<(string Email, string Access, Guid UserId)> RegisterAsync(HttpClient apiClient)
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        var email = $"stamp{unique}@example.com";
        var response = await apiClient.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = $"stamp_{unique}",
            Email = email,
            Password = Password,
            FirstName = "Session",
            LastName = "Stamp",
        }, Json);
        response.EnsureSuccessStatusCode();
        var data = (await response.Content.ReadFromJsonAsync<ApiResponse<RegisterResponse>>(Json))!.Data!;
        return (email, data.Token.AccessToken, data.User.Id);
    }

    public static async Task<string> SignInDirectlyAsync(HttpClient apiClient, string email)
    {
        var login = await apiClient.PostAsJsonAsync("/api/auth/login",
            new LoginRequest { Email = email, Password = Password }, Json);
        login.EnsureSuccessStatusCode();
        return (await login.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>(Json))!.Data!.Token.AccessToken;
    }

    private static async Task<string> SignInThroughTheBffAsync(
        BffOverApiFactory bffHost, HttpClient browser, string email)
    {
        var response = await browser.PostAsJsonAsync("/bff/auth/login",
            new LoginRequest { Email = email, Password = Password }, Json);
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"the sign-in through the BFF must work (body: {await response.Content.ReadAsStringAsync()})");

        var prefix = CookieName(bffHost) + "=";
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(prefix, StringComparison.Ordinal));
        return cookie[prefix.Length..].Split(';')[0];
    }

    /// <summary>A proxied read, as the SPA makes one: the session cookie, and nothing else.</summary>
    private static async Task<HttpStatusCode> ProxiedReadAsync(
        BffOverApiFactory bffHost, HttpClient browser, string sessionId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/accounts");
        request.Headers.Add("Cookie", $"{CookieName(bffHost)}={sessionId}");
        using var response = await browser.SendAsync(request);
        return response.StatusCode;
    }

    private static string CookieName(BffOverApiFactory bffHost) =>
        bffHost.Services.GetRequiredService<IOptions<BffSessionOptions>>().Value.CookieName;

    public static async Task<int> StampOfAsync(CustomWebApplicationFactory api, Guid userId)
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        return await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.SessionStamp).SingleAsync();
    }

    public static async Task<List<AzureBank.Shared.Entities.RefreshToken>> GrantsOfAsync(
        CustomWebApplicationFactory api, Guid userId)
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        return await db.RefreshTokens.AsNoTracking().Where(t => t.UserId == userId).ToListAsync();
    }

    private static async Task<bool> EventuallyAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }
            await Task.Delay(25);
        }
        return true;
    }
}
