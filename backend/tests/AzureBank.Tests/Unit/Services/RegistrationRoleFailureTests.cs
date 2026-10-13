using AzureBank.Api.Mappers;
using AzureBank.Api.Security;
using AzureBank.Api.Services;
using AzureBank.Api.Services.Implementations;
using AzureBank.Api.Services.Interfaces;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Exceptions;
using AzureBank.Shared.Options;
using AzureBank.Shared.Services.Interfaces;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Compliance.Redaction;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace AzureBank.Tests.Unit.Services;

/// <summary>
/// A registration whose default role could not be assigned: what the caller is told, and that
/// nothing follows it.
/// </summary>
/// <remarks>
/// <para>
/// <c>AddToRoleAsync</c> reports what its validators refuse as a result and does not throw it
/// (ADR-0037). A result nobody reads is a registration that answers 201 for a user with no role.
/// A duplicate reported at that step is the lost race of ADR-0013 and has to be the same neutral
/// 409 as every other duplicate; anything else is a fault of the deployment and must not be told
/// to the caller as a defect of the request (422).
/// </para>
/// <para>
/// Mocks and InMemory, the idiom of <c>AuthServiceTests</c>, and with its limit: there is no
/// transaction here, so what is read is the answer and what was NOT asked for after it (no
/// account, no token, no grant). That the user's row is rolled back when the role cannot be written
/// is held on SQL Server by <c>RegistrationRoleFailureSqlServerTests</c>.
/// </para>
/// </remarks>
public sealed class RegistrationRoleFailureTests : IDisposable
{
    private readonly AzureBankDbContext _context;
    private readonly Mock<UserManager<ApplicationUser>> _userManager;
    private readonly Mock<IJwtService> _jwt = new();
    private readonly Mock<IRefreshTokenService> _grants = new();
    private readonly AuthService _sut;

    public RegistrationRoleFailureTests()
    {
        _context = new AzureBankDbContext(
            new DbContextOptionsBuilder<AzureBankDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .ReplaceService<IModelCustomizer, InMemoryTestModelCustomizer>()
                // RegisterAsync opens a transaction, which InMemory ignores with a warning it
                // raises as an exception unless told otherwise (AuthServiceTests).
                .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options);

        _userManager = new Mock<UserManager<ApplicationUser>>(
            new Mock<IUserStore<ApplicationUser>>().Object, null!, null!, null!, null!, null!, null!, null!, null!);

        _sut = new AuthService(
            _userManager.Object,
            _context,
            _jwt.Object,
            _grants.Object,
            new Mock<IPasswordHasher>().Object,
            new Mock<IPinVerifier>().Object,
            new UserMapper(),
            new AccountMapper(),
            new Mock<ILoginTimingEqualizer>().Object,
            new Mock<ILogger<AuthService>>().Object,
            new Mock<IRedactorProvider>().Object,
            new Mock<IAuditService>().Object,
            Options.Create(new DemoOptions()),
            TimeProvider.System);
    }

    public void Dispose() => _context.Dispose();

    private static RegisterRequest Request() => new()
    {
        Email = "new@example.com",
        Password = "SecurePass123!",
        AzureTag = "newcomer",
        FirstName = "New",
        LastName = "Comer",
    };

    private void TheUserIsCreated_AndTheRoleAnswers(IdentityResult role)
    {
        _userManager.Setup(x => x.FindByEmailAsync(It.IsAny<string>())).ReturnsAsync((ApplicationUser?)null);
        _userManager
            .Setup(x => x.CreateAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()))
            .ReturnsAsync(IdentityResult.Success);
        _userManager
            .Setup(x => x.AddToRoleAsync(It.IsAny<ApplicationUser>(), Roles.Default))
            .ReturnsAsync(role);
    }

    private async Task NothingFollowedTheRefusal()
    {
        (await _context.Accounts.CountAsync()).Should().Be(0, "the starter account comes after the role");
        _jwt.Verify(x => x.GenerateToken(It.IsAny<ApplicationUser>(), It.IsAny<DateTime?>()), Times.Never);
        _grants.Verify(x => x.IssueAsync(It.IsAny<ApplicationUser>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("DuplicateEmail")]
    [InlineData("DuplicateUserName")]
    public async Task ADuplicateReportedWhenTheRoleIsAssigned_IsTheNeutralConflict(string code)
    {
        TheUserIsCreated_AndTheRoleAnswers(
            IdentityResult.Failed(new IdentityError { Code = code, Description = "new@example.com is already taken." }));

        var register = () => _sut.RegisterAsync(Request());

        var refused = (await register.Should().ThrowAsync<ConflictException>()).Which;
        using var all = new AssertionScope();
        refused.ErrorCode.Should().Be(ErrorCodes.RegistrationFailed);
        refused.Message.Should().Be("Registration could not be completed.", "Identity's sentence names the email");
        await NothingFollowedTheRefusal();
    }

    [Fact]
    public async Task AnyOtherFailureToAssignTheRole_IsAFaultOfTheServer_AndNotOfTheRequest()
    {
        TheUserIsCreated_AndTheRoleAnswers(
            IdentityResult.Failed(new IdentityError { Code = "ConcurrencyFailure", Description = "Optimistic concurrency failure." }));

        var register = () => _sut.RegisterAsync(Request());

        // Not an AppException: the handler answers 500 for it, and the transaction is left
        // uncommitted. A BusinessRuleException here would be a 422 about the caller's payload.
        var failed = (await register.Should().ThrowAsync<InvalidOperationException>()).Which;
        using var all = new AssertionScope();
        failed.Should().NotBeAssignableTo<AppException>();
        failed.Message.Should().Be("Failed to assign default role (ConcurrencyFailure).");
        await NothingFollowedTheRefusal();
    }

    [Fact]
    public async Task WhenTheRoleIsAssigned_TheRegistrationGoesOn()
    {
        // CONTROL: the same arrangement with a role that succeeds reaches the account and the token.
        TheUserIsCreated_AndTheRoleAnswers(IdentityResult.Success);
        _jwt.Setup(x => x.GenerateToken(It.IsAny<ApplicationUser>(), null))
            .Returns(new TokenResult("test-jwt-token", DateTime.UtcNow.AddMinutes(15)));
        _grants.Setup(x => x.IssueAsync(It.IsAny<ApplicationUser>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssuedGrant("refresh-token-plaintext", DateTime.UtcNow.AddDays(1)));

        var result = await _sut.RegisterAsync(Request());

        using var all = new AssertionScope();
        result.Token.AccessToken.Should().Be("test-jwt-token");
        (await _context.Accounts.CountAsync()).Should().Be(1);
    }
}
