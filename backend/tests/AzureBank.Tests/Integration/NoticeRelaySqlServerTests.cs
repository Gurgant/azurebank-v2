using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AzureBank.Api.Services;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
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
/// What only SQL Server can prove about the claim protocol (ADR-0048): that two runners sweeping the
/// same owed rows at the same moment never both hold one, that a lapsed lease is taken and a live one
/// is not, and that the store refuses a half-set lease.
/// </summary>
/// <remarks>
/// Real rows, written by real enrolments through the real host; real files, written by the real
/// pickup transport into a temp directory outside any git tree. The transport creates each file
/// exclusively, so a row delivered twice would fail its second write — the artefact itself detects
/// a double delivery, which is why the count of files is asserted and not only the count of marks.
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

    private NoticeRelayService Runner(int leaseSeconds = 120) =>
        new(
            Factory().Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new NoticeRelayOptions
            {
                Runner = NoticeRunner.Api,
                PickupDirectory = _directory,
                Contact = Contact,
                PeriodSeconds = 5,
                LeaseSeconds = leaseSeconds,
            }),
            Factory().Services.GetRequiredService<ILogger<NoticeRelayService>>());

    /// <summary>One real enrolment per call: one owed row, written by the API in the enrolment's save.</summary>
    private async Task<Guid> EnrolAsync()
    {
        var factory = Factory();
        var client = factory.CreateClient();
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
    /// hundreds, measured. A relay takes every free owed row, as it should, so before a proof sweeps
    /// it puts every row that is not its own under a live lease held by nobody real. The lease
    /// lapses on its own; nothing is deleted, and no other test's evidence is touched.
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
                .SetProperty(n => n.LeasedBy, "TEST/quarantine/00000000"));
    }

    private async Task<(DateTime? DeliveredAt, string? LeasedBy, DateTime? LeasedUntil)> RowAsync(Guid id)
    {
        using var scope = Factory().Services.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<AzureBankDbContext>()
            .SubscriberNotices.AsNoTracking().SingleAsync(n => n.Id == id);
        return (row.DeliveredAt, row.LeasedBy, row.LeasedUntil);
    }

    [SqlServerFact]
    public async Task TwoRunnersSweepingAtOnce_NeverBothHoldOneRow_AndEveryRowGoesOutOnce()
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
            a.SweepAsync(_directory, CancellationToken.None),
            b.SweepAsync(_directory, CancellationToken.None));

        var claimed = sweeps.Sum(s => s.Claimed);
        var delivered = sweeps.Sum(s => s.Delivered);
        _output.WriteLine($"runner A claimed {sweeps[0].Claimed}, runner B claimed {sweeps[1].Claimed}");

        claimed.Should().BeGreaterThanOrEqualTo(ids.Count, "every owed row was free and somebody took each");
        delivered.Should().Be(ids.Count, "each row is delivered exactly as many times as it was claimed — once");
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
                $"UPDATE SubscriberNotices SET LeasedUntil = DATEADD(minute, 2, SYSUTCDATETIME()), LeasedBy = 'OTHER/1/live0000' WHERE Id = {live}");
            await context.Database.ExecuteSqlAsync(
                $"UPDATE SubscriberNotices SET LeasedUntil = DATEADD(minute, -1, SYSUTCDATETIME()), LeasedBy = 'OTHER/1/dead0000' WHERE Id = {lapsed}");
        }

        var summary = await Runner().SweepAsync(_directory, CancellationToken.None);

        summary.Claimed.Should().Be(1, "only the lapsed lease was free");
        summary.Delivered.Should().Be(1);
        (await RowAsync(lapsed)).DeliveredAt.Should().NotBeNull();
        var untouched = await RowAsync(live);
        untouched.DeliveredAt.Should().BeNull("another runner holds it");
        untouched.LeasedBy.Should().Be("OTHER/1/live0000");
    }

    [SqlServerFact]
    public async Task TheDatabaseRefusesAHalfSetLease()
    {
        var id = await EnrolAsync();
        using var scope = Factory().Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();

        var act = () => context.Database.ExecuteSqlAsync(
            $"UPDATE SubscriberNotices SET LeasedBy = 'HALF/1/00000000' WHERE Id = {id}");

        (await act.Should().ThrowAsync<SqlException>()).Which.Message
            .Should().Contain("CK_SubscriberNotices_Lease", "a holder with no lease end is a state no runner produces");
    }
}
