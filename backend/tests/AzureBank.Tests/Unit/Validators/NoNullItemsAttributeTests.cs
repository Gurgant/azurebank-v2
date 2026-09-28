using System.ComponentModel.DataAnnotations;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.Validation;
using FluentAssertions;

namespace AzureBank.Tests.Unit.Validators;

/// <summary>
/// <see cref="NoNullItemsAttribute"/> on its own, branch by branch. The only other test of it is the
/// revoke endpoint's 400 (<c>AuthEndpointTests.Revoke_WithANullGrant_Is400_AndRevokesNothing</c>),
/// which reaches the list branch alone.
/// </summary>
public class NoNullItemsAttributeTests
{
    private readonly NoNullItemsAttribute _attribute = new();

    [Fact]
    public void AMissingList_IsLeftToRequired()
    {
        _attribute.IsValid(null).Should().BeTrue("a missing list is [Required]'s to refuse");
    }

    [Fact]
    public void AnEmptyList_HasNoNullItem()
    {
        _attribute.IsValid(Array.Empty<string>()).Should().BeTrue("the list's length is [Length]'s to refuse");
    }

    [Fact]
    public void AListOfValues_IsValid()
    {
        _attribute.IsValid(new List<string> { "a", "b" }).Should().BeTrue();
    }

    [Fact]
    public void AListWithANullItem_IsInvalid()
    {
        _attribute.IsValid(new List<string?> { "a", null }).Should().BeFalse();
    }

    [Fact]
    public void AValueThatIsNotAList_IsInvalid()
    {
        _attribute.IsValid(42).Should().BeFalse("the attribute is for lists, and anything else is refused rather than passed");
    }

    [Fact]
    public void OnARevokeRequest_ItNamesTheListInTheMessageTheApiAnswers()
    {
        var request = new RevokeRequest { RefreshTokens = ["grant", null!] };
        var results = new List<ValidationResult>();

        var valid = Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);

        valid.Should().BeFalse();
        results.Should().ContainSingle()
            .Which.ErrorMessage.Should().Be("The RefreshTokens field must not contain a null item.");
        results[0].MemberNames.Should().Equal(nameof(RevokeRequest.RefreshTokens));
    }
}
