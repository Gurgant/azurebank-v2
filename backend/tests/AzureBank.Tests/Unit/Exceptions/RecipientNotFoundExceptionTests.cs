using AzureBank.Shared.Constants;
using AzureBank.Shared.Exceptions;
using FluentAssertions;

namespace AzureBank.Tests.Unit.Exceptions;

/// <summary>
/// A missing recipient is a 404 under a code of its own, with the sentence every other 404 has.
/// </summary>
public class RecipientNotFoundExceptionTests
{
    [Fact]
    public void AnswersNotFound_UnderItsOwnCode_WithTheHandleInTheSentence()
    {
        var exception = new RecipientNotFoundException("mikejohnson");

        exception.StatusCode.Should().Be(404);
        exception.ErrorCode.Should().Be(ErrorCodes.RecipientNotFound);
        exception.Message.Should().Be("Recipient with identifier 'mikejohnson' was not found.");
        exception.Details.Should().BeNull("the code is all this refusal adds to the body");
    }

    [Fact]
    public void ItsCode_IsNotTheCodeOfAMissingAccount()
    {
        // The two 404s of an external transfer: a client tells them apart by the code alone.
        ErrorCodes.RecipientNotFound.Should().Be("RECIPIENT_NOT_FOUND");
        new RecipientNotFoundException("mikejohnson").ErrorCode
            .Should().NotBe(new NotFoundException("Account", Guid.NewGuid()).ErrorCode);
    }

    [Fact]
    public void IsNotANotFoundException()
    {
        // Code that catches NotFoundException is about ACCOUNT_NOT_FOUND, and does not take this
        // refusal with it.
        typeof(NotFoundException).IsAssignableFrom(typeof(RecipientNotFoundException)).Should().BeFalse();
    }
}
