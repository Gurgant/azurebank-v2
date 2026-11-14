namespace AzureBank.Shared.Validation;

/// <summary>
/// The one way a payee's handle is lower-cased on arrival: the letters A to Z become a to z and
/// every other character is left as it is. Not <c>ToLowerInvariant</c>: that also turns a few
/// non-ASCII characters into ASCII ones (the Kelvin sign U+212A becomes <c>k</c>), which would let
/// a handle that is not made of the characters the contract names resolve to one that is.
/// </summary>
public static class HandleCase
{
    public static string Lower(string value)
    {
        var first = value.AsSpan().IndexOfAnyInRange('A', 'Z');
        if (first < 0)
        {
            return value;
        }

        return string.Create(value.Length, value, static (chars, source) =>
        {
            for (var i = 0; i < chars.Length; i++)
            {
                var c = source[i];
                chars[i] = c is >= 'A' and <= 'Z' ? (char)(c + ('a' - 'A')) : c;
            }
        });
    }
}
