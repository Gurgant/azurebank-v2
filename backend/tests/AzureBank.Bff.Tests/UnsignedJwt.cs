using System.Buffers.Text;
using System.Text;

namespace AzureBank.Bff.Tests;

/// <summary>
/// JWT-shaped tokens for the tests in which the BFF has to read a token's own lifetime: an opaque
/// token is dated from the moment the BFF received it.
/// </summary>
internal static class UnsignedJwt
{
    /// <summary>
    /// A JWT-shaped token for the subject <c>held</c>, whose payload carries
    /// <paramref name="issuedAt"/> and <paramref name="expiresAt"/>, so the BFF reads how much of
    /// its lifetime is left. Unsigned: the BFF reads a token, it never verifies one.
    /// </summary>
    internal static string Jwt(DateTime issuedAt, DateTime expiresAt)
    {
        static string Part(string json) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json));
        var iat = new DateTimeOffset(issuedAt).ToUnixTimeSeconds();
        var exp = new DateTimeOffset(expiresAt).ToUnixTimeSeconds();
        return Part("""{"alg":"none","typ":"JWT"}""")
            + "." + Part($$"""{"sub":"held","iat":{{iat}},"exp":{{exp}}}""")
            + ".test-signature";
    }
}
