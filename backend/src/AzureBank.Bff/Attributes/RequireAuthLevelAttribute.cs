namespace AzureBank.Bff.Attributes;

/// <summary>
/// Marks an endpoint as requiring a minimum authentication level.
/// Level 0 = None (public)
/// Level 1 = Session authenticated (logged in)
/// Level 2 = PIN verified (for sensitive operations)
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public class RequireAuthLevelAttribute : Attribute
{
    public int MinimumLevel { get; }

    public RequireAuthLevelAttribute(int minimumLevel)
    {
        MinimumLevel = minimumLevel;
    }
}
