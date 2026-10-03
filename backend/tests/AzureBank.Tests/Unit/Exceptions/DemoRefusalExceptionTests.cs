using AzureBank.Shared.Constants;
using AzureBank.Shared.Exceptions;
using FluentAssertions;

namespace AzureBank.Tests.Unit.Exceptions;

/// <summary>
/// What the public demo's refusals carry: a status, a code of their own and one sentence, and a
/// wait only where the wait is known.
/// </summary>
/// <remarks>
/// <c>AppExceptionHandler</c> writes the status, puts the code in <c>errorCode</c>, the sentence in
/// <c>detail</c> and each entry of <c>Details</c> beside them, and turns <c>retryAfterSeconds</c>
/// into the <c>Retry-After</c> header. So these four values are the wire, and a client tells the
/// refusals apart by the code alone: three of them share the 429.
/// </remarks>
public class DemoRefusalExceptionTests
{
    [Fact]
    public void EachDemoRefusal_Is429_WithItsOwnCode()
    {
        var poolEmpty = DemoRefusalException.PoolEmpty();
        var dailyLimit = DemoRefusalException.DailyLimit(3600);
        var copyLimit = DemoRefusalException.CopyLimit();

        poolEmpty.StatusCode.Should().Be(429);
        dailyLimit.StatusCode.Should().Be(429);
        copyLimit.StatusCode.Should().Be(429);

        poolEmpty.ErrorCode.Should().Be(ErrorCodes.DemoPoolEmpty);
        dailyLimit.ErrorCode.Should().Be(ErrorCodes.DemoDailyLimit);
        copyLimit.ErrorCode.Should().Be(ErrorCodes.DemoCopyLimit);

        // The values a client is written against. A constant renamed is a refactor; a value changed
        // is a new contract.
        ErrorCodes.DemoPoolEmpty.Should().Be("DEMO_POOL_EMPTY");
        ErrorCodes.DemoDailyLimit.Should().Be("DEMO_DAILY_LIMIT");
        ErrorCodes.DemoCopyLimit.Should().Be("DEMO_COPY_LIMIT");

        poolEmpty.Message.Should().Be(DemoRefusalException.PoolEmptyDetail);
        dailyLimit.Message.Should().Be(DemoRefusalException.DailyLimitDetail);
        copyLimit.Message.Should().Be(DemoRefusalException.CopyLimitDetail);
        new[] { poolEmpty.Message, dailyLimit.Message, copyLimit.Message }
            .Should().OnlyHaveUniqueItems("each refusal tells the visitor something different to do");
    }

    [Fact]
    public void OnlyTheDailyLimit_CarriesRetryAfterSeconds()
    {
        // The daily limit ends at an instant the API can compute. An empty pool is refilled by a job
        // the API does not run, and a copy at its limit stays there: neither has a wait to name.
        var dailyLimit = DemoRefusalException.DailyLimit(3600);

        dailyLimit.Details.Should().NotBeNull();
        dailyLimit.Details.Should().HaveCount(1);
        dailyLimit.Details!["retryAfterSeconds"].Should().Be(3600);
        dailyLimit.Message.Any(char.IsDigit).Should().BeFalse(
            "the wait travels as a number the client formats, never inside the sentence");

        DemoRefusalException.PoolEmpty().Details.Should().BeNull();
        DemoRefusalException.CopyLimit().Details.Should().BeNull();
    }

    [Fact]
    public void RegistrationClosed_Is403_WithItsOwnCode_AndOneSentence()
    {
        var closed = new RegistrationClosedException();

        closed.StatusCode.Should().Be(403);
        closed.ErrorCode.Should().Be(ErrorCodes.RegistrationClosed);
        ErrorCodes.RegistrationClosed.Should().Be("REGISTRATION_CLOSED");
        closed.Message.Should().Be(RegistrationClosedException.Detail);
        closed.Details.Should().BeNull();
    }
}
