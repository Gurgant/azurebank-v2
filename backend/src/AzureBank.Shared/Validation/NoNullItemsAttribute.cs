using System.Collections;
using System.ComponentModel.DataAnnotations;

namespace AzureBank.Shared.Validation;

/// <summary>
/// Refuses a list that holds a null item.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why an attribute.</b> JSON binding lets <c>[null]</c> into a list of non-nullable strings: the
/// nullability of a generic argument is not enforced, and <see cref="RequiredAttribute"/> on the list
/// never looks at its items. The contract publishes those items as strings, so a null is a request
/// the contract calls invalid.
/// </para>
/// <para>
/// Written for <c>POST /api/auth/revoke</c>. Schemathesis's conformance run posted
/// <c>{"refreshTokens": [null]}</c> and got 200 "Revoked" (measured 2026-09-28): "API accepted
/// schema-violating request". A null names no grant, so it is a malformed request, not a revocation
/// of an unknown grant, which keeps its 200.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class NoNullItemsAttribute : ValidationAttribute
{
    public NoNullItemsAttribute()
        : base("The {0} field must not contain a null item.")
    {
    }

    public override bool IsValid(object? value) => value switch
    {
        null => true, // a missing list is [Required]'s to refuse
        IEnumerable items => items.Cast<object?>().All(item => item is not null),
        _ => false,
    };
}
