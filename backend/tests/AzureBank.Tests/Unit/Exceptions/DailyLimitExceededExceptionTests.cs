using AzureBank.Shared.Constants;
using AzureBank.Shared.Exceptions;
using FluentAssertions;

namespace AzureBank.Tests.Unit.Exceptions;

/// <summary>
/// The refusal body's shape (ADR-0050 D7): a figure-free sentence and four extension members, by
/// the <c>INSUFFICIENT_FUNDS {available, requested}</c> precedent.
/// </summary>
public class DailyLimitExceededExceptionTests
{
    private static readonly DateTime NextMidnight = new(2026, 9, 8, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void CarriesTheFourMembers_AsTheValuesGiven()
    {
        var exception = new DailyLimitExceededException(5000m, 4000m, 1000.01m, NextMidnight);

        // AppExceptionHandler spreads Details into ProblemDetails.Extensions verbatim, so the keys
        // here ARE the wire member names and the values ARE the JSON values — decimals as numbers,
        // the instant as a string. The SPA's ApiProblem types exactly these four.
        exception.Details.Should().NotBeNull();
        exception.Details.Should().HaveCount(4);
        exception.Details!["limit"].Should().Be(5000m);
        exception.Details["used"].Should().Be(4000m);
        exception.Details["requested"].Should().Be(1000.01m);
        exception.Details["resetsAt"].Should().Be(NextMidnight);
        ((DateTime)exception.Details["resetsAt"]).Kind.Should().Be(
            DateTimeKind.Utc, "System.Text.Json writes a Utc instant with the Z the client needs");
    }

    [Fact]
    public void TheSentenceCarriesNoFigure_AndTheCodeAndStatusAreTheContract()
    {
        var exception = new DailyLimitExceededException(5000m, 4000m, 1000.01m, NextMidnight);

        exception.Message.Should().Be("Daily transfer limit exceeded.");
        exception.Message.Should().NotContainAny("5000", "4000", "1000");
        exception.Message.Any(char.IsDigit).Should().BeFalse(
            "the figures travel as numeric members the client formats itself; "
            + "rendered into the sentence they would carry the server process culture (the 3769dc9 rule)");
        exception.ErrorCode.Should().Be(ErrorCodes.DailyLimitExceeded);
        exception.StatusCode.Should().Be(422);
        exception.Should().BeAssignableTo<BusinessRuleException>();
    }
}
