using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AzureBank.Api.Services;
using AzureBank.Infrastructure.Data;
using AzureBank.Infrastructure.Notices;
using AzureBank.Shared.Options;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace AzureBank.Tests.Integration;

/// <summary>
/// What only SQL Server can prove about the claim protocol (ADR-0048): that a claim held open blocks
/// a second runner until it commits and leaves it nothing; that two runners sweeping back to back
/// deliver every row once; that a live lease is left alone and a lapsed one taken; and that the
/// store refuses a half-set lease.
/// </summary>
/// <remarks>
/// <para>
/// Real rows, written by real enrolments through the real host; real files, written by the real
/// pickup transport into a temp directory outside any git tree. The transport creates each file
/// exclusively, so a row delivered twice into one directory fails its second write — the artefact
/// detects a double DELIVERY. A double CLAIM is caught by the exact claim counts, never by the files.
/// </para>
/// <para>
/// This file is the only cover of the set-based claim statement; the unit suite runs InMemory's
/// load-and-save fallback. A bug in one path is invisible to the other.
/// </para>
/// </remarks>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class NoticeRelaySqlServerTests : IDisposable
{
    private const string Password = "TestPass123!";
    private const string Contact = "security@your-bank.example, +00 000 0000";

    private readonly ITestOutputHelper _output;
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "azurebank-relay-sql-" + Guid.NewGuid().ToString("N"));
    private CustomWebApplicationFactory? _factory;

    public NoticeRelaySqlServerTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        _factory?.Dispose();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
        {
        }
    }

    private CustomWebApplicationFactory Factory()
    {
        if (_factory is null)
        {
            _factory = new CustomWebApplicationFactory();
            _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        }

        return _factory;
    }

    private NoticeRelayService Runner(int leaseSeconds = 120, int batchSize = 100) =>
        new(
            Factory().Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new NoticeRelayOptions
            {
                Runner = NoticeRunner.Api,
                PickupDirectory = _directory,
                Contact = Contact,
                PeriodSeconds = 5,
                LeaseSeconds = leaseSeconds,
                BatchSize = batchSize,
            }),
            Factory().Services.GetRequiredService<ILogger<NoticeRelayService>>());

    /// <summary>One real enrolment per call: one owed row, written by the API in the enrolment's save.</summary>
    private async Task<Guid> EnrolAsync()
    {
        var factory = Factory();
        using var client = factory.CreateClient();
        var unique = Guid.NewGuid().ToString("N")[..8];
        var email = $"relay{unique}@example.com";
        var register = await client.PostAsJsonAsync("/api/auth/register", new
        {
            azureTag = "relay_" + unique,
            email,
            password = Password,
            firstName = "Relay",
            lastName = "Proof",
        });
        register.EnsureSuccessStatusCode();
        var token = (await register.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data").GetProperty("token").GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        (await client.PostAsJsonAsync("/api/auth/pin", new { pin = "424242", password = Password }))
            .EnsureSuccessStatusCode();

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var owner = await context.Users.AsNoTracking().SingleAsync(u => u.Email == email);
        return (await context.SubscriberNotices.AsNoTracking().SingleAsync(n => n.UserId == owner.Id)).Id;
    }

    /// <summary>
    /// The proofs share one database with every other SQL test, and those leave owed rows behind —
    /// measured 2026-09-04: a sweep claimed 500 of them. A relay takes every free owed row, as it
    /// should, so before a proof sweeps it puts every row that is not its own under a live lease held
    /// by nobody real. The lease lapses on its own; nothing is deleted, and no other test's evidence
    /// is touched.
    /// </summary>
    private async Task QuarantineOthersAsync(IEnumerable<Guid> mine)
    {
        using var scope = Factory().Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var keep = mine.ToList();
        var until = DateTime.UtcNow.AddMinutes(10);
        await context.SubscriberNotices
            .Where(n => n.DeliveredAt == null && !keep.Contains(n.Id))
            .ExecuteUpdateAsync(set => set
                .SetProperty(n => n.LeasedUntil, until)
                .SetProperty(n => n.LeasedBy, "test/quarantine/0/00000000"));
    }

    private async Task<(DateTime? DeliveredAt, string? LeasedBy, DateTime? LeasedUntil)> RowAsync(Guid id)
    {
        using var scope = Factory().Services.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<AzureBankDbContext>()
            .SubscriberNotices.AsNoTracking().SingleAsync(n => n.Id == id);
        return (row.DeliveredAt, row.LeasedBy, row.LeasedUntil);
    }

    [SqlServerFact]
    public async Task AClaimHeldOpen_BlocksTheSecondRunner_WhichThenFindsNothingFree()
    {
        /*
          THE PROOF THAT TWO CLAIMS CANNOT BOTH HOLD A ROW, made with a transaction rather than a
          race. Runner A's claim is the same set-based statement the service issues, executed inside
          a transaction that is NOT committed; runner B's sweep must then block on A's row locks —
          measured: it had not returned 1.5 s later — and, once A commits, find nothing free.
        */
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            ids.Add(await EnrolAsync());
        }

        await QuarantineOthersAsync(ids);

        using var held = Factory().Services.CreateScope();
        var heldContext = held.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        await using var transaction = await heldContext.Database.BeginTransactionAsync();
        var now = DateTime.UtcNow;
        var claimedByA = await NoticeClaim.ClaimAsync(
            heldContext, "api/PROOF/1/held0000", now, now.AddMinutes(2), 100, CancellationToken.None);
        claimedByA.Should().Be(ids.Count);

        var b = Runner();
        var bSweep = b.SweepAsync(CancellationToken.None);
        var finishedWhileHeld = await Task.WhenAny(bSweep, Task.Delay(TimeSpan.FromMilliseconds(1500))) == bSweep;
        finishedWhileHeld.Should().BeFalse("B's claim waits on A's row locks for as long as A's transaction is open");

        await transaction.CommitAsync();
        var bSummary = await bSweep;

        bSummary.Claimed.Should().Be(0, "A's committed lease is what B's predicate sees");
        bSummary.Delivered.Should().Be(0);
        foreach (var id in ids)
        {
            (await RowAsync(id)).LeasedBy.Should().Be("api/PROOF/1/held0000");
        }
    }

    [SqlServerFact]
    public async Task TwoRunnersSweepingBackToBack_EveryRowGoesOutOnce()
    {
        var ids = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            ids.Add(await EnrolAsync());
        }

        await QuarantineOthersAsync(ids);
        var a = Runner();
        var b = Runner();
        a.RunnerName.Should().NotBe(b.RunnerName);

        var sweeps = await Task.WhenAll(
            a.SweepAsync(CancellationToken.None),
            b.SweepAsync(CancellationToken.None));

        _output.WriteLine($"runner A claimed {sweeps[0].Claimed}, runner B claimed {sweeps[1].Claimed}");

        sweeps.Sum(s => s.Claimed).Should().Be(ids.Count, "exactly the free rows were claimed, each by one runner");
        sweeps.Sum(s => s.Delivered).Should().Be(ids.Count);
        sweeps.Sum(s => s.Owed).Should().Be(0, "a second delivery would have failed the exclusive file create and been counted here");
        Directory.GetFiles(_directory, "*.eml").Should().HaveCount(ids.Count, "one artefact per row, and never two");

        foreach (var id in ids)
        {
            var (deliveredAt, leasedBy, leasedUntil) = await RowAsync(id);
            deliveredAt.Should().NotBeNull();
            leasedBy.Should().BeNull("the mark clears the lease");
            leasedUntil.Should().BeNull();
        }
    }

    [SqlServerFact]
    public async Task ALiveLeaseIsLeftAlone_AndALapsedOneIsTaken()
    {
        var live = await EnrolAsync();
        var lapsed = await EnrolAsync();
        await QuarantineOthersAsync([live, lapsed]);

        using (var scope = Factory().Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
            await context.Database.ExecuteSqlAsync(
                $"UPDATE SubscriberNotices SET LeasedUntil = DATEADD(minute, 2, SYSUTCDATETIME()), LeasedBy = 'api/OTHER/1/live0000' WHERE Id = {live}");
            await context.Database.ExecuteSqlAsync(
                $"UPDATE SubscriberNotices SET LeasedUntil = DATEADD(minute, -1, SYSUTCDATETIME()), LeasedBy = 'api/OTHER/1/dead0000' WHERE Id = {lapsed}");
        }

        var summary = await Runner().SweepAsync(CancellationToken.None);

        summary.Claimed.Should().Be(1, "only the lapsed lease was free");
        summary.Delivered.Should().Be(1);
        (await RowAsync(lapsed)).DeliveredAt.Should().NotBeNull();
        var untouched = await RowAsync(live);
        untouched.DeliveredAt.Should().BeNull("another runner holds it");
        untouched.LeasedBy.Should().Be("api/OTHER/1/live0000");
    }

    [SqlServerFact]
    public async Task AFileLeftByARunnerThatDiedBeforeMarking_IsRefused_AndTheRowStaysOwed()
    {
        /*
          THE PICKUP DIRECTORY'S SHAPE OF AT-LEAST-ONCE (ADR-0048 D3). The row was delivered and its
          mark lost — simulated by un-marking it — and the lease lapsed. The next claim takes it; the
          exclusive create refuses the file that is already there; the row stays owed beside it, for
          the runbook to clear. Nothing goes out twice, and nothing pretends the row is done.
        */
        var id = await EnrolAsync();
        await QuarantineOthersAsync([id]);
        (await Runner().SweepAsync(CancellationToken.None)).Delivered.Should().Be(1);

        using (var scope = Factory().Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<AzureBankDbContext>().Database.ExecuteSqlAsync(
                $"UPDATE SubscriberNotices SET DeliveredAt = NULL, DeliveryReceipt = NULL WHERE Id = {id}");
        }

        var again = await Runner().SweepAsync(CancellationToken.None);

        again.Should().Be(new NoticeSweepSummary(Claimed: 1, Delivered: 0, Owed: 1));
        Directory.GetFiles(_directory, "*.eml").Should().ContainSingle("the earlier copy is kept, never truncated");
        var row = await RowAsync(id);
        row.DeliveredAt.Should().BeNull();
        row.LeasedBy.Should().NotBeNull("held until the lease lapses, then tried again — and refused again");
    }

    [SqlServerFact]
    public async Task AClaimIsBoundedToTheBatch_OldestFirst_AndTheRestStaysFree()
    {
        // The set-based statement with its TOP(n) subquery, translated and run by the real engine.
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            ids.Add(await EnrolAsync());
        }

        await QuarantineOthersAsync(ids);
        var runner = Runner(batchSize: 2);

        var first = await runner.SweepAsync(CancellationToken.None);
        first.Should().Be(new NoticeSweepSummary(Claimed: 2, Delivered: 2, Owed: 0));
        (await RowAsync(ids[2])).LeasedBy.Should().BeNull("the third, youngest row was left free for the next claim");
        Directory.GetFiles(_directory, "*.eml").Should().HaveCount(2);

        var second = await runner.SweepAsync(CancellationToken.None);
        second.Should().Be(new NoticeSweepSummary(Claimed: 1, Delivered: 1, Owed: 0));
        Directory.GetFiles(_directory, "*.eml").Should().HaveCount(3);
    }

    [SqlServerTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheDatabaseRefusesAHalfSetLease(bool holderOnly)
    {
        var id = await EnrolAsync();
        using var scope = Factory().Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();

        // Both halves, as the delivery pair's sibling test drives both of its halves.
        var act = () => holderOnly
            ? context.Database.ExecuteSqlAsync(
                $"UPDATE SubscriberNotices SET LeasedBy = 'api/HALF/1/00000000' WHERE Id = {id}")
            : context.Database.ExecuteSqlAsync(
                $"UPDATE SubscriberNotices SET LeasedUntil = SYSUTCDATETIME() WHERE Id = {id}");

        (await act.Should().ThrowAsync<SqlException>()).Which.Message
            .Should().Contain("CK_SubscriberNotices_Lease", "a holder with no lease end, or a lease end with no holder, is a state no runner produces");
    }
}
