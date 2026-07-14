using AzureBank.Api.Services.Implementations;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Exceptions;
using AzureBank.Shared.Services.Interfaces;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace AzureBank.Tests.Unit.Services;

/// <summary>
/// Unit tests for PinService: PIN verification + attempt-limiting (lockout).
/// Uses a real InMemory DbContext handed to the service through a mocked
/// IServiceScopeFactory (PinService persists lockout state in its own scope).
/// </summary>
public class PinServiceTests : IDisposable
{
    private readonly AzureBankDbContext _context;
    private readonly Mock<IPasswordHasher> _hasherMock;
    private readonly PinService _sut;

    public PinServiceTests()
    {
        var options = new DbContextOptionsBuilder<AzureBankDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new AzureBankDbContext(options);
        _hasherMock = new Mock<IPasswordHasher>();

        // Hand PinService the same InMemory context via its scope factory.
        var provider = new Mock<IServiceProvider>();
        provider.Setup(p => p.GetService(typeof(AzureBankDbContext))).Returns(_context);
        var scope = new Mock<IServiceScope>();
        scope.Setup(s => s.ServiceProvider).Returns(provider.Object);
        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        _sut = new PinService(scopeFactory.Object, _hasherMock.Object, Mock.Of<ILogger<PinService>>());
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    private ApplicationUser SeedUser(
        string? pinHash = "hashed-pin", int failed = 0, DateTimeOffset? lockoutEnd = null)
    {
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            Email = "pin@example.com",
            AzureTag = "pin_user",
            FirstName = "Pin",
            LastName = "User",
            PinHash = pinHash,
            PinAccessFailedCount = failed,
            PinLockoutEnd = lockoutEnd,
        };
        _context.Users.Add(user);
        _context.SaveChanges();
        return user;
    }

    [Fact]
    public async Task VerifyPinAsync_ValidPin_ReturnsTrue()
    {
        var user = SeedUser();
        _hasherMock.Setup(x => x.VerifyPin("hashed-pin", "123456")).Returns(true);

        (await _sut.VerifyPinAsync(user.Id, "123456")).Should().BeTrue();
    }

    [Fact]
    public async Task VerifyPinAsync_UserNotFound_ReturnsFalse()
    {
        (await _sut.VerifyPinAsync(Guid.NewGuid(), "123456")).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task VerifyPinAsync_NoPinSet_ReturnsFalse(string? pinHash)
    {
        var user = SeedUser(pinHash: pinHash);

        (await _sut.VerifyPinAsync(user.Id, "123456")).Should().BeFalse();
    }

    [Fact]
    public async Task VerifyPinAsync_WrongPin_IncrementsFailureCount()
    {
        var user = SeedUser();
        _hasherMock.Setup(x => x.VerifyPin("hashed-pin", "000000")).Returns(false);

        var result = await _sut.VerifyPinAsync(user.Id, "000000");

        result.Should().BeFalse();
        user.PinAccessFailedCount.Should().Be(1);
        user.PinLockoutEnd.Should().BeNull();
    }

    [Fact]
    public async Task VerifyPinAsync_AttemptCrossingThreshold_LocksAndThrows()
    {
        var user = SeedUser(failed: ValidationRules.MaxPinAttempts - 1); // one away from the lock
        _hasherMock.Setup(x => x.VerifyPin("hashed-pin", "000000")).Returns(false);

        var act = () => _sut.VerifyPinAsync(user.Id, "000000");

        var ex = (await act.Should().ThrowAsync<PinLockedException>()).Which;
        ex.StatusCode.Should().Be(423);
        ex.ErrorCode.Should().Be(ErrorCodes.PinLocked);
        user.PinLockoutEnd.Should().NotBeNull();
        user.PinLockoutEnd!.Value.Should().BeAfter(DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task VerifyPinAsync_WhenLocked_ThrowsWithoutHashing()
    {
        var user = SeedUser(lockoutEnd: DateTimeOffset.UtcNow.AddMinutes(5)); // currently locked

        var act = () => _sut.VerifyPinAsync(user.Id, "123456");

        await act.Should().ThrowAsync<PinLockedException>();
        _hasherMock.Verify(x => x.VerifyPin(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task VerifyPinAsync_CorrectPin_ResetsFailureCount()
    {
        var user = SeedUser(failed: 2);
        _hasherMock.Setup(x => x.VerifyPin("hashed-pin", "123456")).Returns(true);

        var result = await _sut.VerifyPinAsync(user.Id, "123456");

        result.Should().BeTrue();
        user.PinAccessFailedCount.Should().Be(0);
        user.PinLockoutEnd.Should().BeNull();
    }

    [Fact]
    public async Task VerifyPinAsync_ExpiredLock_AllowsFreshAttempt()
    {
        var user = SeedUser(lockoutEnd: DateTimeOffset.UtcNow.AddMinutes(-1)); // already expired
        _hasherMock.Setup(x => x.VerifyPin("hashed-pin", "123456")).Returns(true);

        var result = await _sut.VerifyPinAsync(user.Id, "123456");

        result.Should().BeTrue();
        user.PinLockoutEnd.Should().BeNull();
    }

    [Fact]
    public async Task VerifyPinAsync_WrongPinAfterExpiredLock_StartsFreshWindow()
    {
        var user = SeedUser(lockoutEnd: DateTimeOffset.UtcNow.AddMinutes(-1));
        _hasherMock.Setup(x => x.VerifyPin("hashed-pin", "000000")).Returns(false);

        var result = await _sut.VerifyPinAsync(user.Id, "000000");

        result.Should().BeFalse();
        user.PinAccessFailedCount.Should().Be(1, "an expired lock restarts the failure window at 1");
        user.PinLockoutEnd.Should().BeNull();
    }
}
