using AzureBank.Api.Services.Interfaces;
using AzureBank.Api.Services.Implementations;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Exceptions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace AzureBank.Tests.Unit.Services;

/// <summary>
/// Unit tests for UserService.
/// Tests user profile retrieval and recipient lookup operations.
/// </summary>
public class UserServiceTests : IDisposable
{
    private readonly AzureBankDbContext _context;
    private readonly Mock<ILogger<UserService>> _loggerMock;
    private readonly Mock<IAuditService> _auditMock;
    private readonly UserService _sut;

    public UserServiceTests()
    {
        var options = new DbContextOptionsBuilder<AzureBankDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _context = new AzureBankDbContext(options);
        _loggerMock = new Mock<ILogger<UserService>>();

        _auditMock = new Mock<IAuditService>();
        _sut = new UserService(_context, _loggerMock.Object, _auditMock.Object);
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    #region Helper Methods

    private ApplicationUser CreateTestUser(string azureTag, string firstName = "Test", string lastName = "User")
    {
        var id = Guid.NewGuid();
        return new ApplicationUser
        {
            Id = id,
            AzureTag = azureTag.ToLower(),
            // Decouple (ADR-0015): Identity's UserName is the immutable user id, not the handle.
            UserName = id.ToString(),
            NormalizedUserName = id.ToString().ToUpperInvariant(),
            Email = $"{azureTag}@test.com",
            NormalizedEmail = $"{azureTag.ToUpper()}@TEST.COM",
            FirstName = firstName,
            LastName = lastName,
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString(),
            ConcurrencyStamp = Guid.NewGuid().ToString(),
            CreatedAt = DateTime.UtcNow
        };
    }

    #endregion

    #region GetUserByIdAsync Tests

    [Fact]
    public async Task GetUserByIdAsync_ExistingUser_ReturnsUserResponse()
    {
        // Arrange
        var user = CreateTestUser("testuser", "John", "Doe");
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        // Act
        var result = await _sut.GetUserByIdAsync(user.Id);

        // Assert
        result.Should().NotBeNull();
        result.UserId.Should().Be(user.Id);
        result.Email.Should().Be(user.Email);
        result.AzureTag.Should().Be(user.AzureTag);
        result.FirstName.Should().Be("John");
        result.LastName.Should().Be("Doe");
    }

    [Fact]
    public async Task GetUserByIdAsync_NonExistentUser_ThrowsNotFoundException()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid();

        // Act
        var act = () => _sut.GetUserByIdAsync(nonExistentId);

        // Assert
        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task GetUserByIdAsync_MultipleUsers_ReturnsCorrectUser()
    {
        // Arrange
        var user1 = CreateTestUser("user1", "Alice", "Smith");
        var user2 = CreateTestUser("user2", "Bob", "Jones");
        _context.Users.AddRange(user1, user2);
        await _context.SaveChangesAsync();

        // Act
        var result = await _sut.GetUserByIdAsync(user2.Id);

        // Assert
        result.UserId.Should().Be(user2.Id);
        result.FirstName.Should().Be("Bob");
    }

    #endregion

    #region GetUserByAzureTagAsync Tests

    [Fact]
    public async Task GetUserByAzureTagAsync_ExistingUser_ReturnsLookupResponse()
    {
        // Arrange
        var currentUserId = Guid.NewGuid();
        var targetUser = CreateTestUser("targetuser", "Jane", "Doe");
        _context.Users.Add(targetUser);
        await _context.SaveChangesAsync();

        // Act
        var result = await _sut.GetUserByAzureTagAsync("targetuser", currentUserId);

        // Assert
        result.Should().NotBeNull();
        result.Exists.Should().BeTrue();
        result.AzureTag.Should().Be("targetuser");
        result.DisplayName.Should().Be("Jane D."); // Masked name
    }

    [Fact]
    public async Task GetUserByAzureTagAsync_NonExistentUser_ReturnsNotExists()
    {
        // Arrange
        var currentUserId = Guid.NewGuid();

        // Act
        var result = await _sut.GetUserByAzureTagAsync("nonexistent", currentUserId);

        // Assert
        result.Should().NotBeNull();
        result.Exists.Should().BeFalse();
        result.AzureTag.Should().Be("nonexistent");
        result.DisplayName.Should().BeEmpty();
    }

    [Fact]
    public async Task GetUserByAzureTagAsync_SelfLookup_ReturnsSelfIndicator()
    {
        // Arrange
        var user = CreateTestUser("selfuser", "Self", "User");
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        // Act
        var result = await _sut.GetUserByAzureTagAsync("selfuser", user.Id);

        // Assert - not a valid transfer recipient, and no name is echoed back (ADR-0014).
        result.Should().NotBeNull();
        result.Exists.Should().BeFalse();
        result.DisplayName.Should().BeEmpty();
    }

    [Fact]
    public async Task GetUserByAzureTagAsync_CaseInsensitiveLookup_ReturnsUser()
    {
        // Arrange
        var currentUserId = Guid.NewGuid();
        var user = CreateTestUser("lowercase", "Test", "User");
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        // Act
        var result = await _sut.GetUserByAzureTagAsync("LOWERCASE", currentUserId);

        // Assert
        result.Exists.Should().BeTrue();
    }

    [Fact]
    public async Task GetUserByAzureTagAsync_SubstringDoesNotMatch_ReturnsNotExists()
    {
        // Exact-match only: a substring of a real tag must NOT resolve (ADR-0014 — no
        // directory browsing). "smith" must not find "johnsmith".
        var user = CreateTestUser("johnsmith", "John", "Smith");
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var result = await _sut.GetUserByAzureTagAsync("smith", Guid.NewGuid());

        result.Exists.Should().BeFalse();
        result.DisplayName.Should().BeEmpty();
    }

    [Fact]
    public async Task GetUserByAzureTagAsync_NameWithEdgeSpaces_MasksCleanly()
    {
        // The name charset permits edge spaces; the masked display must still be "John S.",
        // not "John  ." (display normalisation — ADR-0014).
        var user = CreateTestUser("spacedtag", "John", " Smith");
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var result = await _sut.GetUserByAzureTagAsync("spacedtag", Guid.NewGuid());

        result.Exists.Should().BeTrue();
        result.DisplayName.Should().Be("John S.");
    }

    #endregion

    #region GetUserByAzureTagAsync - a handle is resolved only inside the caller's demo copy

    /*
      A demo copy is three users that can pay each other and nobody else. The lookup hands another
      user's name to the caller, so it must not see past the caller's copy, and a handle in another
      copy must be answered EXACTLY as a handle nobody holds: any difference would tell a visitor
      that some other copy uses it.

      Outside the demo every user's copy is null, and null matches null: the tests above this region
      pin that nothing changed for them.
    */

    private async Task<ApplicationUser> AddUserAsync(string azureTag, Guid? copy, string firstName = "Test", string lastName = "User")
    {
        var user = CreateTestUser(azureTag, firstName, lastName);
        user.DemoCopyId = copy;
        _context.Users.Add(user);
        await _context.SaveChangesAsync();
        return user;
    }

    [Fact]
    public async Task GetUserByAzureTagAsync_AHandleInAnotherCopy_IsAnsweredExactlyAsAnUnknownHandle()
    {
        var caller = await AddUserAsync("john_a1b2", copy: Guid.NewGuid());
        await AddUserAsync("jane_c3d4", copy: Guid.NewGuid(), "Jane", "Smith");

        var foreign = await _sut.GetUserByAzureTagAsync("jane_c3d4", caller.Id);
        var unknown = await _sut.GetUserByAzureTagAsync("jane_zzzz", caller.Id);

        foreign.Exists.Should().BeFalse("the handle belongs to another copy");
        foreign.DisplayName.Should().BeEmpty("no name crosses from one copy to another");
        foreign.Should().BeEquivalentTo(
            unknown,
            options => options.Excluding(answer => answer.AzureTag),
            "beyond the handle each answer echoes, nothing tells a foreign handle from an unknown one");
    }

    /// <summary>
    /// CONTROL: green before the copy is compared and after. It shows the refusals beside it are
    /// about the copy, not about the lookup having stopped working for users that have one.
    /// </summary>
    [Fact]
    public async Task GetUserByAzureTagAsync_AHandleInTheCallersOwnCopy_IsFound()
    {
        var copy = Guid.NewGuid();
        var caller = await AddUserAsync("john_a1b2", copy);
        await AddUserAsync("jane_a1b2", copy, "Jane", "Smith");

        var result = await _sut.GetUserByAzureTagAsync("jane_a1b2", caller.Id);

        result.Exists.Should().BeTrue();
        result.DisplayName.Should().Be("Jane S.");
    }

    [Fact]
    public async Task GetUserByAzureTagAsync_ACopysUser_CannotResolveAUserOutsideEveryCopy()
    {
        var caller = await AddUserAsync("john_a1b2", copy: Guid.NewGuid());
        await AddUserAsync("outsider", copy: null, "Olga", "Outside");

        var result = await _sut.GetUserByAzureTagAsync("outsider", caller.Id);

        result.Exists.Should().BeFalse("a copy pays nobody outside itself");
        result.DisplayName.Should().BeEmpty();
    }

    [Fact]
    public async Task GetUserByAzureTagAsync_AUserOutsideEveryCopy_CannotResolveACopysUser()
    {
        var caller = await AddUserAsync("outsider", copy: null);
        await AddUserAsync("jane_c3d4", copy: Guid.NewGuid(), "Jane", "Smith");

        var result = await _sut.GetUserByAzureTagAsync("jane_c3d4", caller.Id);

        result.Exists.Should().BeFalse("nobody outside a copy pays into it");
        result.DisplayName.Should().BeEmpty();
    }

    /// <summary>
    /// A copy's users are deleted when its time is over, and the API does not look the caller up
    /// when it accepts a token: until that token expires, a caller whose row is gone still reaches
    /// the lookup. It has no copy to read, and that must not wave it through into the others.
    /// </summary>
    [Fact]
    public async Task GetUserByAzureTagAsync_ACallerWhoseOwnRowIsGone_CannotResolveACopysUser()
    {
        await AddUserAsync("jane_c3d4", copy: Guid.NewGuid(), "Jane", "Smith");

        var foreign = await _sut.GetUserByAzureTagAsync("jane_c3d4", Guid.NewGuid());
        var unknown = await _sut.GetUserByAzureTagAsync("jane_zzzz", Guid.NewGuid());

        foreign.Exists.Should().BeFalse("a caller with no row belongs to no copy");
        foreign.DisplayName.Should().BeEmpty();
        foreign.Should().BeEquivalentTo(unknown, options => options.Excluding(answer => answer.AzureTag));
    }

    #endregion

    #region RenameAzureTagAsync Tests

    [Fact]
    public async Task RenameAzureTagAsync_FreshTag_UpdatesHandle()
    {
        var user = CreateTestUser("oldtag", "John", "Doe");
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var result = await _sut.RenameAzureTagAsync(user.Id, "newtag");

        result.Should().Be("newtag");
        (await _sut.GetUserByAzureTagAsync("newtag", Guid.NewGuid())).Exists.Should().BeTrue();
        (await _sut.GetUserByAzureTagAsync("oldtag", Guid.NewGuid())).Exists.Should().BeFalse();
    }

    [Fact]
    public async Task RenameAzureTagAsync_TagTakenByAnother_ThrowsConflict()
    {
        var me = CreateTestUser("metag", "Me", "User");
        var other = CreateTestUser("takentag", "Other", "User");
        _context.Users.AddRange(me, other);
        await _context.SaveChangesAsync();

        var thrown = await ((Func<Task>)(() => _sut.RenameAzureTagAsync(me.Id, "takentag")))
            .Should().ThrowAsync<ConflictException>();
        thrown.Which.ErrorCode.Should().Be(ErrorCodes.AzureTagTaken);
    }

    [Fact]
    public async Task RenameAzureTagAsync_OwnTagCaseInsensitive_IsNoOp()
    {
        var user = CreateTestUser("sametag", "Self", "User");
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var result = await _sut.RenameAzureTagAsync(user.Id, "SameTag");

        result.Should().Be("sametag");
    }

    #endregion
}
