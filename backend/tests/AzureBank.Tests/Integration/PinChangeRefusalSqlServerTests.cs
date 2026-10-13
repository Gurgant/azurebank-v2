using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.Enums;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AzureBank.Tests.Integration;

/// <summary>
/// A wrong current PIN on a PIN change, and then the lock, on SQL Server: each refusal's row is
/// committed although the request that caused it fails, and the chain they joined still verifies.
/// </summary>
/// <remarks>
/// The same proof <c>MintPinRefusalSqlServerTests</c> gives for the mints, and behind
/// <c>[SqlServerFact]</c> for the same reason: <c>RecordRefusalAsync</c> commits on its own
/// connection (ADR-0044), and the InMemory provider has no connection to prove that against.
/// <c>AuditTrailPersistenceTests</c> holds the row's shape and the counts on every run.
/// </remarks>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class PinChangeRefusalSqlServerTests : IDisposable
{
    private const string Password = "SecurePass123!";

    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private CustomWebApplicationFactory? _factory;

    [SqlServerFact]
    public async Task WrongCurrentPinsUpToTheLock_CommitTheirRows_AndTheChainStillVerifies()
    {
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        var client = _factory.CreateClient();
        var userId = await RegisterWithPinAsync(client, "123456");

        var answers = new List<HttpStatusCode>();
        foreach (var currentPin in new[] { "999999", "999999", "999999", "123456" })
        {
            var response = await client.PostAsJsonAsync(
                "/api/auth/pin", new SetPinRequest { Pin = "654321", CurrentPin = currentPin }, Json);
            answers.Add(response.StatusCode);
        }

        answers.Should().Equal(
            [
                HttpStatusCode.Unauthorized,
                HttpStatusCode.Unauthorized,
                HttpStatusCode.TooManyRequests,
                HttpStatusCode.TooManyRequests,
            ],
            "the third wrong PIN locks, and the lock refuses the right PIN too");

        // A fresh scope, so the answer comes from SQL Server and not from any tracker.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var rows = await db.AuditEvents.AsNoTracking()
            .Where(e => e.ActorUserId == userId && e.Event == SecurityEvents.PinChangeRefused)
            .OrderBy(e => e.Sequence)
            .ToListAsync();

        rows.Select(r => r.Detail).Should().Equal(
            [ErrorCodes.InvalidPin, ErrorCodes.InvalidPin, ErrorCodes.PinLocked, ErrorCodes.PinLocked],
            "one committed row for each refused request, in the order they were refused");
        rows.Should().OnlyContain(r =>
            r.Outcome == AuditOutcome.Refused && r.SubjectType == "User" && r.SubjectId == userId);

        (await db.AuditEvents.AsNoTracking()
                .CountAsync(e => e.ActorUserId == userId && e.Event == SecurityEvents.PinChanged))
            .Should().Be(0, "no change happened");

        var verification = await scope.ServiceProvider.GetRequiredService<IAuditChain>().VerifyAsync(db);
        verification.IsIntact.Should().BeTrue(because: verification.Reason);
        verification.Verified.Should().BeGreaterThanOrEqualTo(
            rows.Count + 1, "the verdict read the enrolment's row and the four refusals");
    }

    [SqlServerFact]
    public async Task ARightCurrentPin_CommitsTheChange_AndNoRefusal()
    {
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        var client = _factory.CreateClient();
        var userId = await RegisterWithPinAsync(client, "123456");

        var response = await client.PostAsJsonAsync(
            "/api/auth/pin", new SetPinRequest { Pin = "654321", CurrentPin = "123456" }, Json);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var events = await db.AuditEvents.AsNoTracking()
            .Where(e => e.ActorUserId == userId)
            .OrderBy(e => e.Sequence)
            .Select(e => e.Event)
            .ToListAsync();

        events.Should().Equal([SecurityEvents.PinEnrolled, SecurityEvents.PinChanged]);
    }

    private static async Task<Guid> RegisterWithPinAsync(HttpClient client, string pin)
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = $"pinc_{unique}",
            Email = $"pinc{unique}@example.com",
            Password = Password,
            FirstName = "Change",
            LastName = "Refusal",
        }, Json);
        response.EnsureSuccessStatusCode();

        var registered = (await response.Content.ReadFromJsonAsync<ApiResponse<RegisterResponse>>(Json))!.Data!;
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", registered.Token.AccessToken);
        (await client.PostAsJsonAsync(
                "/api/auth/pin", new SetPinRequest { Pin = pin, Password = Password }, Json))
            .EnsureSuccessStatusCode();

        return registered.User.Id;
    }

    public void Dispose() => _factory?.Dispose();
}
