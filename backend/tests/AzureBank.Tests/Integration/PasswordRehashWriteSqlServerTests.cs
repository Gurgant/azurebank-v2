using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AzureBank.Api.Services.Interfaces;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Shared.Exceptions;
using AzureBank.Shared.Services.Implementations;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace AzureBank.Tests.Integration;

/// <summary>
/// What a sign-in writes when it stores a password again at 220,000 rounds (ADR-0003): the hash and
/// the update time of the user's row and nothing else of it, and only over the hash it verified.
/// </summary>
/// <remarks>
/// <para>
/// The sign-in loads the user, spends the hash time, and only then writes. Whatever another
/// request did to that user in between is in the database and not in the row this request loaded:
/// its sessions ended (ADR-0057), its PIN locked (ADR-0010), a wrong password counted (ADR-0012), a
/// new password stored. A write of the row as it was loaded would put each of them back.
/// </para>
/// <para>
/// No race is run here. The hasher of the host is <see cref="RoundCountingPasswordHasher"/>, and
/// what the other request does is done inside its verification, through the API's own services and
/// endpoints: after the load and before the write, every time. Only a relational store has the two
/// as two statements, hence the database named by <c>AZUREBANK_TEST_SQLSERVER</c>.
/// </para>
/// </remarks>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed partial class PasswordRehashWriteSqlServerTests : IDisposable
{
    private const string Password = "TestPass123!";
    private const string WrongPassword = "NotThePassword1!";
    private const string Pin = "424242";
    private const string WrongPin = "131313";

    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly RoundCountingPasswordHasher _hasher = new();
    private readonly CommandRecordingInterceptor _commands = new();
    private CustomWebApplicationFactory? _factory;
    private WebApplicationFactory<Program>? _host;

    public void Dispose()
    {
        _host?.Dispose();
        _factory?.Dispose();
    }

    [SqlServerFact]
    public async Task ASignInThatBeganBeforeTheUsersSessionsWereEnded_LeavesTheStampRaised_AndAnswersTheOneItRead()
    {
        var (client, email) = await RegisterAsync();
        await StoreAHashAt100000Async(email);
        var before = await StoredAsync(email);

        _hasher.BeforeTheNextAnswer(() => InAScope(services => services
            .GetRequiredService<IRefreshTokenService>()
            .RevokeAllForUserAsync(before.Id, RefreshTokenRevokedReason.SignOutEverywhere, DateTime.UtcNow)));
        var (status, answeredStamp) = await SignInAsync(client, email, Password);

        var after = await StoredAsync(email);
        using var all = new AssertionScope();
        status.Should().Be(HttpStatusCode.OK);
        after.SessionStamp.Should().Be(before.SessionStamp + 1, "the stamp that ended the user's sessions stays raised");
        answeredStamp.Should().Be(
            before.SessionStamp,
            "the sign-in answers the stamp it read before the sessions were ended, so its own session ends too");
        AccountPasswordHasher.IterationsOf(after.PasswordHash).Should().Be(220_000);
    }

    [SqlServerTheory]
    [InlineData(1)]
    [InlineData(ValidationRules.MaxPinAttempts)]
    public async Task ASignInThatBeganBeforeWrongPins_LeavesTheirCountAndTheirLock(int wrongPins)
    {
        var (client, email) = await RegisterAsync();
        (await SetPinAsync(client, Password)).Should().Be(HttpStatusCode.OK, "ARRANGE: the user has a PIN");
        await StoreAHashAt100000Async(email);
        var before = await StoredAsync(email);

        _hasher.BeforeTheNextAnswer(() => InAScope(async services =>
        {
            var pins = services.GetRequiredService<IPinVerifier>();
            for (var attempt = 0; attempt < wrongPins; attempt++)
            {
                try
                {
                    await pins.VerifyPinAsync(before.Id, WrongPin);
                }
                catch (PinLockedException)
                {
                    // The wrong PIN that reaches the threshold says so.
                }
            }
        }));
        var (status, _) = await SignInAsync(client, email, Password);

        var after = await StoredAsync(email);
        using var all = new AssertionScope();
        status.Should().Be(HttpStatusCode.OK);
        before.PinAccessFailedCount.Should().Be(0, "ARRANGE");
        before.PinLockoutEnd.Should().BeNull("ARRANGE");
        if (wrongPins < ValidationRules.MaxPinAttempts)
        {
            after.PinAccessFailedCount.Should().Be(wrongPins, "the wrong PINs stay counted");
            after.PinLockoutEnd.Should().BeNull();
        }
        else
        {
            after.PinLockoutEnd.Should().BeAfter(DateTimeOffset.UtcNow, "the PIN stays locked");
        }

        AccountPasswordHasher.IterationsOf(after.PasswordHash).Should().Be(220_000);
    }

    [SqlServerTheory]
    [InlineData(1)]
    [InlineData(ValidationRules.MaxLoginAttempts)]
    public async Task ASignInThatBeganBeforeWrongPasswords_LeavesTheirCountAndTheirLock(int wrongPasswords)
    {
        var (client, email) = await RegisterAsync();
        await StoreAHashAt100000Async(email);
        var before = await StoredAsync(email);

        _hasher.BeforeTheNextAnswer(() =>
        {
            for (var attempt = 0; attempt < wrongPasswords; attempt++)
            {
                InAScope(_ => SignInAsync(client, email, WrongPassword)).Status
                    .Should().Be(HttpStatusCode.Unauthorized, "ARRANGE: the other request's password is wrong");
            }
        });
        var (status, _) = await SignInAsync(client, email, Password);

        var after = await StoredAsync(email);
        using var all = new AssertionScope();
        status.Should().Be(HttpStatusCode.OK, "this sign-in read the row before any wrong password");
        before.AccessFailedCount.Should().Be(0, "ARRANGE");
        before.LockoutEnd.Should().BeNull("ARRANGE");
        if (wrongPasswords < ValidationRules.MaxLoginAttempts)
        {
            after.AccessFailedCount.Should().Be(wrongPasswords, "the wrong passwords stay counted");
            after.LockoutEnd.Should().BeNull();
        }
        else
        {
            after.LockoutEnd.Should().BeAfter(DateTimeOffset.UtcNow, "the account stays locked");
        }

        AccountPasswordHasher.IterationsOf(after.PasswordHash).Should().Be(220_000);
    }

    [SqlServerFact]
    public async Task ASignInThatBeganBeforeThePasswordHashChanged_DoesNotWriteOverTheNewOne()
    {
        var (client, email) = await RegisterAsync();
        await StoreAHashAt100000Async(email);
        var before = await StoredAsync(email);
        var ofAnotherPassword = new AccountPasswordHasher().HashPassword(null!, "AnotherPass456!");

        _hasher.BeforeTheNextAnswer(() => InAScope(services => services
            .GetRequiredService<AzureBankDbContext>().Users
            .Where(u => u.Id == before.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.PasswordHash, ofAnotherPassword))));
        var (status, _) = await SignInAsync(client, email, Password);

        var after = await StoredAsync(email);
        using var all = new AssertionScope();
        status.Should().Be(HttpStatusCode.OK, "it proved the password of the hash it loaded");
        after.PasswordHash.Should().Be(ofAnotherPassword, "a hash stored since the load is not this request's to replace");
    }

    [SqlServerFact]
    public async Task AFirstPinThatBeganBeforeThePasswordHashChanged_IsStored_AndDoesNotWriteOverTheNewHash()
    {
        // A first PIN proves the password as a sign-in does, and then saves the user's whole row.
        var (client, email) = await RegisterAsync();
        await StoreAHashAt100000Async(email);
        var before = await StoredAsync(email);
        var ofAnotherPassword = new AccountPasswordHasher().HashPassword(null!, "AnotherPass456!");

        _hasher.BeforeTheNextAnswer(() => InAScope(services => services
            .GetRequiredService<AzureBankDbContext>().Users
            .Where(u => u.Id == before.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.PasswordHash, ofAnotherPassword))));
        var status = await SetPinAsync(client, Password);

        var after = await StoredAsync(email);
        using var all = new AssertionScope();
        status.Should().Be(HttpStatusCode.OK, "it proved the password of the hash it loaded");
        after.PinHash.Should().NotBeNullOrEmpty();
        after.PasswordHash.Should().Be(ofAnotherPassword, "the save of the PIN writes the hash the row holds");
    }

    [SqlServerFact]
    public async Task ASignInOnAHashAt100000_SendsOneUpdateOfTheUsersRow_ThatSetsTheHashAndUpdatedAtOnly()
    {
        var (client, email) = await RegisterAsync();
        await StoreAHashAt100000Async(email);
        string usersTable;
        using (var scope = _host!.Services.CreateScope())
        {
            usersTable = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>()
                .Model.FindEntityType(typeof(ApplicationUser))!.GetTableName()!;
        }

        _commands.Start();
        var (first, _) = await SignInAsync(client, email, Password);
        _commands.Stop();
        var onTheOlderHash = UpdatesOf(usersTable, _commands.Writes);

        var again = new CommandRecordingInterceptor();
        _factory!.AddInterceptor(again);
        again.Start();
        var (second, _) = await SignInAsync(client, email, Password);
        again.Stop();

        using var all = new AssertionScope();
        first.Should().Be(HttpStatusCode.OK);
        second.Should().Be(HttpStatusCode.OK);
        _commands.Selects.Should().NotBeEmpty("the recorder was listening: the sign-in read the user");
        onTheOlderHash.Should().ContainSingle("the new hash is one statement")
            .Which.Should().BeEquivalentTo(nameof(ApplicationUser.PasswordHash), nameof(ApplicationUser.UpdatedAt));
        again.Selects.Should().NotBeEmpty("the recorder was listening the second time too");
        UpdatesOf(usersTable, again.Writes).Should().BeEmpty("a hash at the count is left as it is");
    }

    /// <summary>
    /// For each UPDATE of <paramref name="table"/> in <paramref name="commands"/>, the columns it
    /// sets: of a set-based statement (<c>UPDATE [u] SET ... FROM [table] AS [u]</c>) and of a
    /// tracked change (<c>UPDATE [table] SET ... OUTPUT</c> or <c>WHERE</c>).
    /// </summary>
    private static List<string[]> UpdatesOf(string table, IEnumerable<string> commands) =>
        [.. commands
            .SelectMany(command => UpdateStatement().Matches(command))
            .Where(update =>
                update.Groups["target"].Value == table
                || update.Groups["rest"].Value.StartsWith($"FROM [{table}] ", StringComparison.Ordinal))
            .Select(update => SetColumn().Matches(update.Groups["set"].Value).Select(c => c.Groups[1].Value).ToArray())];

    [GeneratedRegex(
        @"UPDATE\s+\[(?<target>\w+)\]\s+SET\s+(?<set>.*?)\s+(?<rest>(FROM|OUTPUT|WHERE)\b[^;]*)",
        RegexOptions.Singleline)]
    private static partial Regex UpdateStatement();

    [GeneratedRegex(@"(?:^|,\s*)(?:\[\w+\]\.)?\[(\w+)\]\s*=")]
    private static partial Regex SetColumn();

    private async Task<(HttpClient Client, string Email)> RegisterAsync()
    {
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        _factory.AddInterceptor(_commands);
        _host = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IPasswordHasher<ApplicationUser>>();
            services.AddSingleton<IPasswordHasher<ApplicationUser>>(_hasher);
        }));
        var client = _host.CreateClient();

        var unique = Guid.NewGuid().ToString("N")[..8];
        var email = $"rehash{unique}@example.com";
        using var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = $"rehash_{unique}",
            Email = email,
            Password = Password,
            FirstName = "Rehash",
            LastName = "Test",
        }, Json);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<ApiResponse<RegisterResponse>>(Json);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", result!.Data!.Token.AccessToken);
        return (client, email);
    }

    /// <summary>The status of a sign-in, and the session stamp it answered when it signed in.</summary>
    private static async Task<(HttpStatusCode Status, int? SessionStamp)> SignInAsync(
        HttpClient client, string email, string password)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest { Email = email, Password = password }, Json);
        if (!response.IsSuccessStatusCode)
        {
            return (response.StatusCode, null);
        }

        var result = await response.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>(Json);
        return (response.StatusCode, result!.Data!.Token.SessionStamp);
    }

    private static async Task<HttpStatusCode> SetPinAsync(HttpClient client, string password)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/auth/pin", new SetPinRequest { Pin = Pin, Password = password }, Json);
        return response.StatusCode;
    }

    /// <summary>
    /// Runs <paramref name="work"/> in a scope of its own, as another request would have one, and
    /// waits for it: the verification it is called from is not asynchronous.
    /// </summary>
    private T InAScope<T>(Func<IServiceProvider, Task<T>> work)
    {
        using var scope = _host!.Services.CreateScope();
        return work(scope.ServiceProvider).GetAwaiter().GetResult();
    }

    private void InAScope(Func<IServiceProvider, Task> work) =>
        InAScope(async services =>
        {
            await work(services);
            return true;
        });

    private async Task<ApplicationUser> StoredAsync(string email)
    {
        await using var scope = _host!.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AzureBankDbContext>()
            .Users.AsNoTracking().SingleAsync(u => u.Email == email);
    }

    /// <summary>Stores the password as Identity's hasher made it at 100,000 rounds.</summary>
    private async Task StoreAHashAt100000Async(string email)
    {
        var hash =
            new PasswordHasher<ApplicationUser>(Options.Create(new PasswordHasherOptions { IterationCount = 100_000 }))
                .HashPassword(null!, Password);
        await using var scope = _host!.Services.CreateAsyncScope();
        var written = await scope.ServiceProvider.GetRequiredService<AzureBankDbContext>()
            .Users.Where(u => u.Email == email)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.PasswordHash, hash));
        written.Should().Be(1, "ARRANGE: the hash at the earlier count is in the row");
    }
}
