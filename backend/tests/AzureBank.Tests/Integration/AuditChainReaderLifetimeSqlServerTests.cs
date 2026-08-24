using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace AzureBank.Tests.Integration;

/// <summary>
/// The walk must leave the connection usable when it stops early.
/// </summary>
/// <remarks>
/// <para>
/// <c>VerifyAsync</c> used to be an <c>await foreach</c>, which the compiler lowers to a try/finally
/// that disposes the enumerator on every exit. It was rewritten to drive the enumerator by hand so a
/// row that will not materialise could be caught and reported as a break -- and that rewrite dropped
/// the finally. Every early return, which is to say every BROKEN verdict, then left EF's
/// <c>DbDataReader</c> open on the context's connection.
/// </para>
/// <para>
/// SQL-gated because the InMemory provider has no reader to leave open: the sibling test that walks
/// a broken chain and then counts rows passes there whatever this code does. On SQL Server the next
/// query on that context is the one that pays.
/// </para>
/// </remarks>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class AuditChainReaderLifetimeSqlServerTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private CustomWebApplicationFactory? _factory;

    public AuditChainReaderLifetimeSqlServerTests(ITestOutputHelper output) => _output = output;

    [SqlServerFact]
    public async Task AfterABROKENVerdict_TheSameContextCanStillQuery()
    {
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        _ = _factory.CreateClient();

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var chain = scope.ServiceProvider.GetRequiredService<IAuditChain>();

        var mine = new List<Guid>();

        // Recorded so the cleanup can be CHECKED rather than assumed. This test tampers with a row
        // in a database other tests share, and an append-only chain has no undo.
        var before = await context.AuditEvents.CountAsync();

        try
        {
            for (var i = 0; i < 3; i++)
            {
                var row = new AuditEvent
                {
                    Id = Guid.CreateVersion7(), OccurredAt = DateTime.UtcNow, Event = "ReaderLifetime",
                    Outcome = AuditOutcome.Succeeded, ActorUserId = Guid.NewGuid(), RowHash = string.Empty,
                };
                context.AuditEvents.Add(row);
                await context.SaveChangesAsync();
                mine.Add(row.Id);
            }

            /*
              BY ID, NOT "the newest row in the table". Those coincide only while nothing else is
              writing, and the difference is not a flaky test: the cleanup below removes only the
              rows this test inserted, so tampering with somebody else's row would leave the shared
              database's chain permanently broken, with no undo. What the test MEANS is "my last
              row", so that is what it now says.

              The id is hoisted into a local because an EF expression tree cannot contain a
              from-end index: `e.Id == mine[^1]` is CS8790/CS8791 and does not build.
            */
            var lastOfMine = mine[^1];
            var tampered = await context.AuditEvents.SingleAsync(e => e.Id == lastOfMine);
            tampered.Event = "ReaderLifetimeTampered";
            await context.SaveChangesAsync();

            var verification = await chain.VerifyAsync(context);
            verification.IsIntact.Should().BeFalse("the last row was altered, so the walk stops early");

            /*
              THE ASSERTION IS THE NEXT QUERY, not the verdict. An undisposed reader shows up here as
              "There is already an open DataReader associated with this Connection which must be
              closed first" -- and nowhere else, which is why nothing caught this until a reviewer
              read the rewrite.
            */
            var act = async () => await context.AuditEvents.CountAsync();

            await act.Should().NotThrowAsync(
                "an early return must still release the reader, or the caller's next query fails on "
                + "a connection this walk left busy");

            _output.WriteLine($"broken at {verification.FirstBrokenSequence}, next query still worked");
        }
        finally
        {
            // Remove only my rows. They are the tail, so what remains is a valid prefix.
            var toRemove = await context.AuditEvents.Where(e => mine.Contains(e.Id)).ToListAsync();
            context.AuditEvents.RemoveRange(toRemove);
            await context.SaveChangesAsync();

            // The cleanup either worked or it did not, and "did not" means the next test to verify
            // this chain fails for a reason that has nothing to do with it.
            (await context.AuditEvents.CountAsync()).Should().Be(
                before, "this test must leave the shared audit table exactly as it found it");
        }
    }

    public void Dispose() => _factory?.Dispose();
}
