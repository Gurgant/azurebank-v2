using System.Buffers.Binary;
using System.Security.Cryptography;
using AzureBank.Shared.Entities;
using Microsoft.AspNetCore.Identity;

namespace AzureBank.Shared.Services.Implementations;

/// <summary>
/// The hasher of account passwords: ASP.NET Core Identity's PBKDF2 with HMAC-SHA512, in the
/// Identity V3 format, at <see cref="IterationCount"/> rounds (ADR-0003). The API and the Seeder
/// register this type, so the count is set here and nowhere else.
/// </summary>
/// <remarks>
/// <para>
/// A stored hash carries the count it was made with, and Identity verifies it at that count. A hash
/// made at a lower count therefore still verifies, and the request that proved the password
/// stores a new one at <see cref="IterationCount"/>.
/// </para>
/// <para>
/// Until then that user's verification would cost fewer rounds than anybody else's, and fewer than
/// the dummy verification an unknown email spends (ADR-0012): a wrong password would be answered
/// sooner for exactly the accounts that exist and have not signed in since the count was raised.
/// So a verification spends the rounds the stored hash lacks as well, and every one costs
/// <see cref="IterationCount"/>, whatever the hash and whatever the result.
/// </para>
/// <para>
/// <c>PasswordHasherOptions</c> in the container is not read: a count bound from configuration
/// could differ between the two programs that hash.
/// </para>
/// </remarks>
public class AccountPasswordHasher : PasswordHasher<ApplicationUser>
{
    /// <summary>
    /// Rounds of PBKDF2-HMAC-SHA512 for an account password: what OWASP's Password Storage Cheat
    /// Sheet advises for this function.
    /// </summary>
    public const int IterationCount = 220_000;

    // Identity V3: a format marker, then the PRF, the count and the salt's length, four bytes each
    // and big-endian. The count is bytes 5 to 8.
    private const byte IdentityV3 = 0x01;
    private const int CountOffset = 5;
    private const int HeaderLength = 13;

    // What the top-up derives is thrown away, so its salt protects nothing.
    private static readonly byte[] TopUpSalt = new byte[16];

    public AccountPasswordHasher()
        : base(Microsoft.Extensions.Options.Options.Create(new PasswordHasherOptions { IterationCount = IterationCount }))
    {
    }

    /// <summary>
    /// The rounds <paramref name="hashedPassword"/> says it was made with, or 0 for anything that
    /// is not an Identity V3 hash.
    /// </summary>
    public static int IterationsOf(string? hashedPassword)
    {
        if (string.IsNullOrEmpty(hashedPassword))
        {
            return 0;
        }

        // A Base64 text of n characters decodes to at most 3n/4 bytes.
        var bytes = new byte[hashedPassword.Length];
        if (!Convert.TryFromBase64String(hashedPassword, bytes, out var written)
            || written < HeaderLength
            || bytes[0] != IdentityV3)
        {
            return 0;
        }

        var count = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(CountOffset, sizeof(uint)));
        return count > int.MaxValue ? int.MaxValue : (int)count;
    }

    /// <inheritdoc />
    public override PasswordVerificationResult VerifyHashedPassword(
        ApplicationUser user, string hashedPassword, string providedPassword)
    {
        var result = base.VerifyHashedPassword(user, hashedPassword, providedPassword);

        var missing = IterationCount - IterationsOf(hashedPassword);
        if (missing > 0)
        {
            _ = TopUp(providedPassword, missing);
        }

        return result;
    }

    /// <summary>
    /// What <paramref name="iterations"/> rounds of the same function derive from the password and
    /// a salt of 16 zero bytes. A verification throws it away: the rounds are what it is for.
    /// </summary>
    protected virtual byte[] TopUp(string password, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(password, TopUpSalt, iterations, HashAlgorithmName.SHA512, outputLength: 32);
}
