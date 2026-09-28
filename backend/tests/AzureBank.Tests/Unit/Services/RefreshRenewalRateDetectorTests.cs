using AzureBank.Api.Services;
using AzureBank.Shared.Constants;
using AzureBank.Shared.Options;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzureBank.Tests.Unit.Services;

/// <summary>
/// The renewal-rate detector of 06 §6, anomaly 2, on its own: more than three accepted renewals of
/// one grant within one access-token lifetime raise one <c>RefreshRenewalRateHigh</c>, three or fewer
/// raise nothing, a grant raises at most once per lifetime, and its memory is bounded: entries
/// expire, and the map has a capacity.
/// </summary>
/// <remarks>
/// The stamps are the requests' <c>ReceivedAt</c> values, which the detector counts on, so these
/// tests move time by choosing them; no clock is involved. That it refuses nothing and writes
/// nothing through the real host is <c>RenewalRateDetectorTests</c> and
/// <c>RefreshTokenRotationSqlServerTests.FourRenewalsThatRaiseTheRateEvent_SendNoWriteCommand…</c>.
/// </remarks>
public sealed class RefreshRenewalRateDetectorTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);

    private readonly RecordingLoggerProvider _logs = new();
    private readonly LoggerFactory _loggerFactory;

    public RefreshRenewalRateDetectorTests()
    {
        _loggerFactory = new LoggerFactory([_logs]);
    }

    public void Dispose() => _loggerFactory.Dispose();

    /// <summary>A detector over 15-minute access tokens unless told otherwise.</summary>
    private RefreshRenewalRateDetector Build(int expirationMinutes = 15, int? capacity = null)
    {
        var options = Options.Create(new JwtOptions { ExpirationMinutes = expirationMinutes });
        var logger = _loggerFactory.CreateLogger<RefreshRenewalRateDetector>();
        return capacity is { } max
            ? new RefreshRenewalRateDetector(options, logger, max)
            : new RefreshRenewalRateDetector(options, logger);
    }

    private IReadOnlyList<string> Events =>
        _logs.Lines
            .Where(l => l.Level == LogLevel.Warning && l.Message.Contains(SecurityEvents.RefreshRenewalRateHigh))
            .Select(l => l.Message)
            .ToList();

    [Theory]
    [InlineData(2, 0)]
    [InlineData(3, 0)]
    [InlineData(4, 1)]
    public void RenewalsOfOneGrantWithinOneTokenLifetime_RaiseTheEventOnlyAboveThree(int renewals, int expected)
    {
        // 06 §10 O2k: 4 renewals raise one event, 2 raise none; 3 is the limit itself, so none.
        var detector = Build();
        var grant = Guid.NewGuid();
        var user = Guid.NewGuid();

        for (var i = 0; i < renewals; i++)
        {
            detector.Record(grant, user, T0.AddMinutes(i));
        }

        Events.Should().HaveCount(expected);
        if (expected == 1)
        {
            Events[0].Should().Contain(grant.ToString()).And.Contain(user.ToString(),
                "the line names the grant's row and its user, so an operator can find both");
            Events[0].Should().StartWith($"SecurityEvent {SecurityEvents.RefreshRenewalRateHigh}:");
        }
    }

    [Theory]
    [InlineData(450)] // 7.5 minutes: the window never holds more than 2
    [InlineData(449)] // a second early: the window holds 3 at the third renewal, and again after
    public void TheLegitimateBffsCadence_RaisesNothing_OverTheGrantsWholeLife(int secondsApart)
    {
        /*
          The BFF renews when less than half the token's life is left (06 §4.5): every 7.5 minutes
          for 15-minute tokens, 9 renewals over a 60-minute grant. The token's exp is whole seconds,
          so a renewal can come a little under 7.5 minutes after the last (reasoned, not measured on
          the BFF); at 449 s one window holds three renewals, which the limit of 3 allows.
        */
        var detector = Build();
        var grant = Guid.NewGuid();

        for (var seconds = 0; seconds <= 3600; seconds += secondsApart)
        {
            detector.Record(grant, Guid.NewGuid(), T0.AddSeconds(seconds));
        }

        Events.Should().BeEmpty();
    }

    [Fact]
    public void AGrantRaisesAtMostOncePerTokenLifetime()
    {
        // A copy that renews every minute for the grant's 60 minutes: 61 renewals, and one event per
        // lifetime, at minutes 3, 18, 33 and 48. One per request would be 58 lines.
        var detector = Build();
        var grant = Guid.NewGuid();

        for (var minute = 0; minute <= 60; minute++)
        {
            detector.Record(grant, Guid.NewGuid(), T0.AddMinutes(minute));
        }

        Events.Should().HaveCount(4);
    }

    [Fact]
    public void ABurstAfterAQuietLifetime_RaisesAgain()
    {
        var detector = Build();
        var grant = Guid.NewGuid();
        var user = Guid.NewGuid();

        for (var minute = 0; minute < 6; minute++)
        {
            detector.Record(grant, user, T0.AddMinutes(minute));
        }

        Events.Should().ContainSingle("the fifth and sixth renewals fall in the window that already raised");

        for (var minute = 20; minute < 24; minute++)
        {
            detector.Record(grant, user, T0.AddMinutes(minute));
        }

        Events.Should().HaveCount(2, "a lifetime has passed since the first event, and the window holds four again");
    }

    [Fact]
    public void EachGrantIsCountedOnItsOwn()
    {
        // Two sessions of one user, two renewals each: four renewals, but no grant renewed four times.
        var detector = Build();
        var user = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        for (var i = 0; i < 2; i++)
        {
            detector.Record(first, user, T0.AddMinutes(i));
            detector.Record(second, user, T0.AddMinutes(i));
        }

        Events.Should().BeEmpty();
    }

    [Fact]
    public void StampsThatArriveOutOfOrder_AreCountedByWhenTheyWereReceived()
    {
        // Two requests can reach the detector in the opposite order to their ReceivedAt stamps.
        var detector = Build();
        var grant = Guid.NewGuid();

        foreach (var minute in new[] { 3, 1, 2, 0 })
        {
            detector.Record(grant, Guid.NewGuid(), T0.AddMinutes(minute));
        }

        Events.Should().ContainSingle("four stamps within 15 minutes, in whatever order they arrived");

        var other = Guid.NewGuid();
        foreach (var minute in new[] { 20, 21, 22, 1 })
        {
            detector.Record(other, Guid.NewGuid(), T0.AddMinutes(minute));
        }

        Events.Should().ContainSingle("a stamp older than the window of the newest does not count in it");
    }

    [Fact]
    public void ALifetimeOfZero_IsCountedOverOneMinute()
    {
        var detector = Build(expirationMinutes: 0);
        var grant = Guid.NewGuid();

        for (var second = 0; second < 4; second++)
        {
            detector.Record(grant, Guid.NewGuid(), T0.AddSeconds(second * 10));
        }

        Events.Should().ContainSingle("a window of zero would drop every stamp it had just added");
    }

    // ── Memory ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Entries_AreSweptOnceTheirLastRenewalIsOneLifetimeOld()
    {
        /*
          A sweep runs on a renewal, at most once per lifetime. Here the first renewal, at T0, sweeps
          and sets the next sweep for 15:00; the one at 15:01 sweeps the 500 grants of T0 and sets
          the next for 30:01. So the grant last renewed at 14:00 is still held at 29:30, more than a
          lifetime after its last renewal, and goes at 30:02: an entry is held for up to about two
          lifetimes, never longer while renewals keep coming.
        */
        var detector = Build();
        var user = Guid.NewGuid();
        var stale = Enumerable.Range(0, 500).Select(_ => Guid.NewGuid()).ToList();
        foreach (var grant in stale)
        {
            detector.Record(grant, user, T0);
        }

        var live = Guid.NewGuid();
        detector.Record(live, user, T0.AddMinutes(14));
        detector.TrackedGrants.Should().Be(501, "every grant renewed within the last lifetime is held");

        detector.Record(Guid.NewGuid(), user, T0.AddMinutes(15).AddSeconds(1));

        detector.TrackedGrants.Should().Be(2,
            "the 500 grants last renewed more than a lifetime ago are gone; the one renewed at minute 14 "
            + "and the one just renewed are held");

        detector.Record(Guid.NewGuid(), user, T0.AddMinutes(29).AddSeconds(30));
        detector.TrackedGrants.Should().Be(3, "no sweep is due before 30:01, so the grant of 14:00 is still held");

        detector.Record(Guid.NewGuid(), user, T0.AddMinutes(30).AddSeconds(2));
        detector.TrackedGrants.Should().Be(2,
            "the sweep at 30:02 drops the grants of 14:00 and 15:01; those of 29:30 and 30:02 are held");
    }

    [Fact]
    public void AnExpiredEntry_CountsFromZeroAgain()
    {
        // Three renewals, then silence for a lifetime: the entry is swept, and one more renewal is
        // the first of a new count, not the fourth of the old one.
        var detector = Build();
        var grant = Guid.NewGuid();
        var user = Guid.NewGuid();
        for (var minute = 0; minute < 3; minute++)
        {
            detector.Record(grant, user, T0.AddMinutes(minute));
        }

        detector.Record(grant, user, T0.AddMinutes(20));

        Events.Should().BeEmpty();
        detector.TrackedGrants.Should().Be(1);
    }

    [Fact]
    public void AFullMap_CountsNoNewGrant_SaysSoOncePerLifetime_AndMakesRoomAsEntriesExpire()
    {
        var detector = Build(capacity: 3);
        var user = Guid.NewGuid();
        for (var i = 0; i < 3; i++)
        {
            detector.Record(Guid.NewGuid(), user, T0);
        }

        var late = Guid.NewGuid();
        for (var minute = 1; minute <= 4; minute++)
        {
            detector.Record(late, user, T0.AddMinutes(minute));
        }

        detector.TrackedGrants.Should().Be(3, "the map never holds more than its capacity");
        _logs.Lines.Count(l => l.Level == LogLevel.Warning && l.Message.Contains("its capacity"))
            .Should().Be(1, "a full map is reported, once per lifetime, not once per renewal");
        _logs.Lines.Should().Contain(l => l.Message.Contains(late.ToString()),
            "the warning names the grant it could not count");
        Events.Should().BeEmpty("the grant that did not fit was not counted: the blind spot the warning names");

        detector.Record(late, user, T0.AddMinutes(16));

        detector.TrackedGrants.Should().Be(1, "the three grants renewed at T0 have expired and made room");
    }
}
