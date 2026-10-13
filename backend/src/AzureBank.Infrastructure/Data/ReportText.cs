using System.Globalization;

namespace AzureBank.Infrastructure.Data;

/// <summary>
/// How a number is written in a finding of the audit chain and in a line the verifier prints.
/// </summary>
/// <remarks>
/// A person reads these texts, compares two of them, or hands one to a script, so one fact has one
/// text: the same sequence must not read 12,345 on one host and 12.345 on another. Nothing here
/// takes part in a hash, a signature or a stored value; the payloads that are hashed write their
/// numbers without groups.
/// </remarks>
public static class ReportText
{
    /// <summary>
    /// The number with its digits in groups of three and a comma between the groups, under every
    /// culture: 12345 is "12,345".
    /// </summary>
    public static string Grouped(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>The same for a number that may be absent, which stays absent.</summary>
    public static string? Grouped(long? value) => value?.ToString("N0", CultureInfo.InvariantCulture);
}
