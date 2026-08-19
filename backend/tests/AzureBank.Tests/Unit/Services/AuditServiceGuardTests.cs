using AzureBank.Api.Services.Implementations;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.Enums;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace AzureBank.Tests.Unit.Services;

/// <summary>
/// The two guards that keep the audit writer's halves from being swapped, in either direction
/// (ADR-0044 D1).
/// </summary>
/// <remarks>
/// <para>
/// <c>Record</c> enlists in the caller's transaction; <c>RecordRefusalAsync</c> deliberately escapes
/// it on its own connection. Which one a call site picks is not a style choice — pick wrong on a
/// refusal and the row is erased by the rollback it was meant to survive; pick wrong on a success
/// and a row asserting the action happened commits while the action itself may still be abandoned.
/// </para>
/// <para>
/// The second guard is here because it was MISSING: <c>Record</c> refused refusal outcomes from the
/// start, and nothing stopped a success being written out-of-band until the first review round
/// pointed at the asymmetry.
/// </para>
/// </remarks>
public class AuditServiceGuardTests
{
    private static AuditService NewService() => new(
        new AzureBankDbContext(
            new DbContextOptionsBuilder<AzureBankDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options),
        new Mock<IServiceScopeFactory>().Object,
        new Mock<ILogger<AuditService>>().Object);

    [Theory]
    [InlineData(AuditOutcome.Refused)]
    [InlineData(AuditOutcome.MitigationFailed)]
    public void Record_RefusesAnOutcomeThatWouldBeErasedByItsOwnRollback(AuditOutcome outcome)
    {
        var sut = NewService();

        var act = () => sut.Record(SecurityEvents.RefreshTokenReuse, outcome);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*RecordRefusalAsync*", "the message has to name the method to use instead");
    }

    [Theory]
    [InlineData(AuditOutcome.Succeeded)]
    [InlineData(AuditOutcome.RetryCollision)]
    public async Task RecordRefusalAsync_RefusesAnOutcomeThatMustStayAtomicWithItsTransaction(
        AuditOutcome outcome)
    {
        /*
          The complement, and it fails BEFORE the scope is created — which is why a bare
          Mock<IServiceScopeFactory> that would throw on use is enough to prove the guard runs first.
        */
        var sut = NewService();

        var act = () => sut.RecordRefusalAsync(SecurityEvents.AccountDeleted, outcome);

        var thrown = await act.Should().ThrowAsync<ArgumentException>();

        /*
          THE ABSENCE IS THE ASSERTION, and asserting only the presence would have been theatre:
          "Record" is a substring of "RecordRefusalAsync", so a bare "*Record*" also matches the
          SIBLING guard's message ("must be written with RecordRefusalAsync: ..."). This method
          throwing THAT message would tell the caller to switch to the very method they just
          called — and the naive pattern would have passed. Naming what must NOT be there is what
          turns "mentions something Record-ish" into "names Record".

          The mirror assertion above needs no such pairing: "RecordRefusalAsync" is not a substring
          of anything in this method's message, so it discriminates on its own. The asymmetry is
          the substring relationship, not an inconsistency.
        */
        thrown.Which.Message.Should()
            .Contain("Record", "the message has to name the method to use instead")
            .And.NotContain(
                "RecordRefusalAsync", "which is the method that just refused the call");
    }

    [Fact]
    public void Record_AcceptsTheTwoOutcomesThatBelongToIt()
    {
        // The negative control. A guard that refused everything would pass both tests above and be
        // useless, so the accepted half is pinned as well.
        var sut = NewService();

        sut.Invoking(s => s.Record(SecurityEvents.AccountDeleted, AuditOutcome.Succeeded))
            .Should().NotThrow();
        sut.Invoking(s => s.Record(SecurityEvents.AccountNumberRevealed, AuditOutcome.RetryCollision))
            .Should().NotThrow("RetryCollision is not a refusal — it has a transaction to ride");
    }
}
