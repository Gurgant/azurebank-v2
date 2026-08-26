using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AzureBank.Tests.Integration;

/// <summary>
/// The anchor record against a real database — the shape rules the provider enforces, and the attack
/// the record does NOT stop, performed the way it would really be performed.
/// </summary>
/// <remarks>
/// <para>
/// THESE CANNOT RUN ON THE INMEMORY PROVIDER, and the reason is the point of the file. Tampering
/// here is an UPDATE or a DELETE issued by whoever holds a connection, straight past the change
/// tracker and every application-level guard — which is the actual threat model. The CHECK
/// constraints do not exist on InMemory at all, so a green test there would say nothing about them.
/// </para>
/// <para>
/// Gated by AZUREBANK_TEST_SQLSERVER, like every other proof in this directory.
/// </para>
/// </remarks>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class AuditAnchorSqlServerTests : IDisposable
{
    private CustomWebApplicationFactory? _factory;

    public void Dispose() => _factory?.Dispose();

    private IServiceProvider CreateSqlServices()
    {
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        _ = _factory.CreateClient(); // forces the host to build and the database to migrate
        return _factory.Services;
    }

    private static async Task ClearAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        await context.Database.ExecuteSqlRawAsync("DELETE FROM [AuditAnchors]");
        await context.Database.ExecuteSqlRawAsync("DELETE FROM [AuditEvents]");
    }

    private static async Task WriteRowsAsync(IServiceProvider services, int count)
    {
        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        for (var i = 0; i < count; i++)
        {
            context.AuditEvents.Add(new AuditEvent
            {
                Id = Guid.CreateVersion7(),
                OccurredAt = DateTime.UtcNow,
                Event = $"AnchorProof{i}",
                Outcome = AuditOutcome.Succeeded,
                ActorUserId = Guid.NewGuid(),
                RowHash = string.Empty,
            });
            await context.SaveChangesAsync();
        }
    }

    private static async Task<AuditAnchor> AnchorAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var chain = scope.ServiceProvider.GetRequiredService<IAuditChain>();
        var anchors = scope.ServiceProvider.GetRequiredService<IAuditAnchorChain>();

        var tail = await anchors.ReadTailAsync(context);
        var record = anchors.Build(await chain.VerifyAsync(context), tail, DateTime.UtcNow);
        context.Set<AuditAnchor>().Add(record);
        await context.SaveChangesAsync();
        return record;
    }

    [SqlServerFact]
    public async Task ARecordRoundTripsThroughTheDatabase_AndStillAuthenticates()
    {
        /*
          THE ROUND-TRIP PROOF, and it is not ceremony. The audit row learned this the hard way: a
          payload built on a value that renders differently once it has been through the database
          fails verification the moment it is read back, and the InMemory provider cannot see it
          because its identity map hands back the very object that was written.

          FALSIFIED by declaring PayloadVersion as nchar(8) instead of nvarchar(8): SQL Server pads
          it, the record reads back declaring "a1      ", and this reddens.
        */
        var services = CreateSqlServices();
        await ClearAsync(services);
        await WriteRowsAsync(services, 3);

        var written = await AnchorAsync(services);

        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var anchors = scope.ServiceProvider.GetRequiredService<IAuditAnchorChain>();

        var readBack = await context.Set<AuditAnchor>().AsNoTracking()
            .SingleAsync(a => a.AnchorSequence == written.AnchorSequence);

        readBack.PayloadVersion.Should().Be("a1", "padding would make this a scheme no build renders");
        readBack.CreatedAt.Ticks.Should().Be(
            written.CreatedAt.Ticks, "the payload hashes ticks, so a lossy round-trip breaks the code");
        anchors.Check(readBack).Should().Be(AuditAnchorCheck.Authentic);
    }

    [SqlServerFact]
    public async Task TamperingWithARecordInTheDATABASE_BreaksItsAuthenticationCode()
    {
        // Straight past the change tracker and the insert-only guard, which is the actual threat.
        var services = CreateSqlServices();
        await ClearAsync(services);
        await WriteRowsAsync(services, 3);
        var record = await AnchorAsync(services);

        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var anchors = scope.ServiceProvider.GetRequiredService<IAuditAnchorChain>();

        /*
          PARAMETERISED RATHER THAN CONCATENATED, and not to satisfy the analyser. EF1003 fires on the
          concatenation because it cannot know the value is a long this test just produced -- but
          reaching for a suppression teaches the next person that the warning is noise here, when the
          only reason it is noise is a fact the suppression would not record. A parameter costs
          nothing and leaves nothing to explain.

          `{0}` is the placeholder ExecuteSqlRawAsync expects: the method format-parses its SQL, which
          is also why no literal brace may appear anywhere in a string handed to it.
        */
        var affected = await context.Database.ExecuteSqlRawAsync(
            "UPDATE [AuditAnchors] SET [CoveredRowCount] = 1 WHERE [AnchorSequence] = {0}",
            record.AnchorSequence);
        affected.Should().Be(1, "ExecuteSqlRawAsync returns rows MATCHED, so this also proves the "
            + "record is there to have been matched");

        var tampered = await context.Set<AuditAnchor>().AsNoTracking()
            .SingleAsync(a => a.AnchorSequence == record.AnchorSequence);

        anchors.Check(tampered).Should().Be(AuditAnchorCheck.MacMismatch);
    }

    [SqlServerFact]
    public async Task AGapMarkerCarryingCoverage_IsRefusedByTheDatabaseItself()
    {
        /*
          THE CHECK CONSTRAINT, WHICH ONLY EXISTS HERE. The writer would never produce this record,
          but the writer is not the only thing that can INSERT -- and a marker that could carry a
          coverage claim would let a flipped Kind mint one.

          FALSIFIED by dropping CK_AuditAnchors_Shape: the insert succeeds and this reddens.
        */
        var services = CreateSqlServices();
        await ClearAsync(services);

        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();

        var act = async () => await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO [AuditAnchors] ([AnchorSequence], [PayloadVersion], [AnchorKeyId], [Kind], "
            + "[LowestCoveredSequence], [CoveredThroughSequence], [CoveredRowCount], [TailRowHash], "
            + "[VerifiedUnderChainKeyId], [PreviousAnchorPayloadHash], [AnchoredValue], "
            + "[PayloadHash], [Mac], [CreatedAt]) VALUES (1, N'a1', N'0000000000000000', "
            + "N'GapMarker', 1, 9, 9, NULL, N'0000000000000000', NULL, NULL, "
            + "N'0000000000000000000000000000000000000000000000000000000000000000', "
            + "N'0000000000000000000000000000000000000000000000000000000000000000', SYSUTCDATETIME())");

        // SqlException, not DbUpdateException: ExecuteSqlRawAsync goes straight to the server, so
        // the provider's exception arrives unwrapped. The constraint name is in the message, and
        // asserting on it is what proves WHICH rule refused rather than merely that something did.
        await act.Should().ThrowAsync<Microsoft.Data.SqlClient.SqlException>()
            .WithMessage("*CK_AuditAnchors_Shape*");
    }

    [SqlServerFact]
    public async Task ConsistentSuffixRemovalFromBOTHChains_IsNotDetected_AndThisPinsTheLimit()
    {
        /*
          THE UNCOMFORTABLE DIRECTION, ASSERTED ON PURPOSE, exactly as the row chain's truncation
          test does one layer down. Truncate the audit rows above some sequence, then delete every
          anchor that covered past it, and BOTH chains verify perfectly -- each links backwards only,
          so neither can see that it used to be longer.

          THIS IS WHY NOTHING IN THIS SLICE MAY CLAIM TO DETECT TRUNCATION. What the record buys is
          narrower: deleting anchors is LOUD, because the counter gaps and the links stop meeting,
          while MINTING one needs Audit:AnchorKey. The evidence is the pair of numbers the operator
          wrote down somewhere this machine cannot reach.

          It exists so that claim cannot quietly grow back into the documents, the way a withdrawn
          claim about this very chain already did once.
        */
        var services = CreateSqlServices();
        await ClearAsync(services);

        await WriteRowsAsync(services, 3);
        var kept = await AnchorAsync(services);
        kept.CoveredRowCount.Should().Be(3);

        await WriteRowsAsync(services, 2);
        var doomed = await AnchorAsync(services);
        doomed.CoveredRowCount.Should().Be(5);

        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var chain = scope.ServiceProvider.GetRequiredService<IAuditChain>();
        var anchors = scope.ServiceProvider.GetRequiredService<IAuditAnchorChain>();

        // The attacker truncates the audit tail, and removes the anchor that would contradict them.
        // One connection, two statements, no key of ours.
        await context.Database.ExecuteSqlRawAsync("DELETE FROM [AuditEvents] WHERE [Sequence] > 3");
        await context.Database.ExecuteSqlRawAsync("DELETE FROM [AuditAnchors] WHERE [AnchorSequence] > 1");

        var verification = await chain.VerifyAsync(context);
        verification.IsIntact.Should().BeTrue("the surviving row prefix links and hashes perfectly");
        verification.Verified.Should().Be(3);

        var survivor = await anchors.ReadTailAsync(context);
        survivor.Should().NotBeNull();
        anchors.Check(survivor!).Should().Be(
            AuditAnchorCheck.Authentic, "and the surviving record is genuine, because it is");
        survivor!.CoveredRowCount.Should().Be(
            3, "so the two agree, and NOTHING in either chain records that there was ever more");
    }
}
