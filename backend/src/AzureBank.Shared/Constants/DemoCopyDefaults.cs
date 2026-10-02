namespace AzureBank.Shared.Constants;

/// <summary>What every demo copy starts with.</summary>
public static class DemoCopyDefaults
{
    /// <summary>The PIN every user of a copy is seeded with.</summary>
    /// <remarks>
    /// A constant and not a setting: it is the same on every copy and it is no secret. What keeps a
    /// copy private is its owner's sign-in, never this.
    /// </remarks>
    public const string Pin = "123456";
}
