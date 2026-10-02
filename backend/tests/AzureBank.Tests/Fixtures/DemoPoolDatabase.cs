extern alias seeder;

using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Entities;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using seeder::AzureBank.Seeder.Extensions;
using seeder::AzureBank.Seeder.Pool;

namespace AzureBank.Tests.Fixtures;

/// <summary>One demo copy as the database holds it: its pool row and the users that point at it.</summary>
internal sealed record BuiltCopy(DemoCopy Row, IReadOnlyList<ApplicationUser> Users)
{
    public Guid Id => Row.Id;

    /// <summary>The demo user a visitor signs in as.</summary>
    public ApplicationUser Owner => Users.Single(u => u.Id == Row.OwnerUserId);

    public ApplicationUser Jane => Users.Single(u => u.AzureTag.StartsWith("jane_", StringComparison.Ordinal));

    public ApplicationUser Mike => Users.Single(u => u.AzureTag.StartsWith("mike_", StringComparison.Ordinal));

    public Guid[] UserIds => [.. Users.Select(u => u.Id)];
}

/// <summary>
/// A scratch SQL Server database for one test of the demo pool, with the two hosts that share it in
/// a deployment: the Seeder's own composition root, which is what <c>seed-pool</c> and
/// <c>recycle</c> run in, and the API.
/// </summary>
/// <remarks>
/// <para>
/// ITS OWN DATABASE, not the shared proofs one, for the reason <c>SeededDemoDataSqlServerTests</c>
/// gives: the pool's commands count every row of the tables they manage, so rows another test left
/// behind would change what they do. Dropped when the test ends.
/// </para>
/// <para>
/// THE SEEDER'S REAL ROOT. Each run builds a container with <c>AddSeederServices</c>, as
/// <c>Program.cs</c> does, and resolves the builder or the recycler from a scope of it: one container
/// per run, as one process per command. So a test also proves the two are registered there, with a
/// retrying context, and an interceptor a test registers reaches the context the way the API's
/// commit gate does. The pepper is the API test host's, or a PIN seeded here would not verify there.
/// </para>
/// <para>
/// Small numbers by default: a target of 2 free copies and a low mark of 0, so a test that is not
/// about a signal ends at exit 0, and no test builds the 50 copies a deployment keeps.
/// </para>
/// </remarks>
internal sealed class DemoPoolDatabase : IAsyncDisposable
{
    /// <summary>The password a test gives a copy's owner, standing in for the one a claim sets.</summary>
    public const string VisitorPassword = "Visitor-Pass-2026!";

    private readonly List<IDisposable> _hosts = [];

    private DemoPoolDatabase(string connectionString) => ConnectionString = connectionString;

    public string ConnectionString { get; }

    /// <summary>
    /// When set, every Seeder container built afterwards writes its log here, at every level and
    /// from every category: the builder's and the recycler's own lines, Identity's, and EF's, which
    /// name each statement that was sent.
    /// </summary>
    public RecordingLoggerProvider? Log { get; set; }

    /// <summary>
    /// When set, registers more services in every Seeder container built afterwards, after the
    /// Seeder's own: how a test puts a collaborator that refuses beside the real ones.
    /// </summary>
    public Action<IServiceCollection>? AlsoRegister { get; set; }

    /// <summary>
    /// The environment every Seeder container built afterwards runs in. Production unless a test
    /// says otherwise: in Development EF logs each statement with the values it carried.
    /// </summary>
    public string EnvironmentName { get; set; } = Environments.Production;

    /// <summary>Creates the database and applies every migration.</summary>
    public static async Task<DemoPoolDatabase> CreateAsync()
    {
        var connectionString = new SqlConnectionStringBuilder(SqlServerFactAttribute.ConnectionString!)
        {
            InitialCatalog = $"AzureBankPoolProof_{Guid.NewGuid():N}",
        }.ConnectionString;

        var database = new DemoPoolDatabase(connectionString);
        await using var db = database.NewContext();
        await db.Database.MigrateAsync();
        return database;
    }

