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
          it, the record reads back with trailing blanks hung off the version, and this reddens.

          The transcript quoted "a1      " while that was the current version, and kept quoting it
          after it was not. Padding is the finding here and the version is incidental to it, so the
          sentence no longer names one -- which also matches the assertion below, which reads the
          constant rather than a literal for the same reason.
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

        readBack.PayloadVersion.Should().Be(
            AuditAnchorChain.CurrentPayloadVersion,
            "padding would make this a scheme no build renders. Read from the constant, not from a "
            + "literal: this assertion is about the round trip through nchar, not about which "
            + "version is current, and the literal turned it into a second place the version lives");
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
    public async Task AnInteriorRecordRemoved_IsCaughtByWalkingTheWholeChain()
    {
        /*
          THE PROPERTY THE WHOLE TABLE EXISTS FOR, and until the walk was written the claim had no
          mechanism under it. Authenticating only the newest record leaves an interior deletion
          invisible, because the survivor verifies perfectly well on its own -- so a later run would
          extend a chain with a hole in it, and its link would assert that everything beneath was
          fine.

          FALSIFIED by checking only the tail instead of walking: every surviving record still
          authenticates, the run reports intact, and this reddens.
        */
        var services = CreateSqlServices();
        await ClearAsync(services);

        await WriteRowsAsync(services, 1);
        await AnchorAsync(services);
        await WriteRowsAsync(services, 1);
        var second = await AnchorAsync(services);
        await WriteRowsAsync(services, 1);
        var third = await AnchorAsync(services);

        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var anchors = scope.ServiceProvider.GetRequiredService<IAuditAnchorChain>();

        (await anchors.VerifyChainAsync(context)).IsIntact.Should().BeTrue("three, in order");

        var affected = await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM [AuditAnchors] WHERE [AnchorSequence] = {0}", second.AnchorSequence);
        affected.Should().Be(1);

        var state = await anchors.VerifyChainAsync(context);

        state.IsIntact.Should().BeFalse();
        state.Kind.Should().Be(AuditAnchorChainBreakKind.MissingRecord);
        state.FirstBrokenSequence.Should().Be(second.AnchorSequence);
        state.Verified.Should().Be(1, "the record before the hole verified, and nothing after it did");

        var survivor = await context.Set<AuditAnchor>().AsNoTracking()
            .SingleAsync(a => a.AnchorSequence == third.AnchorSequence);
        anchors.Check(survivor).Should().Be(
            AuditAnchorCheck.Authentic,
            "and the survivor is genuine -- which is exactly why checking it alone proves nothing");
    }

    [SqlServerFact]
    public async Task AnUnauthenticRecordStopsTheWalkWhereItIS_NotAtTheTail()
    {
        var services = CreateSqlServices();
        await ClearAsync(services);

        await WriteRowsAsync(services, 1);
        var first = await AnchorAsync(services);
        await WriteRowsAsync(services, 1);
        await AnchorAsync(services);

        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var anchors = scope.ServiceProvider.GetRequiredService<IAuditAnchorChain>();

        await context.Database.ExecuteSqlRawAsync(
            "UPDATE [AuditAnchors] SET [CoveredRowCount] = 99 WHERE [AnchorSequence] = {0}",
            first.AnchorSequence);

        var state = await anchors.VerifyChainAsync(context);

        state.Kind.Should().Be(AuditAnchorChainBreakKind.Unauthentic);
        state.FirstBrokenSequence.Should().Be(
            first.AnchorSequence, "the walk reports where the break IS, not where it noticed");
        state.Verified.Should().Be(0);
    }

    [SqlServerFact]
    public async Task RewritingAStoredPayloadHashInTheDATABASE_IsCaught()
    {
        /*
          The one derived value the authentication code cannot cover, attacked where it would really
          be attacked. PayloadHash is a hash OF the payload, so it cannot be an element of it -- the
          code verifies with this column set to anything at all, and the NEXT record links to it.

          FALSIFIED by removing the Sha256 comparison from Check.
        */
        var services = CreateSqlServices();
        await ClearAsync(services);
        await WriteRowsAsync(services, 2);
        var record = await AnchorAsync(services);

        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var anchors = scope.ServiceProvider.GetRequiredService<IAuditAnchorChain>();

        await context.Database.ExecuteSqlRawAsync(
            "UPDATE [AuditAnchors] SET [PayloadHash] = {0} WHERE [AnchorSequence] = {1}",
            new string('a', 64), record.AnchorSequence);

        var tampered = await context.Set<AuditAnchor>().AsNoTracking()
            .SingleAsync(a => a.AnchorSequence == record.AnchorSequence);

        anchors.Check(tampered).Should().Be(AuditAnchorCheck.MacMismatch);
        (await anchors.VerifyChainAsync(context)).Kind.Should()
            .Be(AuditAnchorChainBreakKind.Unauthentic);
    }

    [SqlServerFact]
    public async Task DeletingAnchorsIsLoudONLYINTHEINTERIOR_ANDASUFFIXISSILENT()
    {
        /*
          THE HALF OF "DELETING RECORDS IS LOUD" THAT IS NOT TRUE, asserted so the sentence cannot go
          on being repeated whole. Eight places in this repository said it without the qualifier,
          including one written the same day this test was.

          VerifyChainAsync walks in AnchorSequence order against an expectedSequence counter, checks
          the MAC, and checks that each record links to the previous payload hash. Remove a record
          from the MIDDLE and both fire: the counter finds a gap where it expected the missing
          number, and the record after it points at a payload hash that is no longer there. Remove a
          SUFFIX and neither can: the survivors are 1..n with every link met, and nothing in the walk
          asks how tall the chain ought to be. It returns intact.

          That is the same shape as the row chain's own limit one layer down, and it is why the
          anchors alone cannot close truncation: the attack is a suffix removal in both tables, which
          the test below this one performs in full.

          FALSIFIED by asserting the suffix case reports broken -- it does not, and pretending it
          does is the claim this pins.
        */
        var services = CreateSqlServices();
        await ClearAsync(services);
        await WriteRowsAsync(services, 3);
        await AnchorAsync(services);
        await WriteRowsAsync(services, 3);
        await AnchorAsync(services);
        await WriteRowsAsync(services, 3);
        var third = await AnchorAsync(services);

        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var anchors = scope.ServiceProvider.GetRequiredService<IAuditAnchorChain>();

        // The INTERIOR first: remove record 2 of 3 and the walk must name it.
        var interior = await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM [AuditAnchors] WHERE [AnchorSequence] = {0}", third.AnchorSequence - 1);
        interior.Should().Be(1);

        var afterInterior = await anchors.VerifyChainAsync(context);
        afterInterior.IsIntact.Should().BeFalse(
            "a gap in the counter and a link that no longer meets are exactly what the walk checks");

        // Now the SUFFIX: put the chain back to 1..2 by removing the highest, and it goes quiet.
        await ClearAsync(services);
        await WriteRowsAsync(services, 3);
        await AnchorAsync(services);
        await WriteRowsAsync(services, 3);
        await AnchorAsync(services);
        await WriteRowsAsync(services, 3);
        var newest = await AnchorAsync(services);

        var suffix = await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM [AuditAnchors] WHERE [AnchorSequence] >= {0}", newest.AnchorSequence);
        suffix.Should().Be(1, "one record removed from the top, nothing else touched");

        var afterSuffix = await anchors.VerifyChainAsync(context);
        afterSuffix.IsIntact.Should().BeTrue(
            "the survivors are 1..n with every link met, and nothing asks how tall the chain was");
        afterSuffix.Records.Should().Be(2, "the walk reports what it read, not what used to be there");
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
          narrower: deleting an INTERIOR anchor is LOUD, because the counter gaps and the links
          stop meeting -- a SUFFIX removal is silent, which the test above measures --
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
