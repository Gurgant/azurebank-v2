using System.Globalization;
using AzureBank.Shared.Utilities;
using FluentAssertions;

namespace AzureBank.Tests.Unit.Utilities;

/// <summary>
/// The day inside a transaction number is the UTC day in the Gregorian calendar on every host,
/// whatever calendar the thread's culture counts in.
/// </summary>
/// <remarks>
/// th-TH counts years in the Buddhist calendar and ar-SA uses the Umm al-Qura calendar; the empty
/// name is the invariant culture. The day is read before and after the call, so a test that runs
/// across midnight UTC accepts either.
/// </remarks>
public class IdGeneratorCultureTests
{
    [Theory]
    [InlineData("th-TH")]
    [InlineData("ar-SA")]
    [InlineData("fi-FI")]
    [InlineData("")]
    public void GenerateTransactionNumber_UnderAnyCulture_CarriesTheGregorianUtcDay(string culture)
    {
        var (number, days) = Under(culture, IdGenerator.GenerateTransactionNumber);

        days.Select(day => $"TXN-{day}-").Should().Contain(number[..13]);
        IdGenerator.IsValidTransactionNumber(number).Should().BeTrue();
    }

    private static (string Value, string[] Days) Under(string culture, Func<string> generate)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            var before = DateTime.UtcNow;
            var value = generate();
            var after = DateTime.UtcNow;
            return (value, [Day(before), Day(after)]);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static string Day(DateTime utc) => utc.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
}
