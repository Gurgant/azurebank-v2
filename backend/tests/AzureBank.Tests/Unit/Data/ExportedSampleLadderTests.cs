using System.Globalization;
using System.Reflection;
using System.Text.Json;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Shared.Options;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AzureBank.Tests.Unit.Data;

/// <summary>
/// The anchor payload ladder, checked against records this repository actually shipped.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ A VERSION BUMP ON THIS LADDER IS A ONE-WAY DOOR, AND NOTHING GUARDED IT. <c>Check</c> gates on
/// the payload version before it renders anything; a record under an older version comes back
/// <c>UnknownScheme</c>, which the anchor walk reports as a break at record ONE — and
/// <c>anchor</c> refuses to append over a broken chain, with no <c>--force</c> to get past it. So
/// bumping <c>CurrentPayloadVersion</c> without widening the gate does not degrade anything: it
/// bricks anchoring on every deployment that has ever taken an anchor.
/// </para>
/// <para>
/// Every test in the suite writes its fixture under the CURRENT version, so not one of them could
/// see that. This one reads <c>docs/audit/anchors.sample.jsonl</c> — four records produced by
/// <c>ExportCommand</c> itself under <c>a1</c>, with both keys published in
/// <c>docs/audit/README.md</c> — which makes it the only place an older scheme is exercised at all.
/// </para>
/// <para>
/// 🔒 DO NOT REGENERATE THE SAMPLE. Re-exporting it under the current version would delete the only
/// legacy artifact in the tree and turn this test green forever by removing what it checks. If the
/// documentation needs a fresher illustration, add a second file and leave this one alone.
/// </para>
/// </remarks>
public class ExportedSampleLadderTests
{
    private const string SampleChainKey = "azurebank-sample-chain-key-published-in-this-repo";
    private const string SampleAnchorKey = "azurebank-sample-anchor-key-published-in-this-repo";

    [Fact]
    public void EveryRecordInTheCommittedSample_StillAuthenticatesUnderThePublishedKey()
    {
        var anchors = new AuditAnchorChain(Options.Create(new AuditOptions
        {
            ChainKey = SampleChainKey,
            AnchorKey = SampleAnchorKey,
        }));

        var records = SampleRecords();

        records.Should().HaveCount(
            4,
            "the sample is one gap marker over an empty table and three anchors, and a test that "
            + "silently reads zero lines would pass while checking nothing");
        records.Should().Contain(
            r => r.PayloadVersion == AuditAnchorChain.LegacyPayloadVersion,
            "this fixture exists to exercise the OLDER payload version; if every record here is "
            + "current, the sample was regenerated and the ladder is unguarded again");

        foreach (var record in records)
        {
            anchors.Check(record).Should().Be(
                AuditAnchorCheck.Authentic,
                "record {0} was written under '{1}' by the exporter itself and its bytes have not "
                + "changed, so a build that cannot authenticate it has moved the ladder without "
                + "carrying the old rung -- which brings anchoring down rather than degrading it",
                record.AnchorSequence,
                record.PayloadVersion);
        }
    }

    private static List<AuditAnchor> SampleRecords()
    {
        var path = Path.Combine(RepoRoot().FullName, "docs", "audit", "anchors.sample.jsonl");

        File.Exists(path).Should().BeTrue(
            "the sample is the fixture; a missing file must fail loudly rather than read as zero "
            + "records that all authenticate");

        return File.ReadAllLines(path)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => Hydrate(JsonDocument.Parse(line).RootElement))
            .ToList();
    }

    /*
      REBUILT FROM THE EXPORTED FIELDS, NOT FROM A BUILD. Check re-renders the payload from the
      values it is handed, so what matters is that each one arrives exactly as it was written --
      CreatedAt above all, because the payload hashes TICKS. The exporter writes "O", so it is
      parsed round-trip; a lossy parse here would fail every record and read like a broken ladder.
    */
    private static AuditAnchor Hydrate(JsonElement e) => new()
    {
        AnchorSequence = e.GetProperty("anchorSequence").GetInt64(),
        PayloadVersion = e.GetProperty("payloadVersion").GetString()!,
        Kind = Enum.Parse<AuditAnchorKind>(e.GetProperty("kind").GetString()!),
        AnchorKeyId = e.GetProperty("anchorKeyId").GetString()!,
        VerifiedUnderChainKeyId = e.GetProperty("verifiedUnderChainKeyId").GetString()!,
        LowestCoveredSequence = Int64OrNull(e, "lowestCoveredSequence"),
        CoveredThroughSequence = Int64OrNull(e, "coveredThroughSequence"),
        CoveredRowCount = Int64OrNull(e, "coveredRowCount"),
        TailRowHash = StringOrNull(e, "tailRowHash"),
        AnchoredValue = StringOrNull(e, "anchoredValue"),
        PreviousAnchorPayloadHash = StringOrNull(e, "previousAnchorPayloadHash"),
        PayloadHash = e.GetProperty("payloadHash").GetString()!,
        Mac = e.GetProperty("mac").GetString()!,
        CreatedAt = DateTime.Parse(
            e.GetProperty("createdAt").GetString()!,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind),
    };

    private static long? Int64OrNull(JsonElement e, string name) =>
        e.GetProperty(name).ValueKind == JsonValueKind.Null ? null : e.GetProperty(name).GetInt64();

    private static string? StringOrNull(JsonElement e, string name) =>
        e.GetProperty(name).ValueKind == JsonValueKind.Null ? null : e.GetProperty(name).GetString();

    private static DirectoryInfo RepoRoot()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".github")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull(
            "this test reads a committed document; one that cannot run must say so rather than pass");
        return dir!;
    }
}
