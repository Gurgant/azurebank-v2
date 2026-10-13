using System.Net;
using System.Net.Http.Json;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.Entities;
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
/// The rounds an account password is hashed with, through the real host (ADR-0003): what a
/// registration stores, that a password stored at the earlier count of 100,000 still signs in and
/// is stored again at 220,000 when it does, and that a sign-in which fails spends the same rounds
/// whether the email is unknown, known with a hash at the earlier count, or known with a hash at
/// this one (ADR-0012).
/// </summary>
public class PasswordRoundsTests : IntegrationTestBase
{
    private const string Password = "SecurePass123!";
    private const string WrongPassword = "NotThePassword1!";

    public PasswordRoundsTests(CustomWebApplicationFactory factory)
        : base(factory)
    {
    }

    [Fact]
    public async Task ARegisteredPassword_IsStoredAt220000Rounds()
    {
        var email = await RegisterAsync(Client);

        var stored = await StoredHashAsync(email);

        using var all = new AssertionScope();
        stored.Should().StartWith("AQAAAAIAA1tg", "Identity V3, HMAC-SHA512, 220,000 rounds");
        AccountPasswordHasher.IterationsOf(stored).Should().Be(220_000);
    }

    [Fact]
    public async Task APasswordStoredAt100000Rounds_StillSignsIn_AndIsStoredAt220000WhenItDoes()
    {
        var email = await RegisterAsync(Client);
        var old = await StoreAHashAt100000Async(email);

        var wrong = await SignInAsync(Client, email, WrongPassword);
        var afterWrong = await StoredHashAsync(email);
        var right = await SignInAsync(Client, email, Password);
        var afterRight = await StoredHashAsync(email);
        var again = await SignInAsync(Client, email, Password);
        var afterAgain = await StoredHashAsync(email);

        using var all = new AssertionScope();
        AccountPasswordHasher.IterationsOf(old).Should().Be(100_000, "ARRANGE");
        wrong.Should().Be(HttpStatusCode.Unauthorized);
        afterWrong.Should().Be(old, "a wrong password proves nothing, so nothing is stored");
        right.Should().Be(HttpStatusCode.OK, "a password stored before the count was raised still signs in");
        AccountPasswordHasher.IterationsOf(afterRight).Should().Be(220_000, "the sign-in stored it again at the count");
        again.Should().Be(HttpStatusCode.OK, "the new hash is of the same password");
        afterAgain.Should().Be(afterRight, "a hash at the count is left as it is");
    }

