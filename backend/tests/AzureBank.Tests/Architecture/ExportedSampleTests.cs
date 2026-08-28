using System.Reflection;
using System.Text.Json;
using FluentAssertions;

namespace AzureBank.Tests.Architecture;

/// <summary>
/// Keeps the committed anchor export honest about what it shows a reader.
/// </summary>
/// <remarks>
/// <para>
/// <c>docs/audit/anchors.sample.jsonl</c> is the one artefact of this work a reader on GitHub can
/// actually inspect, and a sample that has quietly stopped matching what the tool writes is worse
/// than no sample: it teaches the format wrong, and it is the file somebody would copy.
/// </para>
/// <para>
/// ⚠️ THIS DOES NOT COMPARE THE FILE TO A STORED COPY OF ITSELF. A byte-for-byte golden would pass
/// for a file that is internally nonsense as long as nobody touched it, and it would fail for a
/// regenerated one for reasons — a fresh GUID, a different clock — that say nothing about the format.
/// So every assertion below is a property the file must have on its own terms, and the chain check
/// in particular is exactly the check a reader with no key can run by hand. That is the same reason
/// <c>PublishedErrorContractTests</c> reads the committed OpenAPI document and asserts properties of
/// it rather than diffing it.
/// </para>
/// </remarks>
public class ExportedSampleTests
{
    /// <summary>
    /// Below this the scan is not reporting a clean sample, it is reporting that it found almost
    /// nothing to read. The file is generated with a gap marker and three anchors; the floor is set
    /// under that on purpose, so regenerating with a different number of rounds does not fail a
    /// guard that is about content rather than about size.
    /// </summary>
    private const int Floor = 3;

    private static readonly string[] RequiredFields =
    [
        "anchorSequence", "payloadVersion", "kind", "anchorKeyId", "verifiedUnderChainKeyId",
        "lowestCoveredSequence", "coveredThroughSequence", "coveredRowCount", "tailRowHash",
        "anchoredValue", "previousAnchorPayloadHash", "payloadHash", "mac", "createdAt",
    ];

