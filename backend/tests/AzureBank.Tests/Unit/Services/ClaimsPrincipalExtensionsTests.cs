using System.Security.Claims;
using AzureBank.Api.Security;
using AzureBank.Shared.Exceptions;
using FluentAssertions;
using FluentAssertions.Execution;

namespace AzureBank.Tests.Unit.Services;

/// <summary>
/// How the API names the caller of a request: the user id of the bearer token, under
/// <see cref="ClaimTypes.NameIdentifier"/> or under <c>sub</c>, and a 401 for a token that names
/// nobody. Every controller and both middlewares that need the caller read it here, so an
/// ownership check receives the id these tests pin.
/// </summary>
public class ClaimsPrincipalExtensionsTests
{
    private static ClaimsPrincipal Caller(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authenticationType: "Bearer"));

    [Fact]
    public void TheId_IsReadFromNameIdentifier()
    {
        var id = Guid.NewGuid();

        Caller(new Claim(ClaimTypes.NameIdentifier, id.ToString())).RequireUserId().Should().Be(id);
    }

    [Fact]
    public void TheId_IsReadFromSub_WhenNameIdentifierIsMissing()
    {
        // A host that turns MapInboundClaims off leaves the token's claim under its own name.
        var id = Guid.NewGuid();

        Caller(new Claim("sub", id.ToString())).RequireUserId().Should().Be(id);
    }

    [Fact]
    public void NameIdentifier_WinsOverSub_WhenTheyDiffer()
    {
        var nameIdentifier = Guid.NewGuid();
        var sub = Guid.NewGuid();

        var caller = Caller(new Claim("sub", sub.ToString()), new Claim(ClaimTypes.NameIdentifier, nameIdentifier.ToString()));

        caller.RequireUserId().Should().Be(nameIdentifier);
    }

    [Fact]
    public void ANameIdentifierThatIsNotAUuid_IsRefused_EvenBesideAUsableSub()
    {
        // The id is the first claim present, not the first that parses: a token whose two names
        // for the caller disagree is refused rather than read from the other one.
        var caller = Caller(
            new Claim(ClaimTypes.NameIdentifier, "not-a-user-id"), new Claim("sub", Guid.NewGuid().ToString()));

        var act = () => caller.RequireUserId();

        act.Should().Throw<AuthenticationException>().Which.ErrorCode.Should().Be("AUTH_TOKEN_INVALID");
    }

    public static TheoryData<string, string, string> NoUsableId() => new()
    {
        { "no id at all", ClaimTypes.Email, "someone@example.com" },
        { "an id that is not one", "sub", "not-a-user-id" },
    };

    [Theory]
    [MemberData(nameof(NoUsableId))]
    public void ACallerWithNoUsableId_IsRefusedAsAnInvalidToken(string what, string claimType, string value)
    {
        var act = () => Caller(new Claim(claimType, value)).RequireUserId();

        var thrown = act.Should().Throw<AuthenticationException>(what).Which;
        using var all = new AssertionScope();
        thrown.ErrorCode.Should().Be("AUTH_TOKEN_INVALID");
        thrown.StatusCode.Should().Be(401);
        thrown.Message.Should().Be("A valid user identity is required.");
    }
}
