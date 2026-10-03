using System.Security.Cryptography;
using System.Text;

namespace AzureBank.Api.Security;

/// <summary>Makes the key a claimed demo copy's row holds in place of its client's address.</summary>
/// <remarks>
/// <para>
/// AN ADDRESS IS PERSONAL DATA, and all the row needs of it is to tell one client from another. So
/// the row holds HMAC-SHA256 of the address under <c>Demo:ClientKeySecret</c>, 32 bytes: the same
/// client gives the same key, and the address cannot be read back out of it.
/// </para>
/// <para>
/// WHY A KEY, AND NOT A PLAIN HASH. There are few enough IPv4 addresses to hash every one of them,
/// so a plain hash of an address is undone by looking it up in that list. With a secret in the
/// hash, the list cannot be made by anyone who does not hold the secret. That is also why a blank
/// secret is refused here and not hashed with: HMAC accepts an empty key, and what it gives then is
/// a hash anybody can compute.
/// </para>
/// <para>
/// THE LABEL in front of the address says what this hash is for. If the same secret ever keys a
/// second hash, the two are not each other's: a value made for one use is never the answer to the
/// other.
/// </para>
/// </remarks>
public static class DemoClientKey
{
    private const string Purpose = "demo-claim:";

    /// <summary>
    /// The key of <paramref name="clientAddress"/> under <paramref name="secret"/>: 32 bytes.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The secret is null, empty or white space, or the address is null or empty. Without an
    /// address the label alone would be hashed, and every such client given the one key.
    /// </exception>
    public static byte[] Of(string secret, string clientAddress)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        ArgumentException.ThrowIfNullOrEmpty(clientAddress);

        return HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(Purpose + clientAddress));
    }
}
