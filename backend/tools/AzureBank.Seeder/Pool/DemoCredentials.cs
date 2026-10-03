using System.Security.Cryptography;

namespace AzureBank.Seeder.Pool;

/// <summary>One user of a demo copy: who they are and how they are found.</summary>
public sealed record DemoIdentity(string FirstName, string LastName, string AzureTag, string Email);

/// <summary>The three users of one demo copy, and the suffix their handles share.</summary>
public sealed record DemoCopyIdentities(string Suffix, DemoIdentity Owner, DemoIdentity Jane, DemoIdentity Mike);

/// <summary>Draws the handles and the email addresses of a demo copy.</summary>
/// <remarks>
/// <para>
/// THE CAST IS THE FIXED DEMO'S: John Smith, and the two people he pays, Jane Smith and Mike Brown.
/// What differs from one copy to the next is how they are found. The three handles share one
/// suffix of four characters (<c>john_k7m2</c>, <c>jane_k7m2</c>, <c>mike_k7m2</c>), so a visitor
/// reads their contacts' handles off their own. Handles are unique in the whole table, so a
/// suffix another copy already holds is refused by the database and the copy is drawn again.
/// </para>
/// <para>
/// THE EMAIL ADDRESS IS WHAT A VISITOR SIGNS IN WITH, so nobody else may be able to work it out.
/// Its sixteen characters are drawn on their own: never from the copy's id (a version 7 id carries
/// the instant it was minted) and never from the suffix (every user of the copy can read it).
/// Sixteen characters of thirty-six are 82 bits.
/// </para>
/// <para>
/// EVERY CHARACTER comes from the cryptographic generator, one <c>GetInt32</c> each. A generator
/// whose next value follows from its last would hand a visitor the addresses of the copies seeded
/// beside theirs. <c>GetInt32</c> is uniform over the alphabet; a random byte reduced modulo 36
/// would favour the first four letters.
/// </para>
/// <para>
/// No password is drawn here: a free copy has none, so nothing can sign in to it. The claim sets
/// one when a visitor takes the copy.
/// </para>
/// </remarks>
public static class DemoCredentials
{
    // Lower case only: a handle is stored and compared in lower case, and Identity normalises an
    // address to upper case, so a mixed alphabet would buy no bits in the address and fail the
    // handle rule.
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

    private const int SuffixLength = 4;
    private const int EmailPartLength = 16;

    /// <summary>
    /// The domain of every copy's address. <c>.example</c> is reserved (RFC 2606): no mail can
    /// ever be delivered to it, and nobody can register it.
    /// </summary>
    public const string EmailDomain = "azurebank.example";

    /// <summary>The identities of one new copy.</summary>
    public static DemoCopyIdentities Create()
    {
        var suffix = Draw(SuffixLength);
        return new DemoCopyIdentities(
            suffix,
            new DemoIdentity("John", "Smith", $"john_{suffix}", $"demo-{Draw(EmailPartLength)}@{EmailDomain}"),
            new DemoIdentity("Jane", "Smith", $"jane_{suffix}", $"contact-{Draw(EmailPartLength)}@{EmailDomain}"),
            new DemoIdentity("Mike", "Brown", $"mike_{suffix}", $"contact-{Draw(EmailPartLength)}@{EmailDomain}"));
    }

    private static string Draw(int length) =>
        string.Create(length, 0, static (characters, _) =>
        {
            for (var i = 0; i < characters.Length; i++)
            {
                characters[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
            }
        });
}
