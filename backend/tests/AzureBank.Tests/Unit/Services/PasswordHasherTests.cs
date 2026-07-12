using AzureBank.Shared.Services.Implementations;
using FluentAssertions;

namespace AzureBank.Tests.Unit.Services;

/// <summary>
/// Unit tests for PasswordHasher service.
/// Tests Argon2id hashing with dual profiles (Password: 64MB, PIN: 19MB).
/// </summary>
public class PasswordHasherTests
{
    private readonly PasswordHasher _sut = new();

    #region Password Hashing Tests

    [Fact]
    public void HashPassword_WithValidPassword_ReturnsNonEmptyHash()
    {
        // Arrange
        var password = "SecurePass123!";

        // Act
        var hash = _sut.HashPassword(password);

        // Assert
        hash.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void HashPassword_ReturnsArgon2idFormat()
    {
        // Arrange
        var password = "TestPassword123";

        // Act
        var hash = _sut.HashPassword(password);

        // Assert
        hash.Should().StartWith("$argon2id$v=19$");
        hash.Should().Contain("m=65536"); // 64 MB for passwords
        hash.Should().Contain("t=3");     // 3 iterations
        hash.Should().Contain("p=4");     // 4 parallelism
    }

    [Fact]
    public void HashPassword_WithSamePassword_ReturnsDifferentHashes()
    {
        // Arrange
        var password = "SamePassword123!";

        // Act
        var hash1 = _sut.HashPassword(password);
        var hash2 = _sut.HashPassword(password);

        // Assert - Different salts should produce different hashes
        hash1.Should().NotBe(hash2);
    }

    [Fact]
    public void HashPassword_WithNullPassword_ThrowsArgumentException()
    {
        // Act
        var act = () => _sut.HashPassword(null!);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void HashPassword_WithEmptyPassword_ThrowsArgumentException()
    {
        // Act
        var act = () => _sut.HashPassword(string.Empty);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    #endregion

    #region Password Verification Tests

    [Fact]
    public void VerifyPassword_WithCorrectPassword_ReturnsTrue()
    {
        // Arrange
        var password = "CorrectPassword123!";
        var hash = _sut.HashPassword(password);

        // Act
        var result = _sut.VerifyPassword(hash, password);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void VerifyPassword_WithIncorrectPassword_ReturnsFalse()
    {
        // Arrange
        var password = "OriginalPassword123!";
        var hash = _sut.HashPassword(password);

        // Act
        var result = _sut.VerifyPassword(hash, "WrongPassword456!");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void VerifyPassword_IsCaseSensitive()
    {
        // Arrange
        var password = "CaseSensitive123!";
        var hash = _sut.HashPassword(password);

        // Act
        var result = _sut.VerifyPassword(hash, "casesensitive123!");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void VerifyPassword_WithMalformedHash_ReturnsFalse()
    {
        // Arrange
        var malformedHash = "not-a-valid-hash";

        // Act
        var result = _sut.VerifyPassword(malformedHash, "AnyPassword123!");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void VerifyPassword_WithEmptyHash_ReturnsFalse()
    {
        // Act
        var result = _sut.VerifyPassword(string.Empty, "AnyPassword123!");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void VerifyPassword_WithNullHash_ReturnsFalse()
    {
        // Act
        var result = _sut.VerifyPassword(null!, "AnyPassword123!");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void VerifyPassword_WithEmptyPassword_ReturnsFalse()
    {
        // Arrange
        var hash = _sut.HashPassword("ValidPassword123!");

        // Act
        var result = _sut.VerifyPassword(hash, string.Empty);

        // Assert
        result.Should().BeFalse();
    }

    #endregion

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
    public void HashPin_UsesDifferentProfileThanPassword()
    {
        // Arrange
        var input = "123456";

        // Act
        var passwordHash = _sut.HashPassword(input);
        var pinHash = _sut.HashPin(input);

        // Assert - Password uses 64MB, PIN uses 19MB
        passwordHash.Should().Contain("m=65536");
        pinHash.Should().Contain("m=19456");
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

    #region Cross-Profile Tests

    [Fact]
    public void VerifyPassword_CanVerifyPinHash_WhenParametersMatch()
    {
        // Arrange - PIN hash has different parameters
        var pin = "123456";
        var pinHash = _sut.HashPin(pin);

        // Act - VerifyPassword reads parameters from hash, so it works
        var result = _sut.VerifyPassword(pinHash, pin);

        // Assert - Both methods use the same Verify() internally
        result.Should().BeTrue();
    }

    [Fact]
    public void VerifyPin_CanVerifyPasswordHash_WhenParametersMatch()
    {
        // Arrange - Password hash has different parameters
        var password = "TestPassword123!";
        var passwordHash = _sut.HashPassword(password);

        // Act - VerifyPin reads parameters from hash, so it works
        var result = _sut.VerifyPin(passwordHash, password);

        // Assert - Both methods use the same Verify() internally
        result.Should().BeTrue();
    }

    #endregion

    #region Hash Format Validation Tests

    [Fact]
    public void HashPassword_ContainsSixParts()
    {
        // Arrange
        var password = "TestPassword123!";

        // Act
        var hash = _sut.HashPassword(password);
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
    public void VerifyPassword_WithWrongAlgorithmIdentifier_ReturnsFalse()
    {
        // Arrange - Create a hash with wrong algorithm identifier
        var fakeHash = "$argon2i$v=19$m=65536,t=3,p=4$fakesalt$fakehash";

        // Act
        var result = _sut.VerifyPassword(fakeHash, "AnyPassword");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void VerifyPassword_WithInvalidBase64Salt_ReturnsFalse()
    {
        // Arrange - Invalid Base64 in salt position
        var invalidHash = "$argon2id$v=19$m=65536,t=3,p=4$!!!invalid!!!$fakehash";

        // Act
        var result = _sut.VerifyPassword(invalidHash, "AnyPassword");

        // Assert
        result.Should().BeFalse();
    }

    #endregion
}
