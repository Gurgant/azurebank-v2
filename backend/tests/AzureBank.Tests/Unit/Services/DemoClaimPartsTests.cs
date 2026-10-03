using System.Text.RegularExpressions;
using AzureBank.Api.Security;
using AzureBank.Shared.Constants;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Validation;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace AzureBank.Tests.Unit.Services;

/// <summary>
/// What a claim makes before it takes a copy, proved alone: the password it answers.
/// </summary>
/// <remarks>
/// <para>
/// THE PASSWORD HAS TWO READERS. A visitor reads it off a page and may type it on another device,
/// so it is four groups of four from an alphabet without the characters that pass for one another.
/// The API reads it at every sign-in: <c>LoginRequest.Password</c> carries
/// <see cref="PasswordAttribute"/>, which holds it to <see cref="ValidationRules.PasswordPattern"/>
/// before the credentials are looked at, so a password outside the pattern would be handed out and
/// then refused as malformed. Identity's validators are the rules every other user's password is
/// held to.
/// </para>
/// <para>
/// A THOUSAND AT A TIME where the claim is about every password. A draw of sixteen characters has
/// no digit about once in twelve (<c>python -c "print((48/56)**16)"</c> prints 0.0848...), so a
/// generator that never drew again would pass a test of one password eleven times in twelve.
/// </para>
/// </remarks>
public class DemoClaimPartsTests
{
    // ── The password ─────────────────────────────────────────────────────────────────────────────

    private const string FiftySix = "A-HJ-NP-Za-kmnp-z2-9";

    private static readonly Regex FourGroupsOfFour = new(
        $"^[{FiftySix}]{{4}}(-[{FiftySix}]{{4}}){{3}}$", RegexOptions.Compiled);

    private static readonly Regex OneOfTheFiftySix = new($"^[{FiftySix}]$", RegexOptions.Compiled);

    private static readonly Regex PasswordPattern = new(ValidationRules.PasswordPattern, RegexOptions.Compiled);

    /// <summary>The characters of the class above, read off the class itself.</summary>
    private static char[] TheFiftySix() =>
        Enumerable.Range(0, 128).Select(c => (char)c).Where(c => OneOfTheFiftySix.IsMatch(c.ToString())).ToArray();

    private static string[] AThousand() =>
        Enumerable.Range(0, 1000).Select(_ => DemoPasswordGenerator.Create()).ToArray();

    [Fact]
    public void APassword_IsFourGroupsOfFour_JoinedByHyphens_FromTheFiftySixCharacters()
    {
        TheFiftySix().Should().HaveCount(56, "ARRANGE: the class is 24 upper case, 24 lower case and 8 digits");
        TheFiftySix().Should().NotContain(
            ['I', 'O', 'l', 'o', '0', '1'], "ARRANGE: the six a reader takes for one another are not in it");

        var password = DemoPasswordGenerator.Create();

        password.Should().MatchRegex(FourGroupsOfFour);
        password.Should().HaveLength(19, "sixteen characters and three hyphens, and nothing after them");
    }

    [Fact]
    public async Task AThousandPasswords_MatchThePasswordPattern_AndPassIdentitysValidators()
    {
        using var factory = new CustomWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var owner = new ApplicationUser { AzureTag = "john_k7m2", FirstName = "John", LastName = "Smith" };
        users.PasswordValidators.Should().NotBeEmpty("ARRANGE: a host with no validator would pass anything");
        var signInCheck = new PasswordAttribute();

        var outsideThePattern = new List<string>();
        var refusedAtSignIn = new List<string>();
        var refusedByIdentity = new List<string>();
        foreach (var password in AThousand())
        {
            if (!PasswordPattern.IsMatch(password))
            {
                outsideThePattern.Add(password);
            }

            if (!signInCheck.IsValid(password))
            {
                refusedAtSignIn.Add(password);
            }

            foreach (var validator in users.PasswordValidators)
            {
                var result = await validator.ValidateAsync(users, owner, password);
                if (!result.Succeeded)
                {
                    refusedByIdentity.Add($"{password}: {string.Join(", ", result.Errors.Select(e => e.Code))}");
                }
            }
        }

        // One scope, so a run that fails says which of the three readers refused, and not only the first.
        using (new AssertionScope())
        {
            outsideThePattern.Should().BeEmpty("every password matches ValidationRules.PasswordPattern");
            refusedAtSignIn.Should().BeEmpty("the sign-in request's own check reads every password as well formed");
            refusedByIdentity.Should().BeEmpty("the API host's validators accept a copy's password as any user's");
        }
    }

    [Fact]
    public void AThousandPasswords_AreAllDifferent()
    {
        var passwords = AThousand();

        passwords.Distinct(StringComparer.Ordinal).Should().HaveCount(1000);
    }

    [Fact]
    public void AThousandPasswords_UseEveryOneOfTheFiftySixCharacters_AndNoOther()
    {
        // 16,000 draws over 56 characters: a generator that drew from fewer, or from one more,
        // matches the pattern on any single password that happens to miss the difference.
        var used = AThousand().SelectMany(password => password).Where(c => c != '-').Distinct().ToArray();

        used.Should().BeEquivalentTo(TheFiftySix());
    }
}
