using System.Globalization;
using System.Text.Json;
using AzureBank.Api.Converters;
using FluentAssertions;

namespace AzureBank.Tests.Unit.Converters;

/// <summary>
/// A date the API writes is the same text on every host: the culture of the thread that writes or
/// reads it changes neither the text nor the instant.
/// </summary>
/// <remarks>
/// The cultures are chosen for what they do to a date with no culture named: th-TH counts years in
/// the Buddhist calendar, ar-SA uses the Umm al-Qura calendar, fi-FI puts a full stop between hours
/// and minutes, it-IT writes this format as the invariant culture does. The empty name is the
/// invariant culture. Each test sets the thread's culture and puts the previous one back.
/// </remarks>
public class Rfc3339DateTimeConverterCultureTests
{
    private const string Written = "2026-07-01T10:48:26.0000000Z";

    private static readonly DateTime Instant = new(2026, 7, 1, 10, 48, 26, DateTimeKind.Utc);

    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new Rfc3339DateTimeConverter() },
    };

    [Fact]
    public void TheCulturesChosen_WriteADateTheirOwnWay_WhenNoCultureIsNamed()
    {
        // Without this the other tests could pass on a machine that has no data for these cultures.
        Under("th-TH", () => Instant.ToString("yyyy")).Should().Be("2569");
        Under("ar-SA", () => Instant.ToString("yyyy-MM-dd")).Should().Be("1448-01-16");
        Under("fi-FI", () => Instant.ToString("HH:mm:ss")).Should().Be("10.48.26");
    }

    [Theory]
    [InlineData("th-TH")]
    [InlineData("ar-SA")]
    [InlineData("fi-FI")]
    [InlineData("it-IT")]
    [InlineData("")]
    public void Write_UnderAnyCulture_GivesTheSameRfc3339Text(string culture)
    {
        var json = Under(culture, () => JsonSerializer.Serialize(Instant, Options));

        json.Should().Be($"\"{Written}\"");
    }

    [Theory]
    [InlineData("th-TH")]
    [InlineData("ar-SA")]
    [InlineData("fi-FI")]
    [InlineData("it-IT")]
    [InlineData("")]
    public void AWrittenValue_ReadBackUnderTheSameCulture_IsTheSameInstant(string culture)
    {
        var read = Under(culture, () =>
            JsonSerializer.Deserialize<DateTime>(JsonSerializer.Serialize(Instant, Options), Options));

        read.Kind.Should().Be(DateTimeKind.Utc);
        read.Ticks.Should().Be(Instant.Ticks);
    }

    [Theory]
    [InlineData("th-TH")]
    [InlineData("ar-SA")]
    [InlineData("fi-FI")]
    [InlineData("it-IT")]
    [InlineData("")]
    public void Read_OfAnRfc3339Text_UnderAnyCulture_IsTheSameInstant(string culture)
    {
        var (utc, withOffset) = Under(culture, () => (
            JsonSerializer.Deserialize<DateTime>($"\"{Written}\"", Options),
            JsonSerializer.Deserialize<DateTime>("\"2026-07-01T12:48:26+02:00\"", Options)));

        utc.Kind.Should().Be(DateTimeKind.Utc);
        utc.Ticks.Should().Be(Instant.Ticks);
        withOffset.ToUniversalTime().Ticks.Should().Be(Instant.Ticks);
    }

    [Theory]
    [InlineData("th-TH")]
    [InlineData("fi-FI")]
    [InlineData("it-IT")]
    public void Read_OfATextThatIsNotRfc3339_UnderAnyCulture_IsWhatTheInvariantCultureReads(string culture)
    {
        const string json = "\"01/07/2026 10:48:26\"";
        var invariant = Under(string.Empty, () => JsonSerializer.Deserialize<DateTime>(json, Options));

        var read = Under(culture, () => JsonSerializer.Deserialize<DateTime>(json, Options));

        read.Should().Be(invariant);
    }

    private static T Under<T>(string culture, Func<T> act)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            return act();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
