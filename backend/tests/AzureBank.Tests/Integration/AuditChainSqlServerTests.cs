using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AzureBank.Shared.Constants;
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
    public async Task RenumberingTheTailRow_BreaksTheChain()
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
            .BeTrue("the control: the chain is intact before the renumbering");

        /*
          THE ONE TAMPERING THE v1 PAYLOAD DID NOT COVER. Sequence is the column VerifyAsync orders
          by, and it was not hashed. Reordering an INTERIOR row was always caught — the PreviousHash
          links stop lining up — but renumbering the LAST row to an unused higher value left the
          order unchanged and every hash matching. Nothing verifiable moved.

          No exploit was built on that and none is claimed; it is closed because it cost one field.
          This test is what makes the claim falsifiable rather than a comment.
        */
        var affected = await context.Database.ExecuteSqlRawAsync(
            "UPDATE [AuditEvents] SET [Sequence] = 99 WHERE [Sequence] = 3");
        affected.Should().Be(1, "the renumbering itself must have happened, or the test proves nothing");

        var verification = await chain.VerifyAsync(context);

        verification.IsIntact.Should().BeFalse("Sequence is inside the v2 payload");
        verification.FirstBrokenSequence.Should().Be(99, "and the verifier must name the row it read");
        verification.Verified.Should().Be(2, "the two rows before it still verify");
        _output.WriteLine($"renumbered tail -> {verification.Reason}");
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

    [SqlServerFact]
    public async Task WhenTheAuditRowCannotBeWritten_TheHandleRenameIsRolledBackToo()
    {
        /*
          D1 THROUGH THE REAL ENDPOINT, and the only test in this repository that can tell the two
          shapes apart. WhenTheAuditRowCannotBeWritten_TheBusinessChangeIsRolledBackToo above proves
          the property at the DbContext level, where the test itself chooses to make one save. This
          one proves it for a request the API actually serves.

          It matters because the first version of UserService recorded the row AFTER saving the
          rename and then saved a SECOND time. Every row-exists assertion still passed — the row WAS
          written — so nothing in the suite could tell that the rename and its evidence had stopped
          being atomic. Only a failure injected into the audit insert separates them: with two saves
          the handle stays renamed and the evidence is gone, which is precisely the state D1 forbids.

          Cannot live on InMemory: that provider has no transactions, so nothing would roll back
          there whichever way the code was written, and a green result would mean nothing.
        */
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        var client = _factory.CreateClient();

        var unique = Guid.NewGuid().ToString("N")[..8];
        var register = await client.PostAsJsonAsync("/api/auth/register", new
        {
            azureTag = $"audit_{unique}",
            email = $"audit{unique}@example.com",
            password = "TestPass123!",
            firstName = "Audit",
            lastName = "Atomicity",
        });
        register.EnsureSuccessStatusCode();

        var token = (await register.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data").GetProperty("token").GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var fault = new OverlongAuditEventInterceptor(SecurityEvents.AzureTagRenamed);
        _factory.AddInterceptor(fault);

        var response = await client.PatchAsJsonAsync(
            "/api/users/me/azuretag", new { azureTag = $"renamed_{unique}" });

        fault.Fired.Should().BeTrue("the test proves nothing if the audit insert never actually failed");
        response.IsSuccessStatusCode.Should().BeFalse(
            "an action that cannot be audited must not be reported as done");

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();

        var stored = await context.Users.AsNoTracking().SingleAsync(u => u.Email == $"audit{unique}@example.com");
        stored.AzureTag.Should().Be(
            $"audit_{unique}",
            "the rename shared the failed save, so it must be gone with the row that would have proved it");

        (await context.AuditEvents.AsNoTracking()
            .CountAsync(e => e.ActorUserId == stored.Id && e.Event == SecurityEvents.AzureTagRenamed))
            .Should().Be(0, "and no half-written evidence survives either");

        _output.WriteLine($"audit insert refused -> {(int)response.StatusCode}, handle still {stored.AzureTag}");
    }

    [SqlServerFact]
    public async Task WhenTheReuseAuditRowCannotBeWritten_TheStolenFamilyIsStillRevoked()
    {
        /*
          THE ONE PLACE WHERE D1 MUST NOT APPLY, and the reason the audit wiring in this branch had
          to be reordered. Everywhere else "no evidence, no action" is the right trade. Here the
          action is CONTAINMENT of a token already proven stolen, and refusing to contain it because
          the logging failed hands the attacker the family.

          The first wiring awaited RecordRefusalAsync BEFORE the try that guards the family revoke.
          RecordRefusalAsync is deliberately allowed to throw and runs on its own connection, so a
          command timeout or an unwritable audit table escaped RotateAsync with the revoke never
          attempted, no MitigationFailed row either, and a 500 instead of the uniform 401 — the exact
          outcome the comment inside that catch calls out as inviting a retry of the stolen token.

          Found by an adversarial sweep of the audit write path, not by a bot and not by the suite.
        */
        var services = CreateSqlServices();
        var client = _factory!.CreateClient();

        var unique = Guid.NewGuid().ToString("N")[..8];
        var register = await client.PostAsJsonAsync("/api/auth/register", new
        {
            azureTag = $"reuse_{unique}",
            email = $"reuse{unique}@example.com",
            password = "TestPass123!",
            firstName = "Reuse",
            lastName = "Containment",
        });
        register.EnsureSuccessStatusCode();

        var registered = await register.Content.ReadFromJsonAsync<JsonElement>();
        var stolen = registered.GetProperty("data").GetProperty("token")
            .GetProperty("refreshToken").GetString();
        stolen.Should().NotBeNullOrEmpty("the reuse branch needs a real token to replay");

        /*
          LOG OUT, THEN LOG BACK IN, and both halves are load-bearing — the first two attempts at
          this setup each proved nothing, which is why the reasoning is written down.

          Logging out rather than ROTATING: RotateAsync treats a token that HAS a successor and was
          revoked inside RotationGraceWindow (10 s) as a benign lost-response retry and writes no
          audit row at all, so a rotate-then-replay never reaches the reuse branch. Logout calls
          RevokeAllForUserAsync, which revokes WITHOUT a successor — the shape the code itself names
          as genuine reuse ("explicit logout / theft response").

          Logging back IN afterwards: logout revokes everything, so replaying against that state
          leaves the family revoke with nothing to do and "zero active tokens" holds whether or not
          containment ran. The fresh session is what gives the mitigation a victim, and it is what
          makes this test able to fail — verified by putting the audit write back ahead of the
          containment and watching it go red.
        */
        var accessToken = registered.GetProperty("data").GetProperty("token")
            .GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        (await client.PostAsync("/api/auth/logout", content: null)).EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = null;

        var login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            email = $"reuse{unique}@example.com",
            password = "TestPass123!",
        });
        login.EnsureSuccessStatusCode();

        var fault = new OverlongAuditEventInterceptor(SecurityEvents.RefreshTokenReuse);
        _factory.AddInterceptor(fault);

        var replay = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = stolen });

        fault.Fired.Should().BeTrue("the test proves nothing if the reuse audit insert never failed");
        replay.IsSuccessStatusCode.Should().BeFalse("a replayed token is always rejected");

        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var user = await context.Users.AsNoTracking()
            .SingleAsync(u => u.Email == $"reuse{unique}@example.com");

        var active = await context.RefreshTokens.AsNoTracking()
            .Where(t => t.UserId == user.Id && t.RevokedAt == null)
            .CountAsync();

        active.Should().Be(
            0,
            "the session opened after the logout must die with the rest: containment cannot be "
            + "reachable only when logging works");

        _output.WriteLine($"reuse audit insert refused -> {(int)replay.StatusCode}, active tokens left {active}");
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
