using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Xunit.Sdk;

namespace AzureBank.Tests.Integration;

/// <summary>
/// <see cref="ProblemBody.WithoutTraceId"/> held on fixed texts, because the fault it removes
/// cannot be waited for: a trace id holds the four digits a test looks for about once in 2,260
/// requests.
/// </summary>
/// <remarks>
/// The first test's text is the 403 that <c>GET /api/transactions/summary</c> answers for
/// another user's account, as <c>Summary_WithAnotherUsersAccountId_ReturnsForbidden</c> received
/// it on 2026-10-06, with four characters of its id written over by hand. Each row of the second
/// test is that body with its own id and one member changed or added. The texts of the third
/// are made up.
/// </remarks>
public class ProblemBodyTests
{
    private const string UpToTheDetail =
        "{\"type\":\"https://httpstatuses.com/403\",\"title\":\"Forbidden\",\"status\":403,";

    private const string FromTheInstance =
        "\"instance\":\"/api/transactions/summary\",\"errorCode\":\"ACCESS_DENIED\",";

    private const string UpToTheTraceId =
        UpToTheDetail + "\"detail\":\"You do not have access to this account.\"," + FromTheInstance;

    private const string TheTraceId = "\"traceId\":\"b5efcbf50d00125e9e2137d96c640622\"";

    private const string AnIdThatHoldsTheFigure = "b5efcbf50d0042429e2137d96c640622";

    [Fact]
    public void WithoutTraceId_WhenOnlyTheTraceIdHoldsTheFigure_LeavesNothingToFind()
    {
        // Arrange — the refusal as the API wrote it, with an id that happens to hold 4242.
        const string body = UpToTheTraceId + "\"traceId\":\"" + AnIdThatHoldsTheFigure + "\"}";

        // Act
        var searched = ProblemBody.WithoutTraceId(body);

        // Assert — the search of the whole body fails on the id alone; without the id it
        // passes, and what is left is every other character of the body, in order. The last
        // comparison is xUnit's, whose message shows a brace as it is (ComparableText).
        Action searchOfTheWholeBody = () => body.Should().NotContain("4242");
        searchOfTheWholeBody.Should().Throw<XunitException>(
            "the endpoint test asserted that until 2026-10-06, and the id alone fails it");
        searched.Should().NotContain("4242");
        Assert.Equal(UpToTheTraceId + "}", searched);
    }

    [Theory]
    [InlineData("the detail",
        UpToTheDetail + "\"detail\":\"The account holds 4242.00.\"," + FromTheInstance
        + TheTraceId + "}")]
    [InlineData("a number written after the id",
        UpToTheTraceId + TheTraceId + ",\"available\":4242.0,\"requested\":10}")]
    [InlineData("another member with an id of the same shape",
        UpToTheTraceId + "\"spanId\":\"" + AnIdThatHoldsTheFigure + "\"," + TheTraceId + "}")]
    [InlineData("a traceId inside another member",
        UpToTheTraceId + TheTraceId + ",\"cause\":{\"traceId\":\"" + AnIdThatHoldsTheFigure + "\"}}")]
    [InlineData("a traceId inside another member, written before the body's own",
        UpToTheTraceId + "\"cause\":{\"traceId\":\"" + AnIdThatHoldsTheFigure + "\"},"
        + TheTraceId + "}")]
    [InlineData("an id in the path, which instance repeats",
        UpToTheDetail + "\"detail\":\"You do not have access to this account.\","
        + "\"instance\":\"/api/accounts/019f4242-0c1e-7a6b-9d2f-3b5a8c7e1d04\","
        + "\"errorCode\":\"ACCESS_DENIED\"," + TheTraceId + "}")]
    public void WithoutTraceId_WhenAnotherMemberHoldsTheFigure_StillShowsIt(string where, string body)
    {
        // Act
        var searched = ProblemBody.WithoutTraceId(body);

        // Assert — the assertion the endpoint test makes still fails on this body.
        Action search = () => searched.Should().NotContain("4242");
        search.Should().Throw<XunitException>(
            "the figure is in {0}, which is not the body's trace id", where);
    }

    [Theory]
    // Spacing, an escape, a trailing zero and a letter outside ASCII: what a helper that read
    // the body and wrote it again would not give back the same.
    [InlineData("{ \"status\" : 403, \"detail\" : \"l\\u0027été\", \"available\" : 10.50 }")]
    // Not JSON at all, and JSON that stops inside the member: there is nothing to cut.
    [InlineData("Forbidden. traceId: " + AnIdThatHoldsTheFigure)]
    [InlineData("{\"status\":403,\"traceId\":\"b5efcbf50d004242")]
    [InlineData("")]
    public void WithoutTraceId_WhenThereIsNoTraceIdToCut_ReturnsTheBodyAsItWas(string body)
    {
        // Act + Assert
        Assert.Equal(body, ProblemBody.WithoutTraceId(body));
    }
}
