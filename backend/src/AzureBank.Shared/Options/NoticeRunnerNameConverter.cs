using System.ComponentModel;
using System.Globalization;

namespace AzureBank.Shared.Options;

/// <summary>
/// Reads <c>Notices:Runner</c> from one of the names of <see cref="NoticeRunner"/> and from nothing
/// else, for every host and tool that binds the <c>Notices</c> section.
/// </summary>
/// <remarks>
/// <para>
/// Configuration binding asks the type for its converter, and the one an enum has by default reads
/// more than names: a number written as text (<c>1</c> starts the API's relay, <c>7</c> binds to a
/// value no member has, and every host then steps aside in silence), a comma-separated list of
/// names, and any of them with white space around it. Which host delivers notices is decided by
/// this one setting, so it has to be a word a person can read in a file.
/// </para>
/// <para>
/// ON THE TYPE AND NOT IN A VALIDATOR, because a validator sees the bound value and <c>1</c> has
/// already become <see cref="NoticeRunner.Api"/> by then: only the read sees the text. The refusal
/// is therefore the binder's, not an options validation failure. It names the key and the value,
/// and carries the message below as its cause.
/// </para>
/// <para>
/// Any letter case is read (<c>api</c>, <c>FUNCTION</c>), as the API reads an enum in a request: a
/// name in another case still says which runner it is.
/// </para>
/// </remarks>
public sealed class NoticeRunnerNameConverter : TypeConverter
{
    private static readonly string[] Names = Enum.GetNames<NoticeRunner>();

    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType) =>
        sourceType == typeof(string);

    public override object ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
    {
        if (value is string text)
        {
            foreach (var name in Names)
            {
                if (string.Equals(name, text, StringComparison.OrdinalIgnoreCase))
                {
                    return Enum.Parse<NoticeRunner>(name);
                }
            }
        }

        throw new FormatException(
            $"Notices:Runner must be one of these names: {string.Join(", ", Names)}. A number is not "
            + "read, even the number of a name, and neither is a list of names or a name with spaces "
            + "around it. Leave the setting out and no host delivers, as with None.");
    }
}
