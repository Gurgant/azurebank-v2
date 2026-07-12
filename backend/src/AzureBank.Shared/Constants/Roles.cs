namespace AzureBank.Shared.Constants;

/// <summary>
/// Application role constants - single source of truth.
/// Use these constants throughout the application to avoid magic strings.
/// </summary>
/// <remarks>
/// Uses nameof() to ensure compile-time sync with RoleType enum.
/// Constants can be used in [Authorize(Roles = Roles.Admin)] attributes.
/// </remarks>
public static class Roles
{
    // ═══════════════════════════════════════════════════════════════
    // ROLE CONSTANTS (for attributes and Identity operations)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Standard user role - default for registered users</summary>
    public const string User = nameof(RoleType.User);

    /// <summary>Administrator role - elevated privileges</summary>
    public const string Admin = nameof(RoleType.Admin);

    // ═══════════════════════════════════════════════════════════════
    // ROLE COLLECTIONS (for seeding and validation)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>All available roles in the system</summary>
    public static readonly string[] All = [User, Admin];

    /// <summary>Default role assigned to new users during registration</summary>
    public const string Default = User;
}

/// <summary>
/// Strongly-typed role enum for business logic.
/// Use Roles.* constants for Identity operations.
/// </summary>
public enum RoleType
{
    User,
    Admin
}
