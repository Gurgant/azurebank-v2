using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AzureBank.Tests.Integration;

/// <summary>
/// Registration is all-or-nothing: if the starter account cannot be written, the user is not
/// created either.
///
/// <para>
/// ADR-0036 closed ONE cause of that account INSERT failing — a duplicate account number, which is
/// now retried — and recorded the rest as an open residual: <c>UserManager.CreateAsync</c> committed
/// the <c>ApplicationUser</c> in its own unit of work, so ANY other failure left a user holding the
/// email and the AzureTag with no account, unable to register again because the pre-checks then
/// find their own row and return the enumeration-neutral 409 (ADR-0013).
/// </para>
/// <para>
/// Measured before this change, with the collision-independent fault this class injects: <c>500</c>
/// to the client, user COMMITTED, role assigned, ZERO accounts, and <c>409</c> forever after. The
/// assertions below are the inverse of each of those.
/// </para>
/// <para>
/// The fault is a genuine SQL Server truncation error, not a fabricated exception and not a
/// duplicate key: it stands in for the class ADR-0036 left open — a command timeout (which
/// <c>EnableRetryOnFailure</c> deliberately does NOT retry, per ADR-0034), a dropped connection, an
/// unanticipated constraint — while staying deterministic enough to assert on.
/// </para>
/// <para>
/// SQL-gated because the whole claim is about a real transaction rolling back a real INSERT, which
/// EF InMemory cannot model.
/// </para>
/// </summary>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class RegistrationAtomicitySqlServerTests : IDisposable
{
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private CustomWebApplicationFactory? _factory;

    [SqlServerFact]
    public async Task AFailedAccountWriteLeavesNoUserBehind()
    {
        var client = CreateClient();
        var fault = new OverlongAccountNameInterceptor();
        _factory!.AddInterceptor(fault);

        var unique = Guid.NewGuid().ToString("N")[..8];
        var request = NewRegistration(unique);

        var response = await client.PostAsJsonAsync("/api/auth/register", request, Json);

        fault.Fired.Should().BeTrue(
            "the test proves nothing if the account write was never actually made to fail");
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError,
            "the failure itself is not recoverable — only its side effects are undone");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();

        /*
          THE POINT. Before this change the user existed here, held the email and the AzureTag, owned
          no account, and could never register again. Nothing may survive a rolled-back registration.
        */
        var user = await db.Users.SingleOrDefaultAsync(u => u.Email == request.Email);
        user.Should().BeNull("a registration that could not complete must leave no user behind");

        (await db.Accounts.CountAsync(a => a.AccountNumber != null && a.User.Email == request.Email))
            .Should().Be(0, "and certainly no account");
    }

    [SqlServerFact]
    public async Task TheCallerCanSimplyRegisterAgainAfterwards()
    {
        /*
          The consequence that actually matters to a person. Rolling the user back is only worth
          anything if the same details then work — otherwise the failure is still terminal, just
          tidier in the database.

          The interceptor is one-shot, so the second attempt runs clean. That is the whole recovery
          story: a transient hiccup during registration costs a retry, not an account.
        */
        var client = CreateClient();
        var fault = new OverlongAccountNameInterceptor();
        _factory!.AddInterceptor(fault);

        var unique = Guid.NewGuid().ToString("N")[..8];
        var request = NewRegistration(unique);

        (await client.PostAsJsonAsync("/api/auth/register", request, Json))
            .StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        fault.Fired.Should().BeTrue();

        var second = await client.PostAsJsonAsync("/api/auth/register", request, Json);
        second.StatusCode.Should().Be(HttpStatusCode.Created,
            "the same details must work on a retry — a 409 here would mean the user was stranded");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var user = await db.Users.SingleAsync(u => u.Email == request.Email);
        (await db.Accounts.CountAsync(a => a.UserId == user.Id))
            .Should().Be(1, "the retry produces exactly one account, not zero and not two");
    }

    [SqlServerFact]
    public async Task ARealDuplicateStillGetsTheNeutralConflict()
    {
        /*
          The guard against over-correcting. Making registration atomic must not turn a genuine
          duplicate into something else: a second registration with the same details is still the
          enumeration-neutral 409 of ADR-0013, NOT a 500 and not a retry loop. Without this, a
          transaction wrapped around the whole method could easily convert the duplicate path into a
          rollback-and-rethrow and quietly reopen the enumeration oracle.
        */
        var client = CreateClient();
        var unique = Guid.NewGuid().ToString("N")[..8];
        var request = NewRegistration(unique);

        (await client.PostAsJsonAsync("/api/auth/register", request, Json))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var again = await client.PostAsJsonAsync("/api/auth/register", request, Json);
        again.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "a real duplicate is still the neutral 409, not a 500");

        var body = await again.Content.ReadAsStringAsync();
        body.Should().Contain("Registration could not be completed.",
            "and still the neutral message, which is what keeps it from being an existence oracle");
    }

    private static RegisterRequest NewRegistration(string unique) => new()
    {
        AzureTag = $"atomic_{unique}",
        Email = $"atomic{unique}@example.com",
        Password = "SecurePass123!",
        FirstName = "Atom",
        LastName = "Icity",
    };

    private HttpClient CreateClient()
    {
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        return _factory.CreateClient();
    }

    public void Dispose() => _factory?.Dispose();
}
