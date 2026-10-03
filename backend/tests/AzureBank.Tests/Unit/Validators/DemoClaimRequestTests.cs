using System.ComponentModel.DataAnnotations;
using AzureBank.Shared.DTOs.Auth;
using FluentAssertions;

namespace AzureBank.Tests.Unit.Validators;

/// <summary>
/// The demo claim's one field: the client's address, required, of 1 to 64 characters.
/// </summary>
/// <remarks>
/// Checked by its annotations alone, with no validator class, as <see cref="RevokeRequest"/> and
/// <see cref="RefreshRequest"/> are.
/// </remarks>
public class DemoClaimRequestTests
{
    private static (bool Valid, List<ValidationResult> Results) Validated(string? clientAddress)
    {
        var request = new DemoClaimRequest { ClientAddress = clientAddress! };
        var results = new List<ValidationResult>();
        var valid = Validator.TryValidateObject(
            request, new ValidationContext(request), results, validateAllProperties: true);
        return (valid, results);
    }

    // CONTROL: green before this change. An address the refusals below would be wrong to refuse:
    // without it they would also pass on a rule that refused everything.
    [Theory]
    [InlineData("7")]
    [InlineData("203.0.113.7")]
    [InlineData("2001:db8:85a3:8d3::/64")]
    [InlineData("unknown")]
    [InlineData("0123456789012345678901234567890123456789012345678901234567890123")]
    public void AClientAddressOfOneTo64Characters_IsValid(string clientAddress)
    {
        clientAddress.Length.Should().BeInRange(1, 64, "ARRANGE: the last row is the longest allowed");
        DemoClaimRequest.MaxClientAddressLength.Should().Be(64);

        var (valid, results) = Validated(clientAddress);

        valid.Should().BeTrue();
        results.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("01234567890123456789012345678901234567890123456789012345678901234")]
    public void AnEmptyOrA65CharacterAddress_IsRefused(string? clientAddress)
    {
        var (valid, results) = Validated(clientAddress);

        valid.Should().BeFalse();
        results.Should().ContainSingle()
            .Which.MemberNames.Should().Equal(nameof(DemoClaimRequest.ClientAddress));
    }
}
