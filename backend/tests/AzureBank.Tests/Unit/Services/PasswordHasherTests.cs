using AzureBank.Shared.Services.Implementations;
using FluentAssertions;

namespace AzureBank.Tests.Unit.Services;

/// <summary>
/// Unit tests for PasswordHasher service: Argon2id PIN hashing (19MB profile).
/// Until 2026-09-17 this said "dual profiles (Password: 64MB, PIN: 19MB)"; the password profile
/// was deleted as uncalled outside these tests.
/// </summary>
public class PasswordHasherTests
{
    private readonly PasswordHasher _sut = new();

    #region PIN Hashing Tests

    [Fact]
    public void HashPin_WithValidPin_ReturnsNonEmptyHash()
    {
        // Arrange
        var pin = "123456";

        // Act
        var hash = _sut.HashPin(pin);

        // Assert
        hash.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void HashPin_ReturnsArgon2idFormatWithPinProfile()
    {
        // Arrange
        var pin = "654321";

        // Act
        var hash = _sut.HashPin(pin);

        // Assert
        hash.Should().StartWith("$argon2id$v=19$");
        hash.Should().Contain("m=19456"); // 19 MB for PINs (OWASP Tier 2)
        hash.Should().Contain("t=2");     // 2 iterations
        hash.Should().Contain("p=4");     // 4 parallelism
    }

    [Fact]
    public void HashPin_WithSamePin_ReturnsDifferentHashes()
    {
        // Arrange
        var pin = "123456";

        // Act
        var hash1 = _sut.HashPin(pin);
        var hash2 = _sut.HashPin(pin);

        // Assert - Different salts should produce different hashes
        hash1.Should().NotBe(hash2);
    }

    [Fact]
    public void HashPin_WithNullPin_ThrowsArgumentException()
    {
        // Act
        var act = () => _sut.HashPin(null!);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void HashPin_WithEmptyPin_ThrowsArgumentException()
    {
        // Act
        var act = () => _sut.HashPin(string.Empty);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    #endregion

    #region PIN Verification Tests

    [Fact]
    public void VerifyPin_WithCorrectPin_ReturnsTrue()
    {
        // Arrange
        var pin = "123456";
        var hash = _sut.HashPin(pin);

        // Act
        var result = _sut.VerifyPin(hash, pin);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void VerifyPin_WithIncorrectPin_ReturnsFalse()
    {
        // Arrange
        var pin = "123456";
        var hash = _sut.HashPin(pin);

        // Act
        var result = _sut.VerifyPin(hash, "654321");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void VerifyPin_WithMalformedHash_ReturnsFalse()
    {
        // Arrange
        var malformedHash = "invalid-hash-format";

        // Act
        var result = _sut.VerifyPin(malformedHash, "123456");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void VerifyPin_WithEmptyHash_ReturnsFalse()
    {
        // Act
        var result = _sut.VerifyPin(string.Empty, "123456");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void VerifyPin_WithNullHash_ReturnsFalse()
    {
        // Act
        var result = _sut.VerifyPin(null!, "123456");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void VerifyPin_WithEmptyPin_ReturnsFalse()
    {
        // Arrange
        var hash = _sut.HashPin("123456");

        // Act
        var result = _sut.VerifyPin(hash, string.Empty);

        // Assert
        result.Should().BeFalse();
    }

    #endregion

    #region Hash Format Validation Tests

    [Fact]
    public void HashPin_ContainsSixParts()
    {
        // Arrange
        var pin = "123456";

        // Act
        var hash = _sut.HashPin(pin);
        var parts = hash.Split('$');

        // Assert - Format: $argon2id$v=19$m=X,t=Y,p=Z$salt$hash
        parts.Should().HaveCount(6);
        parts[0].Should().BeEmpty(); // Before first $
        parts[1].Should().Be("argon2id");
        parts[2].Should().Be("v=19");
        parts[3].Should().Contain("m=").And.Contain("t=").And.Contain("p=");
        parts[4].Should().NotBeNullOrEmpty(); // Salt (Base64)
        parts[5].Should().NotBeNullOrEmpty(); // Hash (Base64)
    }

    [Fact]
    public void VerifyPin_WithWrongAlgorithmIdentifier_ReturnsFalse()
    {
        // Arrange - Create a hash with wrong algorithm identifier
        var fakeHash = "$argon2i$v=19$m=19456,t=2,p=4$fakesalt$fakehash";

        // Act
        var result = _sut.VerifyPin(fakeHash, "123456");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void VerifyPin_WithInvalidBase64Salt_ReturnsFalse()
    {
        // Arrange - Invalid Base64 in salt position
        var invalidHash = "$argon2id$v=19$m=19456,t=2,p=4$!!!invalid!!!$fakehash";

        // Act
        var result = _sut.VerifyPin(invalidHash, "123456");

        // Assert
        result.Should().BeFalse();
    }

    #endregion
}