    [Fact]
    public async Task ASignInThatFails_Spends220000Rounds_WhetherTheEmailIsKnownOrNot()
    {
        var rounds = new RoundCountingPasswordHasher();
        using var host = Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IPasswordHasher<ApplicationUser>>();
            services.AddSingleton<IPasswordHasher<ApplicationUser>>(rounds);
        }));
        var client = host.CreateClient();
        var storedBefore = await RegisterAsync(client);
        await StoreAHashAt100000Async(storedBefore);
        var storedNow = await RegisterAsync(client);
        // The first unknown email of a process also makes the dummy hash; this one is not counted.
        await SignInAsync(client, $"nobody{Guid.NewGuid():N}@example.com", WrongPassword);

        rounds.Clear();
        var knownBefore = await SignInAsync(client, storedBefore, WrongPassword);
        var spentBefore = rounds.Verifications;
        rounds.Clear();
        var knownNow = await SignInAsync(client, storedNow, WrongPassword);
        var spentNow = rounds.Verifications;
        rounds.Clear();
        var unknown = await SignInAsync(client, $"nobody{Guid.NewGuid():N}@example.com", WrongPassword);
        var spentUnknown = rounds.Verifications;

        using var all = new AssertionScope();
        knownBefore.Should().Be(HttpStatusCode.Unauthorized);
        knownNow.Should().Be(HttpStatusCode.Unauthorized);
        unknown.Should().Be(HttpStatusCode.Unauthorized);
        spentBefore.Should().ContainSingle("one verification for each sign-in")
            .Which.Stored.Should().Be(100_000, "ARRANGE: this user's hash is at the earlier count");
        spentNow.Should().ContainSingle().Which.Stored.Should().Be(220_000, "ARRANGE: this user's hash is at the count");
        spentUnknown.Should().ContainSingle();
        TotalOf(spentBefore).Should().Be(220_000, "a known email whose hash is at the earlier count");
        TotalOf(spentNow).Should().Be(220_000, "a known email whose hash is at the count");
        TotalOf(spentUnknown).Should().Be(220_000, "an unknown email, on the dummy hash");
    }

    [Fact]
    public async Task AUserWithNoPasswordHash_IsRefusedWithNoVerification_AndTheAttemptIsCounted()
    {
        // What Identity's own check answers for such a user, which the sign-in keeps: no hash to
        // verify, so none is verified. With the demo off this is a row nothing in the API makes.
        var rounds = new RoundCountingPasswordHasher();
        using var host = Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IPasswordHasher<ApplicationUser>>();
            services.AddSingleton<IPasswordHasher<ApplicationUser>>(rounds);
        }));
        var client = host.CreateClient();
        var email = await RegisterAsync(client);
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
            (await db.Users.SingleAsync(u => u.Email == email)).PasswordHash = null;
            await db.SaveChangesAsync();
        }

        rounds.Clear();
        var status = await SignInAsync(client, email, Password);

        using var after = Factory.Services.CreateScope();
        var stored = await after.ServiceProvider.GetRequiredService<AzureBankDbContext>()
            .Users.AsNoTracking().SingleAsync(u => u.Email == email);
        using var all = new AssertionScope();
        status.Should().Be(HttpStatusCode.Unauthorized);
        rounds.Verifications.Should().BeEmpty("there is no hash to verify");
        stored.PasswordHash.Should().BeNull("and none is stored");
        stored.AccessFailedCount.Should().Be(1, "the attempt is counted as a wrong password is");
    }

    [Fact]
    public void TheApi_HashesAccountPasswordsWithTheOneHasher()
    {
        using var scope = Factory.Services.CreateScope();

        var registered = scope.ServiceProvider.GetServices<IPasswordHasher<ApplicationUser>>();

        registered.Should().ContainSingle().Which.GetType().Should().Be<AccountPasswordHasher>();
    }

    [Fact]
    public void TheSeeder_HashesAccountPasswordsWithTheOneHasher()
    {
        using var provider = SeederHost.Build(
            new RecordingLoggerProvider(), interceptor: null, onCommittedSettings: false,
            ("ConnectionStrings:DefaultConnection", CustomWebApplicationFactory.PlaceholderConnectionString),
            ("Security:PinPepper", SeederHost.Pepper));
        using var scope = provider.CreateScope();

        var registered = scope.ServiceProvider.GetServices<IPasswordHasher<ApplicationUser>>();

        registered.Should().ContainSingle().Which.GetType().Should().Be<AccountPasswordHasher>();
    }

    private static int TotalOf(IReadOnlyList<(int Stored, int OnTop)> verifications) =>
        verifications.Sum(v => v.Stored + v.OnTop);

    private static async Task<string> RegisterAsync(HttpClient client)
    {
        var unique = Guid.NewGuid().ToString("N")[..10];
        var email = $"rounds{unique}@example.com";
        using var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = $"rounds_{unique}",
            Email = email,
            Password = Password,
            FirstName = "Rounds",
            LastName = "Test",
        }, JsonOptions);
        response.EnsureSuccessStatusCode();
        return email;
    }

    private static async Task<HttpStatusCode> SignInAsync(HttpClient client, string email, string password)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest { Email = email, Password = password }, JsonOptions);
        return response.StatusCode;
    }

    private async Task<string> StoredHashAsync(string email)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        return (await db.Users.AsNoTracking().Where(u => u.Email == email).Select(u => u.PasswordHash).SingleAsync())!;
    }

    /// <summary>Stores the password as Identity's hasher made it at 100,000 rounds, and answers that hash.</summary>
    private async Task<string> StoreAHashAt100000Async(string email)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var user = await db.Users.SingleAsync(u => u.Email == email);
        user.PasswordHash =
            new PasswordHasher<ApplicationUser>(Options.Create(new PasswordHasherOptions { IterationCount = 100_000 }))
                .HashPassword(user, Password);
        await db.SaveChangesAsync();
        return user.PasswordHash;
    }
}
