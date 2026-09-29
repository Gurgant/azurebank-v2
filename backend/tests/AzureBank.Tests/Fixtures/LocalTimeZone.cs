using System.Reflection;

namespace AzureBank.Tests.Fixtures;

/// <summary>
/// Puts this process in a local time zone the test chooses, until disposed, so a proof about
/// local-time conversion runs the same on every machine: CI's runners are UTC, where converting a
/// value "as local time" changes nothing and a missing conversion cannot be seen.
/// </summary>
/// <remarks>
/// <para>
/// .NET has no public way to set <see cref="TimeZoneInfo.Local"/>. It is read once and cached — from
/// <c>TZ</c> or <c>/etc/localtime</c> on Linux, from the system on Windows — so this writes the cache
/// itself, a private field of <see cref="TimeZoneInfo"/> found by name, and
/// <see cref="TimeZoneInfo.ClearCachedData"/> drops it again on dispose. If a .NET update renames the
/// field, <see cref="Use"/> throws instead of leaving the machine's zone in place: a proof that ran in
/// UTC without saying so would prove nothing.
/// </para>
/// <para>
/// Process-wide: only for tests in <see cref="LocalTimeZoneCollection"/>, which xUnit runs with
/// nothing else beside them.
/// </para>
/// </remarks>
public sealed class LocalTimeZone : IDisposable
{
    private LocalTimeZone()
    {
    }

    /// <summary>Seven hours west of UTC, with no daylight saving time: the same on every machine.</summary>
    public static TimeZoneInfo SevenHoursWestOfUtc { get; } = TimeZoneInfo.CreateCustomTimeZone(
        "AzureBank.Tests.UTC-7", TimeSpan.FromHours(-7), "UTC-07:00 (tests)", "UTC-07:00 (tests)");

    /// <summary>Makes <paramref name="zone"/> the process's local zone until the result is disposed.</summary>
    public static LocalTimeZone Use(TimeZoneInfo zone)
    {
        TimeZoneInfo.ClearCachedData();
        var cachedData = typeof(TimeZoneInfo).GetField("s_cachedData", BindingFlags.NonPublic | BindingFlags.Static)
            ?.GetValue(null);
        var localZone = cachedData?.GetType().GetField("_localTimeZone", BindingFlags.NonPublic | BindingFlags.Instance);
        if (localZone is null)
        {
            throw new InvalidOperationException(
                "TimeZoneInfo no longer caches the local zone in s_cachedData._localTimeZone; "
                + "LocalTimeZone cannot set it, and a test that needs it must not run in the machine's zone.");
        }

        localZone.SetValue(cachedData, zone);
        if (TimeZoneInfo.Local.Id != zone.Id)
        {
            TimeZoneInfo.ClearCachedData();
            throw new InvalidOperationException($"The local zone is still {TimeZoneInfo.Local.Id}, not {zone.Id}.");
        }

        return new LocalTimeZone();
    }

    public void Dispose() => TimeZoneInfo.ClearCachedData();
}

/// <summary>
/// The tests that change the process's local time zone (<see cref="LocalTimeZone"/>). xUnit runs a
/// collection that disables parallelization after the others, with nothing beside it, so no other
/// test reads a local time while the zone is not the machine's.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LocalTimeZoneCollection
{
    public const string Name = "LocalTimeZone";
}