    private static string SamplePath()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".github")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull(
            because: "the guard needs the committed sample; one that cannot run must fail loudly");

        var path = Path.Combine(dir!.FullName, "docs", "audit", "anchors.sample.jsonl");
        File.Exists(path).Should().BeTrue(because: $"the exported sample is expected at {path}");
        return path;
    }

    private static JsonElement[] Records()
    {
        var lines = File.ReadAllText(SamplePath()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Length.Should().BeGreaterThanOrEqualTo(
            Floor, "fewer than this and the scan is reporting that it found nothing, not that it is clean");
        return [.. lines.Select(l => JsonDocument.Parse(l).RootElement.Clone())];
    }

    [Fact]
    public void Every_line_is_one_complete_record()
    {
        /*
          THE UNIT OF THIS FILE IS THE LINE, and that is the property `diff` depends on. A record
          spread over several lines, or a field quietly dropped from the serialiser, breaks the
          comparison the file exists to make possible -- and breaks it silently, because the file
          still parses.

          FALSIFIED by turning on WriteIndented in ExportCommand and regenerating: the first line is
          an unterminated fragment and JsonDocument.Parse throws.
        */
        foreach (var record in Records())
        {
            foreach (var field in RequiredFields)
            {
                record.TryGetProperty(field, out _).Should().BeTrue(
                    because: $"a reader learns the format from this file, and {field} is part of it");
            }
        }
    }

    [Fact]
    public void The_sample_is_a_chain_a_reader_can_follow_without_any_key()
    {
        /*
          THE ONE CHECK THE ARTEFACT EXISTS FOR. previousAnchorPayloadHash on each record equals
          payloadHash on the record before it, and both are plain SHA-256 over values printed on the
          same lines -- so somebody holding none of this project's secrets can verify that the file
          describes one unbroken chain. That is what makes it evidence of a shape rather than a
          screenshot of one.

          The first record chains to nothing, which is not a gap: the table starts empty and no
          genesis record is synthesised, because a synthetic first row would be a claim about a
          moment that never happened.

          FALSIFIED by deleting any interior line of the sample: the link at the seam stops meeting
          and this reddens naming the sequence.
        */
        var records = Records();

        records[0].GetProperty("previousAnchorPayloadHash").ValueKind.Should().Be(
            JsonValueKind.Null, "nothing precedes the first record, and nothing invents a genesis");

        for (var i = 1; i < records.Length; i++)
        {
            records[i].GetProperty("anchorSequence").GetInt64().Should().Be(
                records[i - 1].GetProperty("anchorSequence").GetInt64() + 1,
                "the counter is gapless, which is what makes a removed record loud");

            records[i].GetProperty("previousAnchorPayloadHash").GetString().Should().Be(
                records[i - 1].GetProperty("payloadHash").GetString(),
                $"record {i + 1} must link to record {i}, and a reader checks this by eye");
        }
    }

    [Fact]
    public void A_gap_marker_in_the_sample_covers_NOTHING_and_says_so_in_nulls()
    {
        /*
          The sample opens with a marker on purpose: it is the shape a reader is most likely to
          misread. A marker asserts coverage of nothing, so its coverage fields are null -- and a
          serialiser that wrote zeros instead would produce a record that reads as an anchor covering
          sequence 0, a claim no run ever made.

          FALSIFIED by regenerating with `?? 0` on the coverage fields: they come back as numbers and
          this reddens.
        */
        var markers = Records()
            .Where(r => r.GetProperty("kind").GetString() == "GapMarker")
            .ToArray();

        markers.Should().NotBeEmpty("the sample is generated with one, so a reader sees both shapes");

        foreach (var marker in markers)
        {
            marker.GetProperty("lowestCoveredSequence").ValueKind.Should().Be(JsonValueKind.Null);
            marker.GetProperty("coveredThroughSequence").ValueKind.Should().Be(JsonValueKind.Null);
            marker.GetProperty("coveredRowCount").ValueKind.Should().Be(JsonValueKind.Null);
            marker.GetProperty("tailRowHash").ValueKind.Should().Be(JsonValueKind.Null);
        }
    }

    [Fact]
    public void An_anchor_in_the_sample_carries_the_pair_that_cannot_be_regrown()
    {
        /*
          The evidence an anchor produces is the PAIR -- the counter and the sequence it covers --
          because a bare counter can be regrown by re-running the command until it reaches whatever
          number is on the operator's paper, with genuine authentication codes every step of the way.
          The covered sequence cannot be regrown downward. A sample that showed only counters would
          teach the half that is worthless on its own.

          FALSIFIED by dropping coveredThroughSequence from ExportedAnchor and regenerating.
        */
        var anchors = Records()
            .Where(r => r.GetProperty("kind").GetString() == "Anchor")
            .ToArray();

        anchors.Should().NotBeEmpty();

        foreach (var anchor in anchors)
        {
            anchor.GetProperty("coveredThroughSequence").GetInt64().Should().BePositive();
            anchor.GetProperty("coveredRowCount").GetInt64().Should().BePositive();
            anchor.GetProperty("tailRowHash").GetString().Should().NotBeNullOrWhiteSpace(
                "the tail hash and the covered sequence describe the same endpoint two ways");
        }
    }

    [Fact]
    public void The_committed_bytes_are_what_the_tool_writes_on_any_platform()
    {
        /*
          This clone has core.autocrlf=true and the repository has no root .gitattributes, so a file
          written with the platform separator is CRLF on a Windows checkout and LF in CI. In a format
          whose only signal is WHICH LINES DIFFER, that is a difference on every line for a reason
          that has nothing to do with the audit trail -- and it would land in the diff of any commit
          that regenerated the sample from the other platform.

          FALSIFIED by committing a CRLF copy of the same records: the CR assertion reddens.
        */
        var bytes = File.ReadAllBytes(SamplePath());

        bytes.Take(3).Should().NotEqual(
            [0xEF, 0xBB, 0xBF], "a JSON Lines file starts at its first '{'");
        bytes.Should().NotContain((byte)'\r', "CRLF would make every line differ across platforms");
        bytes[^1].Should().Be((byte)'\n', "every record is terminated, including the last");
    }
}
