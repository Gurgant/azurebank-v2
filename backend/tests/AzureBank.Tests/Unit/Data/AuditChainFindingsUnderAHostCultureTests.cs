using System.Globalization;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Shared.Options;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AzureBank.Tests.Unit.Data;

/// <summary>
/// A finding of the audit chain writes its sequence numbers the same way under every culture: digits
/// in groups of three with a comma between the groups.
/// </summary>
/// <remarks>
/// de-DE groups with a full stop and fr-FR with a narrow no-break space; the empty name is the
/// invariant culture. Every row here is refused on its sequence before its hash is read, so a
/// sequence is planted without a key. The culture is set on the test's own flow and the machine's
/// is put back.
/// </remarks>
public class AuditChainFindingsUnderAHostCultureTests : IDisposable
{
    private const string OldestKey = "culture-test-audit-chain-key-0123456789abcdef";
    private const string SecondKey = "culture-test-second-retired-key-0123456789abc";
    private const string CurrentKey = "culture-test-audit-chain-key-AFTER-rotation-98";

    private readonly string _storeName = Guid.NewGuid().ToString();
    private readonly AzureBankDbContext _context;

    public AuditChainFindingsUnderAHostCultureTests()
    {
        _context = ContextWritingWith(OldestKey);
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    [InlineData("")]
    public async Task ARowThatNamesARetiredKeyAboveItsBoundary_ReadsTheSameUnderEveryCulture(string culture)
    {
        await PlantAsync(OldestKey, row => row.Sequence = 12345);

        var reason = await ReasonUnderAsync(culture, Ring(founding: OldestKey, Retired(OldestKey, 1234)));

        reason.Should().Contain("that key was retired at sequence 1,234 and this row is sequence 12,345.");
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    [InlineData("")]
    public async Task ARowWithNoIdentityAboveTheFoundingBoundary_ReadsTheSameUnderEveryCulture(string culture)
    {
        await PlantAsync(OldestKey, row =>
        {
            row.PayloadVersion = "v2";
            row.KeyId = null;
            row.Sequence = 12345;
        });

        var reason = await ReasonUnderAsync(culture, Ring(founding: OldestKey, Retired(OldestKey, 1234)));

        reason.Should().Contain("was retired at sequence 1,234 while this row is sequence 12,345.");
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    [InlineData("")]
    public async Task ARowThatNamesAKeyBelowItsEpoch_ReadsTheSameUnderEveryCulture(string culture)
    {
        await PlantAsync(CurrentKey, row => row.Sequence = 1100);

        var reason = await ReasonUnderAsync(culture, Ring(founding: OldestKey, Retired(OldestKey, 1234)));

        reason.Should().Contain("is sequence 1,100 and names key id")
            .And.Contain("whose epoch begins at 1,235 ");
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    [InlineData("")]
    public async Task ARowWithNoIdentityBelowTheFoundingEpoch_ReadsTheSameUnderEveryCulture(string culture)
    {
        await PlantAsync(OldestKey, row =>
        {
            row.PayloadVersion = "v2";
            row.KeyId = null;
            row.Sequence = 1100;
        });

        var reason = await ReasonUnderAsync(
            culture, Ring(founding: SecondKey, Retired(OldestKey, 1234), Retired(SecondKey, 2345)));

        reason.Should().Contain("that key opens begins at 1,235, while this row is sequence 1,100.");
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    [InlineData("")]
    public async Task ARowWithNoIdentityBelowTheFirstSequence_ReadsTheSameUnderEveryCulture(string culture)
    {
        await PlantAsync(OldestKey, row =>
        {
            row.PayloadVersion = "v2";
            row.KeyId = null;
            row.Sequence = -12345;
        });

        var reason = await ReasonUnderAsync(
            culture,
            new AuditChain(Options.Create(new AuditOptions { ChainKey = OldestKey }), NullLogger<AuditChain>.Instance));

        reason.Should().Contain("row stored at sequence -12,345, which is below");
    }

    private AzureBankDbContext ContextWritingWith(string key) => new(
        new DbContextOptionsBuilder<AzureBankDbContext>().UseInMemoryDatabase(_storeName).Options,
        timeProvider: null,
        auditChain: new AuditChain(Options.Create(new AuditOptions { ChainKey = key }), NullLogger<AuditChain>.Instance));

    /// <summary>Writes one row under <paramref name="key"/>, then changes it in the store.</summary>
    private async Task PlantAsync(string key, Action<AuditEvent> change)
    {
        using (var writer = ContextWritingWith(key))
        {
            writer.AuditEvents.Add(new AuditEvent
            {
                Id = Guid.CreateVersion7(),
                OccurredAt = DateTime.UtcNow,
                Event = "Planted",
                Outcome = AuditOutcome.Succeeded,
                ActorUserId = Guid.NewGuid(),
                RowHash = string.Empty,
            });
            await writer.SaveChangesAsync();
        }

        var planted = await _context.AuditEvents.SingleAsync();
        change(planted);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private async Task<string> ReasonUnderAsync(string culture, AuditChain verifier)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            var verification = await verifier.VerifyAsync(_context);

            verification.Kind.Should().Be(AuditChainBreakKind.UnknownScheme);
            return verification.Reason!;
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static AuditChain Ring(string founding, params RetiredChainKey[] retired) => new(
        Options.Create(new AuditOptions
        {
            ChainKey = CurrentKey,
            RetiredChainKeys = [.. retired],
            FoundingChainKey = founding,
        }),
        NullLogger<AuditChain>.Instance);

    private static RetiredChainKey Retired(string key, long lastSequence) =>
        new() { Key = key, LastSequence = lastSequence };
}
