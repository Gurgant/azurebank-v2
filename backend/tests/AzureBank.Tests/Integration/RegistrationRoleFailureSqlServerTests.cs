using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AzureBank.Tests.Integration;

/// <summary>
/// A registration whose default role cannot be written leaves no user behind: the user's row, which
/// was written first, is rolled back with it (ADR-0037).
/// </summary>
/// <remarks>
/// <para>
/// The sibling of <see cref="RegistrationAtomicitySqlServerTests"/>, one step earlier. That class
/// fails the INSERT of the starter account; this one fails the INSERT of the role's row, which
/// Identity sends in a save of its own after the user's.
/// </para>
/// <para>
/// The statement is refused by <see cref="FailingCommandInterceptor"/>, with a fault the execution
/// strategy does not retry. The other way the step fails, a database with no default role, is not
/// held here: the role is a row every other class of the collection reads.
/// </para>
/// <para>
/// SQL-gated because the claim is that a real transaction undoes a real INSERT, which EF InMemory
/// cannot model. <c>RegistrationRoleFailureTests</c> holds what the service answers, on mocks.
/// </para>
/// </remarks>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class RegistrationRoleFailureSqlServerTests : IDisposable
{
    private const string RoleInsert = "INSERT INTO [AspNetUserRoles]";

    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private CustomWebApplicationFactory? _factory;

    [SqlServerFact]
    public async Task ARoleThatCannotBeWritten_Is500_AndLeavesNoUser_NoAccount_AndNoGrant()
    {
        var client = CreateClient();
        var refused = FailingCommandInterceptor.OnText(RoleInsert);
        _factory!.AddInterceptor(refused);
        var request = NewRegistration();

        using var response = await client.PostAsJsonAsync("/api/auth/register", request, Json);

        refused.Failures.Should().Be(1, "the role's insert must actually have been refused, else the test proves nothing");
        var body = await response.Content.ReadAsStringAsync();
        var left = await RowsOfAsync(request);
        using (new AssertionScope())
        {
            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError, body);
            using var problem = JsonDocument.Parse(body);
            problem.RootElement.GetProperty("status").GetInt32().Should().Be(500);
            problem.RootElement.GetProperty("title").GetString().Should().Be("Internal Server Error");
            problem.RootElement.GetProperty("detail").GetString()
                .Should().Be("An unexpected error occurred. Please try again later.");
            body.Should().NotContain(FailingCommandInterceptor.Message, "the fault is the server's and is not told");

            left.Users.Should().Be(0, "the user was written before the role, and went back with it");
            left.Roles.Should().Be(0);
            left.Accounts.Should().Be(0, "the starter account comes after the role");
            left.Grants.Should().Be(0, "a grant is issued only for a registration that happened");
        }
    }

    [SqlServerFact]
    public async Task WithNothingRefused_TheSameRegistrationLeavesAUser_ARole_AnAccount_AndAGrant()
    {
        // CONTROL: the same host and the same interceptor, which sees the role's insert and lets it
        // pass. The four counts the refusal leaves at 0 are 1 here.
        var client = CreateClient();
        var seen = 0;
        var watching = FailingCommandInterceptor.OnTurnsOfText(
            turn =>
            {
                seen = turn;
                return false;
            },
            RoleInsert);
        _factory!.AddInterceptor(watching);
        var request = NewRegistration();

        using var response = await client.PostAsJsonAsync("/api/auth/register", request, Json);

        var left = await RowsOfAsync(request);
        using var all = new AssertionScope();
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        seen.Should().Be(1, "a registration sends the statement the other tests refuse, once");
        watching.Failures.Should().Be(0);
        left.Should().Be(new Rows(Users: 1, Roles: 1, Accounts: 1, Grants: 1));
    }

    [SqlServerFact]
    public async Task TheSameDetailsRegisterOnceTheRoleCanBeWritten()
    {
        // Refused on the first turn only: the second registration is the caller trying again.
        var client = CreateClient();
        var refused = FailingCommandInterceptor.OnTurnsOfText(turn => turn == 1, RoleInsert);
        _factory!.AddInterceptor(refused);
        var request = NewRegistration();

        using var first = await client.PostAsJsonAsync("/api/auth/register", request, Json);
        first.StatusCode.Should().Be(HttpStatusCode.InternalServerError, "ARRANGE");
        refused.Failures.Should().Be(1, "ARRANGE");

        using var second = await client.PostAsJsonAsync("/api/auth/register", request, Json);

        var left = await RowsOfAsync(request);
        using var all = new AssertionScope();
        second.StatusCode.Should().Be(HttpStatusCode.Created,
            "a 409 would mean the first attempt still holds the email or the handle");
        left.Should().Be(new Rows(Users: 1, Roles: 1, Accounts: 1, Grants: 1));
    }

    /// <summary>What the database holds for a registration's email and handle.</summary>
    private sealed record Rows(int Users, int Roles, int Accounts, int Grants);

    private async Task<Rows> RowsOfAsync(RegisterRequest request)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();

        var users = db.Users.Where(u => u.Email == request.Email || u.AzureTag == request.AzureTag);
        return new Rows(
            Users: await users.CountAsync(),
            Roles: await db.UserRoles.CountAsync(r => users.Any(u => u.Id == r.UserId)),
            Accounts: await db.Accounts.CountAsync(a => users.Any(u => u.Id == a.UserId)),
            Grants: await db.RefreshTokens.CountAsync(t => users.Any(u => u.Id == t.UserId)));
    }

    private static RegisterRequest NewRegistration()
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        return new RegisterRequest
        {
            AzureTag = $"norole_{unique}",
            Email = $"norole{unique}@example.com",
            Password = "SecurePass123!",
            FirstName = "Rollo",
            LastName = "Back",
        };
    }

    private HttpClient CreateClient()
    {
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        return _factory.CreateClient();
    }

    public void Dispose() => _factory?.Dispose();
}
