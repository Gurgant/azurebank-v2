using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using AzureBank.Api.Services.Implementations;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Options;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Moq;

namespace AzureBank.Tests.Unit.Services;

/// <summary>
/// Unit tests for JwtService.
/// Tests JWT token generation and validation using HMAC-SHA256.
/// </summary>
public class JwtServiceTests
{
    private readonly JwtService _sut;
    private readonly JwtOptions _options;
    private readonly Mock<ILogger<JwtService>> _loggerMock;

    public JwtServiceTests()
    {
        _options = new JwtOptions
        {
            Secret = "ThisIsAVeryLongSecretKeyForTestingPurposesAtLeast32Chars!",
            Issuer = "AzureBank.Tests",
            Audience = "AzureBank.Api.Tests",
            ExpirationMinutes = 15
        };

        _loggerMock = new Mock<ILogger<JwtService>>();
        _sut = new JwtService(Options.Create(_options), _loggerMock.Object);
    }

    #region Helper Methods

    private static ApplicationUser CreateTestUser(Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Email = "test@example.com",
        AzureTag = "test.user",
        FirstName = "Test",
        LastName = "User"
    };

    #endregion

    #region GenerateToken Tests

    [Fact]
    public void GenerateToken_WithValidUser_ReturnsNonEmptyToken()
    {
        // Arrange
        var user = CreateTestUser();

        // Act
        var token = _sut.GenerateToken(user).AccessToken;

        // Assert
        token.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void GenerateToken_ReturnsValidJwtFormat()
    {
        // Arrange
        var user = CreateTestUser();

        // Act
        var token = _sut.GenerateToken(user).AccessToken;
        var parts = token.Split('.');

        // Assert - JWT has 3 parts: header.payload.signature
        parts.Should().HaveCount(3);
        parts[0].Should().NotBeNullOrEmpty(); // Header
        parts[1].Should().NotBeNullOrEmpty(); // Payload
        parts[2].Should().NotBeNullOrEmpty(); // Signature
    }

    [Fact]
    public void GenerateToken_ContainsSubjectClaim()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var user = CreateTestUser(userId);

        // Act
        var token = _sut.GenerateToken(user).AccessToken;
        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(token);

        // Assert
        var subClaim = jwt.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Sub);
        subClaim.Should().NotBeNull();
        subClaim!.Value.Should().Be(userId.ToString());
    }

    [Fact]
    public void GenerateToken_ContainsEmailClaim()
    {
        // Arrange
        var user = CreateTestUser();
        user.Email = "custom@email.com";

        // Act
        var token = _sut.GenerateToken(user).AccessToken;
        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(token);

        // Assert
        var emailClaim = jwt.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Email);
        emailClaim.Should().NotBeNull();
        emailClaim!.Value.Should().Be("custom@email.com");
    }

    [Fact]
    public void GenerateToken_ContainsAzureTagClaim()
    {
        // Arrange
        var user = CreateTestUser();
        user.AzureTag = "custom.azure.tag";

        // Act
        var token = _sut.GenerateToken(user).AccessToken;
        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(token);

        // Assert
        var azureTagClaim = jwt.Claims.FirstOrDefault(c => c.Type == "azure_tag");
        azureTagClaim.Should().NotBeNull();
        azureTagClaim!.Value.Should().Be("custom.azure.tag");
    }

    [Fact]
    public void GenerateToken_ContainsJtiClaim()
    {
        // Arrange
        var user = CreateTestUser();

        // Act
        var token = _sut.GenerateToken(user).AccessToken;
        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(token);

        // Assert - JTI is unique token identifier
        var jtiClaim = jwt.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Jti);
        jtiClaim.Should().NotBeNull();
        Guid.TryParse(jtiClaim!.Value, out _).Should().BeTrue();
    }

    [Fact]
    public void GenerateToken_SetsCorrectIssuer()
    {
        // Arrange
        var user = CreateTestUser();

        // Act
        var token = _sut.GenerateToken(user).AccessToken;
        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(token);

        // Assert
        jwt.Issuer.Should().Be(_options.Issuer);
    }

    [Fact]
    public void GenerateToken_SetsCorrectAudience()
    {
        // Arrange
        var user = CreateTestUser();

        // Act
        var token = _sut.GenerateToken(user).AccessToken;
        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(token);

        // Assert
        jwt.Audiences.Should().Contain(_options.Audience);
    }

    [Fact]
    public void GenerateToken_SetsCorrectExpiration()
    {
        // Arrange
        var user = CreateTestUser();
        var beforeGeneration = DateTime.UtcNow;

        // Act
        var token = _sut.GenerateToken(user).AccessToken;
        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(token);

        // Assert - Token expires in ExpirationMinutes from now
        var expectedExpiration = beforeGeneration.AddMinutes(_options.ExpirationMinutes);
        jwt.ValidTo.Should().BeCloseTo(expectedExpiration, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void GenerateToken_ReturnedExpiresAt_EqualsTokenExpClaim()
    {
        // The returned ExpiresAt must be the token's real exp (single source of truth,
        // ADR-0012) — so a client's advertised expiry can never drift from the token.
        var user = CreateTestUser();

        var result = _sut.GenerateToken(user);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(result.AccessToken);

        result.ExpiresAt.Should().Be(jwt.ValidTo);
    }

    [Fact]
    public void GenerateToken_DifferentUsersGetDifferentTokens()
    {
        // Arrange
        var user1 = CreateTestUser(Guid.NewGuid());
        var user2 = CreateTestUser(Guid.NewGuid());

        // Act
        var token1 = _sut.GenerateToken(user1).AccessToken;
        var token2 = _sut.GenerateToken(user2).AccessToken;

        // Assert
        token1.Should().NotBe(token2);
    }

    [Fact]
    public void GenerateToken_SameUserGetsDifferentTokens()
    {
        // Arrange - Same user, called twice (different JTI)
        var user = CreateTestUser();

        // Act
        var token1 = _sut.GenerateToken(user).AccessToken;
        var token2 = _sut.GenerateToken(user).AccessToken;

        // Assert - Different JTI means different tokens
        token1.Should().NotBe(token2);
    }

    [Fact]
    public void GenerateToken_WithNullEmail_HandlesGracefully()
    {
        // Arrange
        var user = CreateTestUser();
        user.Email = null;

        // Act
        var token = _sut.GenerateToken(user).AccessToken;
        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(token);

        // Assert - Email claim should be empty string
        var emailClaim = jwt.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Email);
        emailClaim.Should().NotBeNull();
        emailClaim!.Value.Should().Be(string.Empty);
    }

    #endregion

    #region ValidateToken Tests

    [Fact]
    public void ValidateToken_WithValidToken_ReturnsTrueAndUserId()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var user = CreateTestUser(userId);
        var token = _sut.GenerateToken(user).AccessToken;

        // Act
        var (isValid, extractedUserId) = _sut.ValidateToken(token);

        // Assert
        isValid.Should().BeTrue();
        extractedUserId.Should().Be(userId);
    }

    [Fact]
    public void ValidateToken_WithEmptyToken_ReturnsFalse()
    {
        // Act
        var (isValid, userId) = _sut.ValidateToken(string.Empty);

        // Assert
        isValid.Should().BeFalse();
        userId.Should().Be(Guid.Empty);
    }

    [Fact]
    public void ValidateToken_WithNullToken_ReturnsFalse()
    {
        // Act
        var (isValid, userId) = _sut.ValidateToken(null!);

        // Assert
        isValid.Should().BeFalse();
        userId.Should().Be(Guid.Empty);
    }

    [Fact]
    public void ValidateToken_WithTamperedToken_ReturnsFalse()
    {
        // Arrange
        var user = CreateTestUser();
        var token = _sut.GenerateToken(user).AccessToken;
        var tamperedToken = token[..^10] + "tampered!!"; // Modify signature

        // Act
        var (isValid, userId) = _sut.ValidateToken(tamperedToken);

        // Assert
        isValid.Should().BeFalse();
        userId.Should().Be(Guid.Empty);
    }

    [Fact]
    public void ValidateToken_WithMalformedToken_ReturnsFalse()
    {
        // Arrange
        var malformedToken = "not.a.valid.jwt.token";

        // Act
        var (isValid, userId) = _sut.ValidateToken(malformedToken);

        // Assert
        isValid.Should().BeFalse();
        userId.Should().Be(Guid.Empty);
    }

    [Fact]
    public void ValidateToken_WithExpiredToken_ReturnsFalse()
    {
        // Arrange - Create a token that expires immediately
        var expiredOptions = new JwtOptions
        {
            Secret = _options.Secret,
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            ExpirationMinutes = -1 // Already expired
        };

        var expiredService = new JwtService(
            Options.Create(expiredOptions),
            _loggerMock.Object);

        var user = CreateTestUser();
        var expiredToken = expiredService.GenerateToken(user).AccessToken;

        // Act
        var (isValid, userId) = _sut.ValidateToken(expiredToken);

        // Assert
        isValid.Should().BeFalse();
        userId.Should().Be(Guid.Empty);
    }

    [Fact]
    public void ValidateToken_WithWrongIssuer_ReturnsFalse()
    {
        // Arrange - Create token with different issuer
        var wrongIssuerOptions = new JwtOptions
        {
            Secret = _options.Secret,
            Issuer = "WrongIssuer",
            Audience = _options.Audience,
            ExpirationMinutes = 15
        };

        var wrongIssuerService = new JwtService(
            Options.Create(wrongIssuerOptions),
            _loggerMock.Object);

        var user = CreateTestUser();
        var token = wrongIssuerService.GenerateToken(user).AccessToken;

        // Act - Validate with original service (expects correct issuer)
        var (isValid, userId) = _sut.ValidateToken(token);

        // Assert
        isValid.Should().BeFalse();
        userId.Should().Be(Guid.Empty);
    }

    [Fact]
    public void ValidateToken_WithWrongAudience_ReturnsFalse()
    {
        // Arrange - Create token with different audience
        var wrongAudienceOptions = new JwtOptions
        {
            Secret = _options.Secret,
            Issuer = _options.Issuer,
            Audience = "WrongAudience",
            ExpirationMinutes = 15
        };

        var wrongAudienceService = new JwtService(
            Options.Create(wrongAudienceOptions),
            _loggerMock.Object);

        var user = CreateTestUser();
        var token = wrongAudienceService.GenerateToken(user).AccessToken;

        // Act - Validate with original service (expects correct audience)
        var (isValid, userId) = _sut.ValidateToken(token);

        // Assert
        isValid.Should().BeFalse();
        userId.Should().Be(Guid.Empty);
    }

    [Fact]
    public void ValidateToken_WithWrongSigningKey_ReturnsFalse()
    {
        // Arrange - Create token with different secret
        var wrongSecretOptions = new JwtOptions
        {
            Secret = "DifferentSecretKeyThatIsAlsoAtLeast32Characters!!",
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            ExpirationMinutes = 15
        };

        var wrongSecretService = new JwtService(
            Options.Create(wrongSecretOptions),
            _loggerMock.Object);

        var user = CreateTestUser();
        var token = wrongSecretService.GenerateToken(user).AccessToken;

        // Act - Validate with original service (expects correct secret)
        var (isValid, userId) = _sut.ValidateToken(token);

        // Assert
        isValid.Should().BeFalse();
        userId.Should().Be(Guid.Empty);
    }

    #endregion

    #region Algorithm Tests

    [Fact]
    public void GenerateToken_UsesHmacSha256Algorithm()
    {
        // Arrange
        var user = CreateTestUser();

        // Act
        var token = _sut.GenerateToken(user).AccessToken;
        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(token);

        // Assert
        jwt.Header.Alg.Should().Be(SecurityAlgorithms.HmacSha256);
    }

    #endregion
}
