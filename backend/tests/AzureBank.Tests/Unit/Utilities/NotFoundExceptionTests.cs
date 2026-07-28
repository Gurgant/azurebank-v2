using AzureBank.Shared.Constants;
using AzureBank.Shared.Exceptions;
using FluentAssertions;
using Xunit;

namespace AzureBank.Tests.Unit.Utilities;

/// <summary>
/// Pins the constructor BINDING, not just the message.
/// </summary>
/// <remarks>
/// These tests exist because the type used to expose a second constructor,
/// <c>(string message, string errorCode)</c>, which overload resolution preferred whenever the
/// identifier happened to be a string. Every call site reads
/// <c>new NotFoundException("Resource", identifier)</c>, so the seven that pass a Guid were correct
/// and the one that passes an azureTag — the recipient lookup on the external transfer path — was
/// silently constructing a completely different exception: message "Recipient", errorCode set to
/// the handle the user typed.
///
/// A test that only asserted the message for a Guid could never have caught it. The string case is
/// the whole point, which is why it is first.
/// </remarks>
public class NotFoundExceptionTests
{
    [Fact]
    public void StringIdentifier_BindsToTheResourceConstructor_NotAMessageAndCode()
    {
        // The regression. Before the second constructor was deleted this produced
        // Message = "Recipient" and ErrorCode = "mikejohnson" — verified on the wire against the
        // running API, not inferred.
        var exception = new NotFoundException("Recipient", "mikejohnson");

        exception.Message.Should().Be("Recipient with identifier 'mikejohnson' was not found.");
        exception.ErrorCode.Should().Be(ErrorCodes.AccountNotFound);
        exception.StatusCode.Should().Be(404);
    }

    [Fact]
    public void GuidIdentifier_BehavesIdentically()
    {
        var id = Guid.Parse("019f9ea7-445b-789e-9e3f-1de1e94daf22");

        var exception = new NotFoundException("Account", id);

        exception.Message.Should().Be($"Account with identifier '{id}' was not found.");
        exception.ErrorCode.Should().Be(ErrorCodes.AccountNotFound);
    }

    [Theory]
    [InlineData("Account")]
    [InlineData("User")]
    [InlineData("Transaction")]
    [InlineData("Recipient")]
    public void EveryResourceReportsAccountNotFound(string resource)
    {
        // Surprising, and deliberate: the constructor hard-codes AccountNotFound for every
        // resource. TransactionNotFound and UserNotFound exist but are unreachable from this path.
        // The frontend and the MSW mock both mirror this quirk, so a "correction" here would break
        // three places at once. Pinned so that it stays a decision rather than becoming a bug.
        new NotFoundException(resource, Guid.NewGuid()).ErrorCode
            .Should().Be(ErrorCodes.AccountNotFound);
    }
}
