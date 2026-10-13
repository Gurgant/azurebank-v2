using System.Buffers.Binary;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Services.Implementations;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace AzureBank.Tests.Unit.Services;

/// <summary>
/// The hasher of account passwords (ADR-0003): the count a new hash is made at, that a hash made at
/// a lower count still verifies and is reported as due for a new one, and that every verification
/// spends the same rounds whatever the stored hash (ADR-0012).
/// </summary>
public class AccountPasswordHasherTests
{
    private const string Password = "SecurePass123!";
    private const string WrongPassword = "NotThePassword1!";

    private static readonly ApplicationUser NoUser =
        new() { AzureTag = string.Empty, FirstName = string.Empty, LastName = string.Empty };

    private static string HashedAt(int iterations) =>
        new PasswordHasher<ApplicationUser>(Options.Create(new PasswordHasherOptions { IterationCount = iterations }))
            .HashPassword(NoUser, Password);

    [Fact]
    public void TheCount_Is220000()
    {
        // The literal, on purpose: the constant compared with itself would hold at any value.
        AccountPasswordHasher.IterationCount.Should().Be(220_000);
    }

    [Fact]
    public void ANewHash_IsIdentityV3_WithHmacSha512_At220000Rounds()
    {
        var hash = new AccountPasswordHasher().HashPassword(NoUser, Password);

        var bytes = Convert.FromBase64String(hash);
        using var all = new AssertionScope();
        bytes[0].Should().Be(0x01, "the format marker of Identity V3");
        BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(1, 4)).Should().Be(2u, "KeyDerivationPrf.HMACSHA512");
        BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(5, 4)).Should().Be(220_000u);
        BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(9, 4)).Should().Be(16u, "the salt's length in bytes");
        hash.Should().StartWith("AQAAAAIAA1tg", "what that header reads as in a stored hash");
        AccountPasswordHasher.IterationsOf(hash).Should().Be(220_000);
    }

    [Fact]
    public void AHashMadeAt100000_StillVerifies_AndIsReportedAsDueForANewOne()
    {
        var sut = new AccountPasswordHasher();
        var old = HashedAt(100_000);

        using var all = new AssertionScope();
        old.Should().StartWith("AQAAAAIAAYag", "ARRANGE: the header of a hash at 100,000 rounds");
        sut.VerifyHashedPassword(NoUser, old, Password).Should().Be(PasswordVerificationResult.SuccessRehashNeeded);
        sut.VerifyHashedPassword(NoUser, old, WrongPassword).Should().Be(PasswordVerificationResult.Failed);
    }

    [Fact]
    public void AHashMadeAtTheCount_Verifies_AndIsNotDueForANewOne()
    {
        var sut = new AccountPasswordHasher();
        var hash = sut.HashPassword(NoUser, Password);

        using var all = new AssertionScope();
        sut.VerifyHashedPassword(NoUser, hash, Password).Should().Be(PasswordVerificationResult.Success);
        sut.VerifyHashedPassword(NoUser, hash, WrongPassword).Should().Be(PasswordVerificationResult.Failed);
    }

    [Theory]
    [InlineData(100_000, Password)]
    [InlineData(100_000, WrongPassword)]
    [InlineData(220_000, Password)]
    [InlineData(220_000, WrongPassword)]
    [InlineData(10_000, WrongPassword)]
    public void AVerification_Spends220000Rounds_WhateverCountTheStoredHashWasMadeAt(int storedAt, string provided)
    {
        var sut = new RoundCountingPasswordHasher();

        sut.VerifyHashedPassword(NoUser, HashedAt(storedAt), provided);

        var (stored, onTop) = sut.Verifications.Should().ContainSingle().Subject;
        using var all = new AssertionScope();
        stored.Should().Be(storedAt, "Identity verifies a hash at the count the hash carries");
        (stored + onTop).Should().Be(220_000, "a hash below the count is topped up to it, and one at it is not");
    }

    /// <summary>
    /// Base64 that is not an Identity V3 hash: nothing, a V3 header cut off before its salt, and
    /// the size and the marker of a V2 hash.
    /// </summary>
    public static TheoryData<string> NotIdentityV3 =>
        ["", "AQAAAAIAAYag", Convert.ToBase64String(new byte[49])];

    [Theory]
    [MemberData(nameof(NotIdentityV3))]
    public void AVerification_OfAHashItCannotRead_Spends220000Rounds(string stored)
    {
        var sut = new RoundCountingPasswordHasher();

        var result = sut.VerifyHashedPassword(NoUser, stored, WrongPassword);

        using var all = new AssertionScope();
        result.Should().Be(PasswordVerificationResult.Failed);
        sut.Verifications.Should().Equal((0, 220_000));
    }

    [Theory]
    [MemberData(nameof(NotIdentityV3))]
    public void IterationsOf_IsZero_ForWhatIsNotAnIdentityV3Hash(string stored)
    {
        AccountPasswordHasher.IterationsOf(stored).Should().Be(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-hash")]
    public void IterationsOf_IsZero_ForNoHash_AndForWhatIsNotBase64(string? stored)
    {
        AccountPasswordHasher.IterationsOf(stored).Should().Be(0);
    }

    [Theory]
    [InlineData(100_000)]
    [InlineData(220_000)]
    [InlineData(600_000)]
    public void IterationsOf_ReadsTheCountAHashCarries(int storedAt)
    {
        AccountPasswordHasher.IterationsOf(HashedAt(storedAt)).Should().Be(storedAt);
    }
}
