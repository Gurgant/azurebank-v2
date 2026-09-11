using AzureBank.Api.Services;
using AzureBank.Api.Services.Implementations;
using AzureBank.Api.Services.Interfaces;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Shared.Exceptions;
using AzureBank.Shared.Options;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace AzureBank.Tests.Unit.Services;

/// <summary>
/// The rail's own properties, on the real <see cref="StepUpAuthorizationService"/> over an InMemory
/// context — the shape <c>TransferServiceTests</c> already uses for its mint/validate/consume.
/// </summary>
/// <remarks>
/// <para>
/// Written for ADR-0049, when a NON-MONEY operation joined the rail: what has to hold is that the
/// operation name inside the HMAC keeps a closure and a transfer apart even when every other bound
/// field agrees, and that spending an authorisation for something that produces no ledger row
/// leaves <c>ConsumedByTransactionId</c> null rather than dressed as a movement that does not
/// exist. The wire-level versions of the first property live in
/// <c>AccountDeletionAuthorizationTests</c>; these pin it at the seam where the hash is computed.
/// </para>
/// </remarks>
public class StepUpAuthorizationServiceTests : IDisposable
{
    private const string TestPin = "123456";

    private readonly AzureBankDbContext _context;
    private readonly StepUpAuthorizationService _sut;
    private readonly Guid _userId = Guid.NewGuid();

    public StepUpAuthorizationServiceTests()
    {
        var options = new DbContextOptionsBuilder<AzureBankDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new AzureBankDbContext(options);

        // MintAsync looks the user up for the PIN-enrolled check; the PIN itself is verified by
        // the mocked IPinVerifier, so only the hash's PRESENCE matters here.
        _context.Users.Add(new ApplicationUser
        {
            Id = _userId,
            UserName = _userId.ToString(),
            NormalizedUserName = _userId.ToString().ToUpperInvariant(),
            Email = "stepup@test.com",
            NormalizedEmail = "STEPUP@TEST.COM",
            AzureTag = "stepup",
            FirstName = "Step",
            LastName = "Up",
            SecurityStamp = Guid.NewGuid().ToString(),
            ConcurrencyStamp = Guid.NewGuid().ToString(),
            CreatedAt = DateTime.UtcNow,
            PinHash = "not-a-real-hash-presence-is-what-is-checked"
        });
        _context.SaveChanges();

        var pinVerifier = new Mock<IPinVerifier>();
        pinVerifier.Setup(v => v.VerifyPinAsync(It.IsAny<Guid>(), It.IsAny<string>())).ReturnsAsync(true);

        _sut = new StepUpAuthorizationService(
            _context,
            pinVerifier.Object,
            new Mock<IAuditService>().Object,
            Options.Create(new StepUpOptions { BindingKey = new string('k', 32) }),
            new Mock<ILogger<StepUpAuthorizationService>>().Object);
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<StepUpAuthorization> ReadAsync(Guid authorizationId) =>
        await _context.StepUpAuthorizations.AsNoTracking().SingleAsync(a => a.Id == authorizationId);

    [Fact]
    public async Task ComputeBindingHash_ForAccountDeletion_DiffersFromATransferOnTheSameAccount()
    {
        /*
          The closure's binding is (account, null, null, 0) under the v1 payload — no field was
          added. What separates it from a transfer minted from the same account is the operation
          name inside the HMAC, and this is the test that would go red if that name ever left the
          payload: the two rows would then carry the same BindingHash.
        */
        var accountId = Guid.NewGuid();

        var closure = await _sut.MintAsync(
            _userId, StepUpOperation.AccountDeletion, StepUpBinding.ForAccountDeletion(accountId), TestPin);
        var transfer = await _sut.MintAsync(
            _userId, StepUpOperation.Transfer, new StepUpBinding(accountId, null, null, 0m), TestPin);

        closure.Operation.Should().Be(StepUpOperation.AccountDeletion);
        closure.BindingHash.Should().NotBe(
            transfer.BindingHash,
            "the same four bound fields under a different operation must not hash alike — the "
            + "operation name is the only thing that keeps a closure and a transfer apart");
    }

    [Fact]
    public async Task ValidateAsync_RefusesATransferAuthorisationPresentedAsADeletion()
    {
        // A real transfer authorisation from the account about to be closed — the closest thing to
        // a closure authorisation an attacker holding the user's transfer flow could obtain.
        var accountId = Guid.NewGuid();
        var transfer = await _sut.MintAsync(
            _userId, StepUpOperation.Transfer,
            new StepUpBinding(accountId, null, Guid.NewGuid(), 25m), TestPin);

        var act = () => _sut.ValidateAsync(
            _userId, transfer.Id, StepUpOperation.AccountDeletion, StepUpBinding.ForAccountDeletion(accountId));

        // The UNIFORM refusal, indistinguishable from an unknown reference: nothing on the wire
        // says "that was a transfer authorisation".
        (await act.Should().ThrowAsync<AuthenticationException>())
            .Which.ErrorCode.Should().Be(ErrorCodes.AuthorizationInvalid);
        (await ReadAsync(transfer.Id)).Status.Should().Be(
            StepUpAuthorizationStatus.Pending, "validation is read-only; a mismatch spends nothing");
    }

    [Fact]
    public async Task ValidateAsync_AcceptsTheClosureAuthorisation_ForTheAccountItWasMintedFor()
    {
        var accountId = Guid.NewGuid();
        var closure = await _sut.MintAsync(
            _userId, StepUpOperation.AccountDeletion, StepUpBinding.ForAccountDeletion(accountId), TestPin);

        var sameAccount = () => _sut.ValidateAsync(
            _userId, closure.Id, StepUpOperation.AccountDeletion, StepUpBinding.ForAccountDeletion(accountId));
        var otherAccount = () => _sut.ValidateAsync(
            _userId, closure.Id, StepUpOperation.AccountDeletion, StepUpBinding.ForAccountDeletion(Guid.NewGuid()));

        await sameAccount.Should().NotThrowAsync("the positive control: the factory reproduces the mint's binding");
        (await otherAccount.Should().ThrowAsync<AuthenticationException>())
            .Which.ErrorCode.Should().Be(ErrorCodes.AuthorizationInvalid);
    }

    [Fact]
    public async Task ConsumeAsync_WithNullConsumedBy_MarksConsumedAndLeavesTheColumnNull()
    {
        /*
          A closure produces no ledger row, so there is nothing for ConsumedByTransactionId to name
          (ADR-0049). Null is the honest value: the evidence verb joins that column on the movement
          id, and an account id written there would be a value that can never match, dressed as one
          that could. The transfer path still passes the outgoing transaction id — the column did
          not change, only what a caller is allowed to say about it.
        */
        var closure = await _sut.MintAsync(
            _userId, StepUpOperation.AccountDeletion, StepUpBinding.ForAccountDeletion(Guid.NewGuid()), TestPin);

        await _sut.ConsumeAsync(_userId, closure.Id, consumedByTransactionId: null);

        var spent = await ReadAsync(closure.Id);
        spent.Status.Should().Be(StepUpAuthorizationStatus.Consumed);
        spent.ConsumedAt.Should().NotBeNull("spent is spent, whether or not money moved");
        spent.ConsumedByTransactionId.Should().BeNull("there is no ledger row to name");

        // And spent means spent: the single-use guarantee does not depend on a consumed-by value.
        var again = () => _sut.ConsumeAsync(_userId, closure.Id, consumedByTransactionId: null);
        (await again.Should().ThrowAsync<AuthenticationException>())
            .Which.ErrorCode.Should().Be(ErrorCodes.AuthorizationInvalid);
    }
}
