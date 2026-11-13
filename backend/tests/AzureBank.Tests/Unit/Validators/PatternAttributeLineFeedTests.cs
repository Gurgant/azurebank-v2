using System.ComponentModel.DataAnnotations;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.Validation;
using FluentAssertions;

namespace AzureBank.Tests.Unit.Validators;

/// <summary>
/// The handle, PIN and password attributes on their own. Each builds a Regex from a <c>^...$</c>
/// pattern, and without <c>RegexOptions.Multiline</c> the <c>$</c> also matches before a final line
/// feed, so each attribute requires the match to cover the whole value. "alice\n", "123456\n" and
/// "Abcdefg1\n" must be refused, as the published patterns and the generated zod schema refuse them.
/// </summary>
public class PatternAttributeLineFeedTests
{
    [Theory]
    [InlineData("alice")]
    [InlineData("a_b_1")]
    public void AzureTag_AcceptsAWholeHandle(string tag)
    {
        new AzureTagAttribute().IsValid(tag).Should().BeTrue();
    }

    [Fact]
    public void AzureTag_RefusesAHandleEndingInALineFeed()
    {
        new AzureTagAttribute().IsValid("alice\n").Should().BeFalse("'$' alone would match before the final line feed");
    }

    [Fact]
    public void Pin_AcceptsSixDigits()
    {
        new PinAttribute().IsValid("123456").Should().BeTrue();
    }

    [Fact]
    public void Pin_RefusesAPinEndingInALineFeed()
    {
        new PinAttribute().IsValid("123456\n").Should().BeFalse("the contract publishes exactly 6 characters");
    }

    [Fact]
    public void Password_AcceptsAWholePassword()
    {
        new PasswordAttribute().IsValid("Abcdef1!").Should().BeTrue();
    }

    [Fact]
    public void Password_RefusesAPasswordEndingInALineFeed()
    {
        new PasswordAttribute().IsValid("Abcdefg1\n").Should().BeFalse(
            "the lookahead takes the line feed as the special character and '$' matches before it");
    }

    [Fact]
    public void AzureTagQuery_AcceptsAWholeHandle()
    {
        var context = new ValidationContext(new object()) { MemberName = "azureTag" };

        new AzureTagQueryAttribute().GetValidationResult("alice", context).Should().Be(ValidationResult.Success);
    }

    [Fact]
    public void AzureTagQuery_RefusesAHandleEndingInALineFeed()
    {
        // A name that differs from the attribute's own "azureTag" fallback, so the assertion shows the
        // context's name was used; the message shows the pattern branch refused, not a length branch.
        var context = new ValidationContext(new object()) { MemberName = "NewAzureTag" };

        var result = new AzureTagQueryAttribute().GetValidationResult("alice\n", context);

        result.Should().NotBe(ValidationResult.Success);
        result!.ErrorMessage.Should().Be(ValidationRules.AzureTagPatternMessage);
        result.MemberNames.Should().Equal("NewAzureTag");
    }

    [Fact]
    public void OnARegisterRequest_ItNamesTheAzureTagMember()
    {
        var request = new RegisterRequest
        {
            AzureTag = "alice\n",
            Email = "alice@example.com",
            Password = "Abcdef1!",
            FirstName = "Alice",
            LastName = "Smith",
        };
        var results = new List<ValidationResult>();

        var valid = Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);

        valid.Should().BeFalse();
        results.Should().ContainSingle()
            .Which.MemberNames.Should().Equal(nameof(RegisterRequest.AzureTag));
    }
}
