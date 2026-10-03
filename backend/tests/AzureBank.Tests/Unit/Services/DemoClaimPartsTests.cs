using System.Security.Cryptography;
using System.Text;
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
/// What a claim makes before it takes a copy, proved alone: the password it answers, and the key
/// it stores in place of its client's address.
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
/// <para>
/// THE KEY IS HELD TWICE: by what it must do (one client, one key; another secret or another client,
/// another key; no address inside it), and by one known answer worked out outside .NET, because
/// the first is as true of a function that is no HMAC at all.
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

    // ── The client's key ─────────────────────────────────────────────────────────────────────────

    /// <summary>Test-only values, NOT real secrets.</summary>
    private const string Secret = "unit-tests-only-demo-client-key-secret-0001";
    private const string AnotherSecret = "unit-tests-only-demo-client-key-secret-0002";

    /// <summary>An address reserved for documentation (RFC 5737).</summary>
    private const string Address = "203.0.113.7";

    [Fact]
    public void TheSameSecretAndAddress_GiveTheSameKey_Of32Bytes()
    {
        var first = DemoClientKey.Of(Secret, Address);
        var second = DemoClientKey.Of(Secret, Address);

        using (new AssertionScope())
        {
            first.Should().HaveCount(32, "DemoCopy.ClientKey is 32 bytes, of fixed length");
            second.Should().Equal(first, "one client has one key: its rows are found by it");
        }
    }

    [Fact]
    public void AnotherSecret_OrAnotherAddress_GivesAnotherKey()
    {
        var key = DemoClientKey.Of(Secret, Address);

        using (new AssertionScope())
        {
            DemoClientKey.Of(AnotherSecret, Address).Should().NotEqual(
                key, "the key depends on the secret: whoever lacks it cannot work out an address's key");
            DemoClientKey.Of(Secret, "203.0.113.8").Should().NotEqual(key, "another client is another key");
        }
    }

    [Theory]
    [InlineData("203.0.113.7")]
    [InlineData("2001:db8:85a3:8d3::/64")]
    [InlineData("unknown")]
    public void TheKey_IsNotTheBareHmacOfTheAddress_AndDoesNotContainIt(string address)
    {
        var addressBytes = Encoding.UTF8.GetBytes(address);

        var key = DemoClientKey.Of(Secret, address);

        key.Should().HaveCount(32, "an empty key differs from anything and contains nothing: first, there is a key");
        key.Should().NotEqual(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), addressBytes),
            "the address is hashed under a label of this one use, so the key is no other use's hash of it");
        key.AsSpan().IndexOf(addressBytes).Should().Be(-1, "what is stored does not hold the address");
    }

    [Fact]
    public void TheKey_IsHmacSha256_OfTheLabelledAddress_UnderTheSecret()
    {
        /*
          A KNOWN ANSWER, worked out outside .NET and pasted here. Two commands gave the same value:

            python -c "import hmac, hashlib; print(hmac.new(b'unit-tests-only-demo-client-key-secret-0001', b'demo-claim:203.0.113.7', hashlib.sha256).hexdigest())"
            printf 'demo-claim:203.0.113.7' | openssl dgst -sha256 -hmac 'unit-tests-only-demo-client-key-secret-0001'

          Never recomputed here with HMACSHA256: a test that works out its own expectation agrees
          with whatever the code does. This is what holds the label, the encoding and the hash. A
          change to any of them gives every client a new key, and the rows a client was found by
          are found by nobody.
        */
        var key = DemoClientKey.Of(Secret, Address);

        Convert.ToHexStringLower(key).Should().Be("c26c7fb014dd27aaa16483844731fe12182c3f413780e1e1dd000713624301d3");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("    ")]
    public void WithNoSecret_NoKeyIsMade(string? secret)
    {
        // HMAC accepts an empty key, and what it gives then is a hash anybody can compute: trying
        // every address would turn a stored key back into the address it was made from.
        var make = () => DemoClientKey.Of(secret!, Address);

        make.Should().Throw<ArgumentException>().WithParameterName("secret");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void WithNoAddress_NoKeyIsMade(string? address)
    {
        // Left alone, the label would be hashed by itself, and every client with no address would
        // be given one and the same key.
        var make = () => DemoClientKey.Of(Secret, address!);

        make.Should().Throw<ArgumentException>().WithParameterName("clientAddress");
    }
}
