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

            // Break the chain on a row of my own, so the walk returns EARLY and the reader is left
            // wherever the implementation leaves it.
            var tampered = await context.AuditEvents.OrderByDescending(e => e.Sequence).FirstAsync();
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
        }
    }

    public void Dispose() => _factory?.Dispose();
}
