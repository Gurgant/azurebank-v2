using System.Security.Cryptography;

namespace AzureBank.Api.Security;

/// <summary>Draws the password of a demo copy, for the visitor who claims it.</summary>
/// <remarks>
/// <para>
/// A PERSON READS IT. The visitor is shown it and may type it by hand on another device, so it is
/// four groups of four joined by hyphens, in the shape of <c>Kp7m-Xw2R-hd9G-tQ4n</c>, and its
/// alphabet leaves out the six characters that pass for one another: I, O, l, o, 0 and 1.
/// </para>
/// <para>
/// THE API READS IT AT EVERY SIGN-IN. <c>LoginRequest.Password</c> is held to
/// <c>ValidationRules.PasswordPattern</c> before the credentials are looked at: an upper-case
/// letter, a lower-case letter, a digit and a character that is none of the three. The hyphens are
/// the last. The others are a matter of the draw, so a draw that lacks one is thrown away and made
/// again: about one in twelve, nearly all of them for want of a digit, of which the alphabet has
/// eight in fifty-six (<c>python -c "print((48/56)**16)"</c> prints 0.0848...).
/// </para>
/// <para>
/// EVERY CHARACTER comes from the cryptographic generator, one <c>GetInt32</c> each: a generator
/// whose next value follows from its last would hand one visitor the passwords of the next.
/// <c>GetInt32</c> is uniform over the alphabet; a random byte reduced modulo 56 would favour its
/// first thirty-two characters. Sixteen characters of fifty-six are 92 bits
/// (<c>python -c "from math import log2; print(16*log2(56))"</c> prints 92.9...), and the draws
/// thrown away cost a fraction of one bit.
/// </para>
/// </remarks>
public static class DemoPasswordGenerator
{
    // Upper case without I and O, lower case without l and o, the digits from 2 to 9: fifty-six.
    internal const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";

    private const int Groups = 4;
    private const int GroupLength = 4;
    private const char Separator = '-';

    /// <summary>
    /// A new password: four groups of four characters, with an upper-case letter, a lower-case
    /// letter and a digit among them.
    /// </summary>
    public static string Create() =>
        Create(static toExclusive => RandomNumberGenerator.GetInt32(toExclusive));

    /// <summary>
    /// The same, with each character's place in the alphabet asked of <paramref name="next"/>, which
    /// is given the alphabet's size and answers a place below it. A test's way to choose the draw:
    /// one that lacks an upper-case letter comes up about 13 times in 100,000
    /// (<c>python -c "print((32/56)**16)"</c> prints 0.000129...), too seldom to wait for.
    /// </summary>
    internal static string Create(Func<int, int> next)
    {
        while (true)
        {
            var password = Draw(next);
            if (password.Any(char.IsAsciiLetterUpper)
                && password.Any(char.IsAsciiLetterLower)
                && password.Any(char.IsAsciiDigit))
            {
                return password;
            }
        }
    }

    private static string Draw(Func<int, int> next) =>
        string.Create(Groups * GroupLength + Groups - 1, next, static (characters, next) =>
        {
            for (var i = 0; i < characters.Length; i++)
            {
                // Every fifth character is the hyphen between two groups.
                characters[i] = (i + 1) % (GroupLength + 1) == 0
                    ? Separator
                    : Alphabet[next(Alphabet.Length)];
            }
        });
}
