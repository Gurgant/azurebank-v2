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
/// THE authoritative proofs of the audit trail (ADR-0044), on REAL SQL Server — because none of the
/// three properties below can be established anywhere else.
/// </summary>
/// <remarks>
/// <para>
/// <c>AuditChainTests</c> proves, on the InMemory provider, that the chain links and that tampering
/// with a row breaks it. It cannot prove what is here, and claiming otherwise would be the "green
/// and false" state this project treats as the worst outcome:
/// </para>
/// <list type="number">
/// <item>NO FORK UNDER CONCURRENT WRITERS. The whole design rests on <c>UPDLOCK, HOLDLOCK</c>, and
/// the InMemory provider has no locks at all, so a green test there would say nothing.</item>
/// <item>TAMPERING PERFORMED IN THE DATABASE, not through the tracked object graph — an UPDATE
/// issued by whoever holds a connection, which is the actual threat model.</item>
/// <item>A FAILED AUDIT WRITE TAKES THE BUSINESS ACTION WITH IT. This is decision D1 — "better a
/// blocked bank than an emptied one" — and until it is measured it is an intention, not a
/// property.</item>
/// </list>
/// <para>
/// Gated by AZUREBANK_TEST_SQLSERVER, like every other proof in this directory.
/// </para>
/// </remarks>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class AuditChainSqlServerTests : IDisposable
{
    private const int Parallelism = 24;

    private readonly ITestOutputHelper _output;
    private CustomWebApplicationFactory? _factory;

    public AuditChainSqlServerTests(ITestOutputHelper output) => _output = output;

    [SqlServerFact]
    public async Task ConcurrentWriters_DoNotForkTheChain()
    {
        var services = CreateSqlServices();
        await ClearAuditEventsAsync(services);

        /*
          Twenty-four writers, each in its own scope and therefore on its own connection, appending at
          the same moment. Without UPDLOCK + HOLDLOCK two of them read the same tail and both append
          after it: two rows carrying the same PreviousHash, which is a FORK — the chain silently
          becomes a tree, and a deletion inside one branch stops being detectable.
        */
        using var barrier = new Barrier(Parallelism);
        var writers = Enumerable.Range(0, Parallelism).Select(i => Task.Run(async () =>
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
            db.AuditEvents.Add(NewEvent($"Concurrent{i:D2}"));
            barrier.SignalAndWait();
            await db.SaveChangesAsync();
        }));

        await Task.WhenAll(writers);

        using var verifyScope = services.CreateScope();
        var verifier = verifyScope.ServiceProvider.GetRequiredService<IAuditChain>();
        var context = verifyScope.ServiceProvider.GetRequiredService<AzureBankDbContext>();

        var rows = await context.AuditEvents.AsNoTracking().OrderBy(e => e.Sequence).ToListAsync();
        _output.WriteLine(
            $"{Parallelism} concurrent writers -> {rows.Count} rows, "
            + $"sequences {rows[0].Sequence}..{rows[^1].Sequence}");

        rows.Should().HaveCount(
            Parallelism, "every writer's row must be there — a lost row is as bad as a forked one");
        rows.Select(r => r.Sequence).Should().OnlyHaveUniqueItems("Sequence is what the chain is ordered by");
        rows.Skip(1).Select(r => r.PreviousHash).Should().OnlyHaveUniqueItems(
            "two rows sharing a predecessor IS the fork this lock exists to prevent");

        var verification = await verifier.VerifyAsync(context);
        verification.IsIntact.Should().BeTrue(because: verification.Reason);
        verification.Verified.Should().Be(
            Parallelism, "a verification that read nothing also reports intact");
    }

    [SqlServerFact]
    public async Task ARowUpdatedDirectlyInTheDatabase_BreaksTheChain()
    {
        var services = CreateSqlServices();
        await ClearAuditEventsAsync(services);

        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var chain = scope.ServiceProvider.GetRequiredService<IAuditChain>();

        foreach (var name in new[] { "First", "Second", "Third" })
        {
            context.AuditEvents.Add(NewEvent(name));
            await context.SaveChangesAsync();
        }

        (await chain.VerifyAsync(context)).IsIntact.Should()
            .BeTrue("the control: the chain is intact before the tampering");

        /*
          The actual threat model, and the reason this test cannot live on InMemory: not an edit made
          through EF's change tracker, but a raw UPDATE issued by whoever holds a connection — a DBA,
          a leaked connection string, an attacker who reached the database and not the application.
          ExecuteSqlRaw goes straight to the server, past every application-level guard we own.
        */
        var affected = await context.Database.ExecuteSqlRawAsync(
            "UPDATE [AuditEvents] SET [Event] = 'Nothing' WHERE [Sequence] = 2");
        affected.Should().Be(1, "the tampering itself must have happened, or the test proves nothing");

        var verification = await chain.VerifyAsync(context);

        verification.IsIntact.Should().BeFalse("the row no longer hashes to what is stored beside it");
        verification.FirstBrokenSequence.Should().Be(
            2, "and it must name WHICH row, not merely report that something is wrong");
        verification.Verified.Should().Be(1, "one row was read and passed before the broken one");
        _output.WriteLine($"tampered row 2 -> {verification.Reason}");
    }

    [SqlServerFact]
    public async Task WhenTheAuditRowCannotBeWritten_TheBusinessChangeIsRolledBackToo()
    {
        var services = CreateSqlServices();
        await ClearAuditEventsAsync(services);

        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();

        /*
          A REAL user row, because SQL Server enforces FK_Accounts_AspNetUsers_UserId and the
          InMemory provider does not — the first version of this test invented a UserId and died on
          the constraint instead of on the audit row it meant to test.
        */
        var user = new ApplicationUser
        {
            Id = Guid.CreateVersion7(),
            UserName = $"audit-rollback-{Guid.NewGuid():N}",
            Email = $"audit-rollback-{Guid.NewGuid():N}@example.test",
            AzureTag = $"audit{Guid.NewGuid():N}"[..20],
            FirstName = "Audit",
            LastName = "Rollback",
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var account = new Account
        {
            Id = Guid.CreateVersion7(),
            UserId = user.Id,
            // Unique per run: IX_Accounts_AccountNumber is a real unique index here, and a fixed
            // literal made this test pass once and then fail forever on a shared database.
            AccountNumber = $"AB-{Random.Shared.Next(1000, 9999)}-{Random.Shared.Next(1000, 9999)}-01",
            Name = "Before",
            Type = AccountType.Savings,
            Balance = 0,
            User = null!, // Set by the FK above; the navigation itself is not needed here
        };
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        /*
          D1, measured rather than asserted. The audit row is made unwritable by SQL Server itself —
          Detail is capped at 1024 in AuditEventConfiguration, so 2,000 characters is refused with
          "String or binary data would be truncated". Deliberately a DATABASE refusal and not a guard
          thrown by our own code: the point is that a failure arriving at the very last step still
          takes the business change down with it.
        */
        account.Name = "After";
        context.AuditEvents.Add(NewEvent("Doomed", detail: new string('x', 2000)));

        var act = () => context.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>();

        // A fresh scope, so the answer comes from SQL Server and not from the tracker that just failed.
        using var freshScope = services.CreateScope();
        var fresh = freshScope.ServiceProvider.GetRequiredService<AzureBankDbContext>();

        var reread = await fresh.Accounts.AsNoTracking().SingleAsync(a => a.Id == account.Id);
        reread.Name.Should().Be(
            "Before",
            "better a blocked bank than an emptied one: an action that cannot be audited must not happen");

        (await fresh.AuditEvents.CountAsync()).Should()
            .Be(0, "and no half-written audit row survives either");

        // The database is shared by the whole SQL collection and lives between runs, so this test
        // takes its own fixtures back out instead of leaving one more user behind every time.
        await fresh.Database.ExecuteSqlRawAsync(
            "DELETE FROM [Accounts] WHERE [UserId] = {0}; DELETE FROM [AspNetUsers] WHERE [Id] = {0};",
            user.Id);
    }

    [SqlServerFact]
    public async Task AnAuditedSave_WorksUnderTheRetryingStrategyProductionActuallyUses()
    {
        /*
          THE PRODUCTION CONFIGURATION, pinned — and the only test here that came from a running API
          rather than from reasoning. EnableRetryOnFailure is on in ServiceCollectionExtensions, and
          EF refuses a user-initiated transaction under a retrying strategy. The chain opens one, so
          every audited request answered 500:

            System.InvalidOperationException: The configured execution strategy
            'SqlServerRetryingExecutionStrategy' does not support user-initiated transactions. Use
            the execution strategy returned by 'DbContext.Database.CreateExecutionStrategy()' …

          measured on https://localhost:7215 with POST /api/auth/refresh, while all 766 tests were
          green. They were green because CustomWebApplicationFactory leaves the retrying strategy
          OFF unless a test opts in — so the default SQL path is not the production path, and this
          test exists to make one of them be.
        */
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        _factory.EnableSqlRetryOnFailure();
        _ = _factory.CreateClient();

        await ClearAuditEventsAsync(_factory.Services);

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var chain = scope.ServiceProvider.GetRequiredService<IAuditChain>();

        context.AuditEvents.Add(NewEvent("UnderRetryingStrategy"));
        var act = () => context.SaveChangesAsync();

        await act.Should().NotThrowAsync(
            "the funnel must go through Database.CreateExecutionStrategy(), as AuthService.RegisterAsync already does");

        var row = await context.AuditEvents.AsNoTracking().SingleAsync();
        row.RowHash.Should().HaveLength(64, "and the row must actually be chained, not merely written");
        row.Sequence.Should().Be(1);

        var verification = await chain.VerifyAsync(context);
        verification.IsIntact.Should().BeTrue(because: verification.Reason);
        verification.Verified.Should().Be(1, "a verification that read nothing also reports intact");
    }

    private static AuditEvent NewEvent(string name, string? detail = null) => new()
    {
        Id = Guid.CreateVersion7(),
        OccurredAt = DateTime.UtcNow,
        Event = name,
        Outcome = AuditOutcome.Succeeded,
        ActorUserId = Guid.NewGuid(),
        Detail = detail,
        RowHash = string.Empty,
    };

    private IServiceProvider CreateSqlServices()
    {
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        _ = _factory.CreateClient(); // forces the host to build and the database to migrate
        return _factory.Services;
    }

    // The table is shared across the whole SQL collection and every assertion here counts rows, so
    // each test starts from a known floor rather than from whatever ran before it.
    private static async Task ClearAuditEventsAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        await context.Database.ExecuteSqlRawAsync("DELETE FROM [AuditEvents]");
    }

    public void Dispose() => _factory?.Dispose();
}
