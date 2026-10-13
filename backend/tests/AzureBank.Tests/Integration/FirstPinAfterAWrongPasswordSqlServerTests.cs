using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AzureBank.Tests.Integration;

/// <summary>
/// A first PIN set with the right password after a wrong one: it is stored, and the count of wrong
/// passwords the right one cleared is still cleared once it is. On SQL Server, the only place the
/// two writes are two statements.
/// </summary>
/// <remarks>
/// <para>
/// Enrolling a PIN asks for the account password, and a wrong one counts against the login lockout
/// (ADR-0012). The right one clears the count, as a sign-in does. On a relational store the
/// clearing is a set-based UPDATE that the change tracker never sees, and the row the request
/// loaded is taken out of the tracker so that no later save writes the old count back. The PIN is
/// then stored by Identity's update of that same row, which first looks the user up again: with
/// the row left out of the tracker that lookup tracks a second copy, the update cannot attach the
/// first, and the request answers 500 with no PIN stored.
/// </para>
/// <para>
/// InMemory cannot show either half: there the clearing writes through the tracked row, so the
/// row is never taken out and never disagrees with the store. Hence real requests through the real
/// host against the database named by <c>AZUREBANK_TEST_SQLSERVER</c>.
/// </para>
/// </remarks>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class FirstPinAfterAWrongPasswordSqlServerTests : IDisposable
{
    private const string Password = "TestPass123!";
    private const string WrongPassword = "NotThePassword1!";
    private const string Pin = "424242";

    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private CustomWebApplicationFactory? _factory;

    public void Dispose() => _factory?.Dispose();

    [SqlServerFact]
    public async Task AfterOneWrongPassword_TheRightOneStoresThePin_AndTheCountStaysCleared()
    {
        var (client, azureTag, _) = await RegisterAsync();

        (await SetPinAsync(client, WrongPassword)).Should().Be(HttpStatusCode.Unauthorized);
        (await StoredAsync(azureTag)).AccessFailedCount.Should().Be(1, "ARRANGE: the wrong password was counted");

        var enrolled = await SetPinAsync(client, Password);

        var stored = await StoredAsync(azureTag);
        using var all = new AssertionScope();
        enrolled.Should().Be(HttpStatusCode.OK);
        stored.PinHash.Should().NotBeNullOrEmpty("the PIN is stored by the write that follows the clearing");
        stored.AccessFailedCount.Should().Be(0, "the write that stores the PIN must not put the old count back");
        stored.LockoutEnd.Should().BeNull();
        (await PinVerifiesAsync(client)).Should().BeTrue("the stored PIN is the one that was sent");
        await AssertOneEnrolmentIsOnRecordAsync(stored.Id);
    }

    [SqlServerFact]
    public async Task WithNoWrongPasswordBefore_TheRightOneStoresThePin()
    {
        // The control: nothing to clear, so the row is never taken out of the tracker.
        var (client, azureTag, _) = await RegisterAsync();

        var enrolled = await SetPinAsync(client, Password);

        var stored = await StoredAsync(azureTag);
        using var all = new AssertionScope();
        enrolled.Should().Be(HttpStatusCode.OK);
        stored.PinHash.Should().NotBeNullOrEmpty();
        stored.AccessFailedCount.Should().Be(0);
        stored.LockoutEnd.Should().BeNull();
        (await PinVerifiesAsync(client)).Should().BeTrue("the stored PIN is the one that was sent");
        await AssertOneEnrolmentIsOnRecordAsync(stored.Id);
    }

    [SqlServerFact]
    public async Task AWrongPassword_IsRefused_IsCounted_AndStoresNothing()
    {
        var (client, azureTag, _) = await RegisterAsync();

        var first = await SetPinAsync(client, WrongPassword);
        var afterOne = await StoredAsync(azureTag);
        var second = await SetPinAsync(client, WrongPassword);
        var afterTwo = await StoredAsync(azureTag);

        using var all = new AssertionScope();
        first.Should().Be(HttpStatusCode.Unauthorized);
        second.Should().Be(HttpStatusCode.Unauthorized);
        afterOne.AccessFailedCount.Should().Be(1);
        afterTwo.AccessFailedCount.Should().Be(2, "each wrong password counts toward the login lockout");
        afterTwo.LockoutEnd.Should().BeNull("two wrong passwords are under the threshold");
        afterTwo.PinHash.Should().BeNull("no password was proved, so no PIN is stored");
        await AssertNoEnrolmentIsOnRecordAsync(afterTwo.Id);
    }

    [SqlServerFact]
    public async Task AfterALockThatHasRunOut_TheRightPasswordStoresThePin_AndTheLockIsCleared()
    {
        /*
          The other way into the clearing: the wrong password that reaches the threshold sets the
          lock and puts the count back to zero in one statement, so once the lock has run out the
          row holds a count of zero and a LockoutEnd in the past. The right password clears that
          too, and the row is taken out of the tracker exactly as it is for a count.
        */
        var (client, azureTag, _) = await RegisterAsync();

        for (var attempt = 0; attempt < ValidationRules.MaxLoginAttempts; attempt++)
        {
            (await SetPinAsync(client, WrongPassword)).Should().Be(HttpStatusCode.Unauthorized);
        }

        var locked = await StoredAsync(azureTag);
        locked.AccessFailedCount.Should().Be(0, "ARRANGE: the lock replaced the count");
        locked.LockoutEnd.Should().BeAfter(DateTimeOffset.UtcNow, "ARRANGE: the account is locked");

        var whileLocked = await SetPinAsync(client, Password);
        whileLocked.Should().Be(
            HttpStatusCode.TooManyRequests, "the lock is revealed to a caller that proved the password");
        (await StoredAsync(azureTag)).PinHash.Should().BeNull("a locked account enrols nothing");

        await TheLockRanOutAsync(locked.Id);

        var enrolled = await SetPinAsync(client, Password);

        var stored = await StoredAsync(azureTag);
        using var all = new AssertionScope();
        enrolled.Should().Be(HttpStatusCode.OK);
        stored.PinHash.Should().NotBeNullOrEmpty();
        stored.AccessFailedCount.Should().Be(0);
        stored.LockoutEnd.Should().BeNull("the write that stores the PIN must not put the old lock back");
        (await PinVerifiesAsync(client)).Should().BeTrue("the stored PIN is the one that was sent");
        await AssertOneEnrolmentIsOnRecordAsync(stored.Id);
    }

    [SqlServerFact]
    public async Task ASignInAfterAWrongPassword_StillSucceeds_AndTheCountIsCleared()
    {
        /*
          The sign-in clears the count through the same statement and leaves its user out of the
          tracker, which is right there: all it saves afterwards is the grant, by the user's key.
          This holds that the enrolment's way of tracking the user again stayed out of that path.
        */
        var (client, azureTag, email) = await RegisterAsync();

        (await SignInAsync(client, email, WrongPassword)).Should().Be(HttpStatusCode.Unauthorized);
        (await StoredAsync(azureTag)).AccessFailedCount.Should().Be(1, "ARRANGE: the wrong password was counted");

        var signedIn = await SignInAsync(client, email, Password);

        var stored = await StoredAsync(azureTag);
        using var all = new AssertionScope();
        signedIn.Should().Be(HttpStatusCode.OK);
        stored.AccessFailedCount.Should().Be(0, "the right password clears the count");
        stored.LockoutEnd.Should().BeNull();
    }

    private async Task<(HttpClient Client, string AzureTag, string Email)> RegisterAsync()
    {
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        var client = _factory.CreateClient();

        var unique = Guid.NewGuid().ToString("N")[..8];
        var azureTag = $"firstpin_{unique}";
        var email = $"firstpin{unique}@example.com";
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = azureTag,
            Email = email,
            Password = Password,
            FirstName = "First",
            LastName = "Pin",
        }, Json);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<ApiResponse<RegisterResponse>>(Json);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", result!.Data!.Token.AccessToken);
        return (client, azureTag, email);
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

    private static async Task<bool> PinVerifiesAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/auth/pin/verify", new VerifyPinRequest { Pin = Pin }, Json);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            return false;
        }

        return (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data").GetProperty("verified").GetBoolean();
    }

    private async Task<(Guid Id, int AccessFailedCount, DateTimeOffset? LockoutEnd, string? PinHash)> StoredAsync(
        string azureTag)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.AzureTag == azureTag);
        return (user.Id, user.AccessFailedCount, user.LockoutEnd, user.PinHash);
    }

    /// <summary>
    /// Stands in for the lockout's minutes going by: moves the lock's end into the past and touches
    /// nothing else on the row.
    /// </summary>
    private async Task TheLockRanOutAsync(Guid userId)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var moved = await db.Users.Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.LockoutEnd, DateTimeOffset.UtcNow.AddMinutes(-1)));
        moved.Should().Be(1, "ARRANGE: the lock was moved into the past");
    }

    /// <summary>
    /// One audit row and one owed notice for the enrolment, and the notice names that audit row
    /// (ADR-0044, ADR-0045, ADR-0052).
    /// </summary>
    private async Task AssertOneEnrolmentIsOnRecordAsync(Guid userId)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();

        var evidence = await db.AuditEvents.AsNoTracking()
            .Where(e => e.ActorUserId == userId && e.Event == SecurityEvents.PinEnrolled)
            .Select(e => e.Id)
            .ToListAsync();
        var notices = await db.SubscriberNotices.AsNoTracking()
            .Where(n => n.UserId == userId)
            .Select(n => new { n.Event, n.AuditEventId })
            .ToListAsync();

        evidence.Should().HaveCount(1, "one enrolment leaves one audit row");
        notices.Should().HaveCount(1, "one enrolment owes one notice");
        if (evidence.Count == 1 && notices.Count == 1)
        {
            notices[0].Event.Should().Be(SecurityEvents.PinEnrolled);
            notices[0].AuditEventId.Should().Be(evidence[0], "the notice names its own audit row");
        }
    }

    private async Task AssertNoEnrolmentIsOnRecordAsync(Guid userId)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();

        (await db.AuditEvents.AsNoTracking()
            .CountAsync(e => e.ActorUserId == userId && e.Event == SecurityEvents.PinEnrolled))
            .Should().Be(0, "no evidence claims an enrolment that did not happen");
        (await db.SubscriberNotices.AsNoTracking().CountAsync(n => n.UserId == userId))
            .Should().Be(0, "no notice is owed for an enrolment that did not happen");
    }
}
