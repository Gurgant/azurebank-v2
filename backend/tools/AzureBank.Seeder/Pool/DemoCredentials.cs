namespace AzureBank.Seeder.Pool;

/// <summary>One user of a demo copy: who they are and how they are found.</summary>
public sealed record DemoIdentity(string FirstName, string LastName, string AzureTag, string Email);

/// <summary>The three users of one demo copy, and the suffix their handles share.</summary>
public sealed record DemoCopyIdentities(string Suffix, DemoIdentity Owner, DemoIdentity Jane, DemoIdentity Mike);

/// <summary>Draws the handles and the email addresses of a demo copy.</summary>
/// <remarks>NOT WRITTEN YET: it answers empty values, so its tests compile and fail.</remarks>
public static class DemoCredentials
{
    /// <summary>The identities of one new copy.</summary>
    public static DemoCopyIdentities Create()
    {
        var nobody = new DemoIdentity(string.Empty, string.Empty, string.Empty, string.Empty);
        return new DemoCopyIdentities(string.Empty, nobody, nobody, nobody);
    }
}
