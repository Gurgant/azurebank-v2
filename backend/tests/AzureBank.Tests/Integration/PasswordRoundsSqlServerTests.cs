using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Services.Implementations;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AzureBank.Tests.Integration;

/// <summary>
/// A password stored at the earlier count of 100,000 rounds, on SQL Server: the right password
/// signs in, or sets a first PIN, and the row then holds a hash at 220,000 (ADR-0003).
/// </summary>
/// <remarks>
/// On a relational store the new hash is stored by a set-based UPDATE, in the middle of a request
/// that also clears the count of wrong passwords with another, and each takes the row the request
/// loaded out of the change tracker (<see cref="FirstPinAfterAWrongPasswordSqlServerTests"/> says
/// why). InMemory writes both through the tracked row and so cannot show them together: hence
/// real requests through the real host against the database named by
/// <c>AZUREBANK_TEST_SQLSERVER</c>. What that UPDATE leaves alone is in
/// <see cref="PasswordRehashWriteSqlServerTests"/>.
/// </remarks>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class PasswordRoundsSqlServerTests : IDisposable
{
    private const string Password = "TestPass123!";
    private const string WrongPassword = "NotThePassword1!";
    private const string Pin = "424242";

    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private CustomWebApplicationFactory? _factory;

    public void Dispose() => _factory?.Dispose();

    [SqlServerFact]
    public async Task AfterAWrongPassword_TheRightOneSignsIn_AndTheHashIsStoredAt220000()
    {
        var (client, email) = await RegisterAsync();
        var old = await StoreAHashAt100000Async(email);

        var wrong = await SignInAsync(client, email, WrongPassword);
        var afterWrong = await StoredAsync(email);
        var right = await SignInAsync(client, email, Password);
        var afterRight = await StoredAsync(email);
        var again = await SignInAsync(client, email, Password);
        var afterAgain = await StoredAsync(email);

        using var all = new AssertionScope();
        wrong.Should().Be(HttpStatusCode.Unauthorized);
        afterWrong.PasswordHash.Should().Be(old, "a wrong password proves nothing, so nothing is stored");
        afterWrong.AccessFailedCount.Should().Be(1, "and it is counted");
        right.Should().Be(HttpStatusCode.OK, "a password stored before the count was raised still signs in");
        AccountPasswordHasher.IterationsOf(afterRight.PasswordHash).Should().Be(220_000);
        afterRight.AccessFailedCount.Should().Be(0, "the write of the new hash must not put the old count back");
        again.Should().Be(HttpStatusCode.OK, "the new hash is of the same password");
        afterAgain.PasswordHash.Should().Be(afterRight.PasswordHash, "a hash at the count is left as it is");
    }

    [SqlServerFact]
    public async Task AfterAWrongPassword_TheRightOneSetsAFirstPin_AndTheHashIsStoredAt220000()
    {
        var (client, email) = await RegisterAsync();
        await StoreAHashAt100000Async(email);

        var wrong = await SetPinAsync(client, WrongPassword);
        var right = await SetPinAsync(client, Password);
        var stored = await StoredAsync(email);
        var signedIn = await SignInAsync(client, email, Password);

        using var all = new AssertionScope();
        wrong.Should().Be(HttpStatusCode.Unauthorized);
        right.Should().Be(HttpStatusCode.OK);
        stored.PinHash.Should().NotBeNullOrEmpty("the PIN is stored by the write that follows the new hash");
        AccountPasswordHasher.IterationsOf(stored.PasswordHash).Should().Be(220_000);
        stored.AccessFailedCount.Should().Be(0);
        signedIn.Should().Be(HttpStatusCode.OK, "the new hash is of the same password");
    }

    [SqlServerFact]
    public async Task SignInsAtTheSameMoment_OnAHashAt100000_AllSignIn()
    {
        var (client, email) = await RegisterAsync();
        await StoreAHashAt100000Async(email);

        // Each loads the row before any of them has stored the new hash, so one write lands and
        // the others find another hash in the row than the one they verified.
        var together = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(_ => SignInAsync(client, email, Password)));
        var stored = await StoredAsync(email);
        var after = await SignInAsync(client, email, Password);

        using var all = new AssertionScope();
        together.Should().OnlyContain(
            status => status == HttpStatusCode.OK, "the password was right in every one of them");
        AccountPasswordHasher.IterationsOf(stored.PasswordHash).Should().Be(220_000);
        after.Should().Be(HttpStatusCode.OK);
    }

    [SqlServerFact]
    public async Task ASignInAndAFirstPinAtTheSameMoment_OnAHashAt100000_BothSucceed()
    {
        var (client, email) = await RegisterAsync();
        await StoreAHashAt100000Async(email);

        var signIns = Enumerable.Range(0, 3).Select(_ => SignInAsync(client, email, Password)).ToList();
        var pin = SetPinAsync(client, Password);
        var signedIn = await Task.WhenAll(signIns);
        var pinSet = await pin;
        var stored = await StoredAsync(email);

        using var all = new AssertionScope();
        signedIn.Should().OnlyContain(status => status == HttpStatusCode.OK);
        pinSet.Should().Be(HttpStatusCode.OK, "whichever request stored the new hash, the PIN is stored after it");
        stored.PinHash.Should().NotBeNullOrEmpty();
        AccountPasswordHasher.IterationsOf(stored.PasswordHash).Should().Be(220_000);
    }

    private async Task<(HttpClient Client, string Email)> RegisterAsync()
    {
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        var client = _factory.CreateClient();

        var unique = Guid.NewGuid().ToString("N")[..8];
        var email = $"rounds{unique}@example.com";
        using var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = $"rounds_{unique}",
            Email = email,
            Password = Password,
            FirstName = "Rounds",
            LastName = "Test",
        }, Json);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<ApiResponse<RegisterResponse>>(Json);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", result!.Data!.Token.AccessToken);
        return (client, email);
    }

    private static async Task<HttpStatusCode> SignInAsync(HttpClient client, string email, string password)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest { Email = email, Password = password }, Json);
        return response.StatusCode;
    }

    private static async Task<HttpStatusCode> SetPinAsync(HttpClient client, string password)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/auth/pin", new SetPinRequest { Pin = Pin, Password = password }, Json);
        return response.StatusCode;
    }

    private async Task<(string? PasswordHash, int AccessFailedCount, string? PinHash)> StoredAsync(string email)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Email == email);
        return (user.PasswordHash, user.AccessFailedCount, user.PinHash);
    }

    /// <summary>Stores the password as Identity's hasher made it at 100,000 rounds, and answers that hash.</summary>
    private async Task<string> StoreAHashAt100000Async(string email)
    {
        var hash =
            new PasswordHasher<ApplicationUser>(Options.Create(new PasswordHasherOptions { IterationCount = 100_000 }))
                .HashPassword(null!, Password);
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var written = await db.Users.Where(u => u.Email == email)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.PasswordHash, hash));
        written.Should().Be(1, "ARRANGE: the hash at the earlier count is in the row");
        return hash;
    }
}