    /// <summary>A plain context on this database: no retry, no interceptor, nothing tracked across tests.</summary>
    public AzureBankDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AzureBankDbContext>().UseSqlServer(ConnectionString).Options);

    /// <summary>The API, in process, on this database.</summary>
    public CustomWebApplicationFactory Api()
    {
        var factory = new CustomWebApplicationFactory();
        factory.SetConnectionString(ConnectionString);
        _hosts.Add(factory);
        return factory;
    }

    /// <summary>
    /// The Seeder's container, as its <c>Program.cs</c> builds it, with the demo on.
    /// <paramref name="settings"/> override the defaults above; <paramref name="interceptors"/> reach
    /// every context the container builds.
    /// </summary>
    public ServiceProvider Seeder(Dictionary<string, string?>? settings = null, params IInterceptor[] interceptors)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = ConnectionString,
            ["Security:PinPepper"] = CustomWebApplicationFactory.PinPepper,
            ["Demo:Enabled"] = "true",
            ["Demo:Pool:TargetFree"] = "2",
            ["Demo:Pool:LowMark"] = "0",
        };
        foreach (var (key, value) in settings ?? [])
        {
            values[key] = value;
        }

        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns(EnvironmentName);

        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            if (Log is { } recorder)
            {
                logging.SetMinimumLevel(LogLevel.Trace).AddProvider(recorder);
            }
        });
        foreach (var interceptor in interceptors)
        {
            services.AddSingleton(interceptor);
        }

        services.AddSeederServices(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), environment.Object);
        AlsoRegister?.Invoke(services);

        var provider = services.BuildServiceProvider();
        _hosts.Add(provider);
        return provider;
    }

    /// <summary>What <c>seed-pool [target]</c> runs.</summary>
    public async Task<PoolRunSummary> SeedPoolAsync(
        int? target = null, Dictionary<string, string?>? settings = null, params IInterceptor[] interceptors)
    {
        using var scope = Seeder(settings, interceptors).CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DemoCopyBuilder>().SeedPoolAsync(target);
    }

    /// <summary>What <c>recycle</c> runs.</summary>
    public async Task<PoolRunSummary> RecycleAsync(
        Dictionary<string, string?>? settings = null,
        RecyclerControl control = RecyclerControl.None,
        params IInterceptor[] interceptors)
    {
        using var scope = Seeder(settings, interceptors).CreateScope();
        var recycler = scope.ServiceProvider.GetRequiredService<DemoCopyRecycler>();
        recycler.Control = control;
        return await recycler.RunAsync();
    }

    /// <summary>
    /// ARRANGE: <paramref name="count"/> free copies for a test to work on, built by the builder on a
    /// pool that holds no free copy. A test that is not about the builder stops here when the builder
    /// cannot build, and says so.
    /// </summary>
    public async Task<IReadOnlyList<BuiltCopy>> BuildCopiesAsync(int count)
    {
        var summary = await SeedPoolAsync(count);
        summary.Seeded.Should().Be(
            count, "ARRANGE: this test works on {0} built copies, and the builder has to build them first", count);

        var copies = await CopiesAsync();
        copies.Count(c => c.Row.ClaimedAt is null).Should().BeGreaterThanOrEqualTo(
            count, "ARRANGE: the copies the builder reported are the rows the test goes on to use");
        return copies;
    }

    /// <summary>Every pool row, oldest first, with the users that point at it.</summary>
    public async Task<IReadOnlyList<BuiltCopy>> CopiesAsync()
    {
        await using var db = NewContext();
        var rows = await db.Set<DemoCopy>().AsNoTracking().OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).ToListAsync();
        var users = await db.Users.AsNoTracking().Where(u => u.DemoCopyId != null).ToListAsync();
        return [.. rows.Select(row => new BuiltCopy(row, [.. users.Where(u => u.DemoCopyId == row.Id)]))];
    }

    /// <summary>One pool row as it is now.</summary>
    public async Task<BuiltCopy?> CopyAsync(Guid copyId) =>
        (await CopiesAsync()).SingleOrDefault(c => c.Id == copyId);

    /// <summary>
    /// Marks a free copy claimed at <paramref name="claimedAt"/>, as a visitor's claim would: the
    /// instant, a claim id, and the client's key when one is given.
    /// </summary>
    public async Task MarkClaimedAsync(Guid copyId, DateTime claimedAt, byte[]? clientKey = null)
    {
        await using var db = NewContext();
        Guid? claimId = Guid.CreateVersion7();
        DateTime? at = claimedAt;
        var affected = await db.Set<DemoCopy>()
            .Where(c => c.Id == copyId && c.ClaimedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.ClaimedAt, at)
                .SetProperty(c => c.ClaimId, claimId)
                .SetProperty(c => c.ClientKey, clientKey));
        affected.Should().Be(1, "ARRANGE: the copy was free, so the claim takes it");
    }

    /// <summary>Moves a claimed copy's claim to <paramref name="claimedAt"/>.</summary>
    public async Task BackdateClaimAsync(Guid copyId, DateTime claimedAt)
    {
        await using var db = NewContext();
        DateTime? at = claimedAt;
        var affected = await db.Set<DemoCopy>()
            .Where(c => c.Id == copyId && c.ClaimedAt != null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ClaimedAt, at));
        affected.Should().Be(1, "ARRANGE: only a claimed copy has a claim to back-date");
    }

    /// <summary>Moves a copy's seed instant to <paramref name="createdAt"/>.</summary>
    public async Task BackdateSeedAsync(Guid copyId, DateTime createdAt)
    {
        await using var db = NewContext();
        var affected = await db.Set<DemoCopy>()
            .Where(c => c.Id == copyId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.CreatedAt, createdAt));
        affected.Should().Be(1, "ARRANGE: the copy exists");
    }

    /// <summary>
    /// Gives a copy's owner a password through Identity, standing in for the claim: a free copy has
    /// none, and nothing can sign in to it.
    /// </summary>
    public async Task GiveOwnerAPasswordAsync(BuiltCopy copy, string password = VisitorPassword)
    {
        using var scope = Seeder().CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var owner = await users.FindByIdAsync(copy.Owner.Id.ToString());
        owner.Should().NotBeNull("ARRANGE: the copy's owner exists");
        var result = await users.AddPasswordAsync(owner!, password);
        result.Succeeded.Should().BeTrue(
            "ARRANGE: a free copy's owner has no password, so one can be added ({0})",
            string.Join("; ", result.Errors.Select(e => e.Description)));
    }

    /// <summary>
    /// A copy a visitor holds: claimed at <paramref name="claimedAt"/>, its owner able to sign in
    /// with <see cref="VisitorPassword"/>.
    /// </summary>
    public async Task ClaimForAVisitorAsync(BuiltCopy copy, DateTime claimedAt)
    {
        await GiveOwnerAPasswordAsync(copy);
        await MarkClaimedAsync(copy.Id, claimedAt);
    }

    /// <summary>
    /// Ends these users' grants as time would: a grant lives 60 minutes, and a test that back-dates a
    /// claim by a day has to move the session it opened along with it.
    /// </summary>
    public async Task ExpireGrantsAsync(Guid[] userIds)
    {
        await using var db = NewContext();
        var expired = DateTime.UtcNow.AddHours(-1);
        await db.RefreshTokens
            .Where(t => userIds.Contains(t.UserId))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ExpiresAt, expired));
    }

    /// <summary>Revokes these users' grants, as a sign-out does.</summary>
    public async Task<int> RevokeGrantsAsync(Guid[] userIds)
    {
        await using var db = NewContext();
        DateTime? now = DateTime.UtcNow;
        return await db.RefreshTokens
            .Where(t => userIds.Contains(t.UserId) && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now));
    }

    /// <summary>The ids that name a copy in a statement: the copy, its users and their accounts.</summary>
    public async Task<Guid[]> IdsOfAsync(BuiltCopy copy)
    {
        await using var db = NewContext();
        var userIds = copy.UserIds;
        var accountIds = await db.Accounts.IgnoreQueryFilters()
            .Where(a => userIds.Contains(a.UserId)).Select(a => a.Id).ToListAsync();
        return [copy.Id, .. userIds, .. accountIds];
    }

    /// <summary>How many rows a table holds for these users.</summary>
    public async Task<Dictionary<string, int>> RowsOfAsync(Guid[] userIds)
    {
        await using var db = NewContext();
        var accountIds = await db.Accounts.IgnoreQueryFilters()
            .Where(a => userIds.Contains(a.UserId)).Select(a => a.Id).ToListAsync();

        return new Dictionary<string, int>
        {
            ["AspNetUsers"] = await db.Users.CountAsync(u => userIds.Contains(u.Id)),
            ["AspNetUserRoles"] = await db.UserRoles.CountAsync(r => userIds.Contains(r.UserId)),
            ["Accounts"] = accountIds.Count,
            ["Transactions"] = await db.Transactions.IgnoreQueryFilters().CountAsync(t => accountIds.Contains(t.AccountId)),
            ["RefreshTokens"] = await db.RefreshTokens.CountAsync(t => userIds.Contains(t.UserId)),
            ["IdempotencyRecords"] = await db.IdempotencyRecords.CountAsync(r => userIds.Contains(r.UserId)),
            ["StepUpAuthorizations"] = await db.StepUpAuthorizations.CountAsync(a => userIds.Contains(a.UserId)),
            ["SubscriberNotices"] = await db.SubscriberNotices.CountAsync(n => userIds.Contains(n.UserId)),
        };
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            host.Dispose();
        }

        // Drop the scratch database, so repeated runs do not leave one behind each.
        SqlConnection.ClearAllPools();
        await using var db = NewContext();
        await db.Database.EnsureDeletedAsync();
    }
}
