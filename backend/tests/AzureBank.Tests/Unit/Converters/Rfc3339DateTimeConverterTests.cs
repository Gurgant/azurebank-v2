using System.Text.Json;
using AzureBank.Api.Converters;
using FluentAssertions;
using FluentAssertions.Execution;

namespace AzureBank.Tests.Unit.Converters;

/// <summary>
/// The API's <c>DateTime</c> converter in the two directions no test read: a date that ARRIVES,
/// and a local time that leaves.
/// </summary>
/// <remarks>
/// Every response of the API writes a UTC instant through this converter, and the contract's gate
/// reads those. The reading half ran in no test: the instant a client sends with an offset, the
/// instant it sends in UTC, and the empty string. Neither did the conversion of a local time
/// before it is written with the <c>Z</c> that says UTC.
/// </remarks>
public sealed class Rfc3339DateTimeConverterTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new Rfc3339DateTimeConverter() },
    };

    [Fact]
    public void AnInstantInUtc_IsReadAsThatInstant_InUtc()
    {
        var read = JsonSerializer.Deserialize<DateTime>("\"2026-01-12T10:48:26.5907775Z\"", Options);

        using var all = new AssertionScope();
        read.Kind.Should().Be(DateTimeKind.Utc);
        read.Should().Be(new DateTime(2026, 1, 12, 10, 48, 26, DateTimeKind.Utc).AddTicks(5907775));
    }

    [Fact]
    public void AnInstantWithAnOffset_IsReadAsTheSameInstant()
    {
        // 12:48 at +02:00 is 10:48 in UTC, whatever this machine's own zone is.
        var read = JsonSerializer.Deserialize<DateTime>("\"2026-01-12T12:48:26+02:00\"", Options);

        read.ToUniversalTime().Should().Be(new DateTime(2026, 1, 12, 10, 48, 26, DateTimeKind.Utc));
    }

    [Fact]
    public void AnEmptyString_IsReadAsNoDate_AndDoesNotThrow()
    {
        var read = JsonSerializer.Deserialize<DateTime>("\"\"", Options);

        read.Should().Be(default(DateTime));
    }

    [Fact]
    public void ALocalTime_IsWrittenAsItsUtcInstant_WithTheZ()
    {
        var instant = new DateTime(2026, 7, 1, 10, 48, 26, DateTimeKind.Utc).AddTicks(5907775);

        var written = JsonSerializer.Serialize(instant.ToLocalTime(), Options);

        written.Should().Be("\"2026-07-01T10:48:26.5907775Z\"");
    }
}
