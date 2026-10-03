extern alias seeder;

using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Services.Interfaces;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using seeder::AzureBank.Seeder.Pool;

namespace AzureBank.Tests.Fixtures;

/// <summary>
/// A free demo copy made by hand in an API test host's own database: one pool row, and the three
/// users that point at it, created through <see cref="UserManager{TUser}"/> with no password.
/// </summary>
/// <remarks>
/// <para>
/// For the tests of the claim that run on the InMemory provider. The Seeder's builder is not
/// what makes these: it runs in the Seeder's own container, against SQL Server, and dates a
/// copy's ledger with set-based statements, which the API's own code never sends to the InMemory
/// provider. What the claim reads of a copy is all here: the pool row, the owner without a
/// password, and the two contacts carrying the copy's id. Its accounts and its ledger are not, and
/// no test that uses this looks at them; the copies the builder makes are what the SQL Server
/// tests claim (<see cref="DemoPoolDatabase"/>).
/// </para>
/// <para>
/// The identities are drawn by the Seeder's own <see cref="DemoCredentials"/>, so a hand-made
/// copy's handles and addresses have the shape of a built one's.
/// </para>
/// </remarks>
internal sealed record HandMadeDemoCopy(Guid Id, ApplicationUser Owner, ApplicationUser Jane, ApplicationUser Mike)
{
    /// <summary>The handles of the copy's two contacts, in the order a claim answers them.</summary>
    public string[] ContactHandles =>
        [.. new[] { Jane.AzureTag, Mike.AzureTag }.Order(StringComparer.Ordinal)];

    /// <summary>Makes one free copy in <paramref name="api"/>'s database, seeded at <paramref name="createdAt"/> (now when none is given).</summary>
    public static async Task<HandMadeDemoCopy> CreateAsync(CustomWebApplicationFactory api, DateTime? createdAt = null)
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var pinHash = scope.ServiceProvider.GetRequiredService<IPasswordHasher>().HashPin(DemoCopyDefaults.Pin);

        var cast = DemoCredentials.Create();
        var copyId = Guid.CreateVersion7();
        var ownerId = Guid.CreateVersion7();

        // The pool row first, as the builder writes it: the users' foreign key points at it.
        db.DemoCopies.Add(new DemoCopy { Id = copyId, OwnerUserId = ownerId, CreatedAt = createdAt ?? DateTime.UtcNow });
        await db.SaveChangesAsync();

        var owner = await CreateUserAsync(users, ownerId, copyId, cast.Owner, pinHash);
        var jane = await CreateUserAsync(users, Guid.CreateVersion7(), copyId, cast.Jane, pinHash);
        var mike = await CreateUserAsync(users, Guid.CreateVersion7(), copyId, cast.Mike, pinHash);
        return new HandMadeDemoCopy(copyId, owner, jane, mike);
    }

    /// <summary>The copy's pool row as the database holds it now.</summary>
    public async Task<DemoCopy> RowAsync(CustomWebApplicationFactory api)
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        return await db.DemoCopies.AsNoTracking().SingleAsync(c => c.Id == Id);
    }

    /// <summary>The owner's password hash as the database holds it now: null while the copy is free.</summary>
    public async Task<string?> OwnerPasswordHashAsync(CustomWebApplicationFactory api)
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        return await db.Users.AsNoTracking().Where(u => u.Id == Owner.Id).Select(u => u.PasswordHash).SingleAsync();
    }

    private static async Task<ApplicationUser> CreateUserAsync(
        UserManager<ApplicationUser> users, Guid id, Guid copyId, DemoIdentity who, string pinHash)
    {
        var user = new ApplicationUser
        {
            Id = id,
            UserName = id.ToString(),
            Email = who.Email,
            EmailConfirmed = true,
            AzureTag = who.AzureTag,
            FirstName = who.FirstName,
            LastName = who.LastName,
            PinHash = pinHash,
            DemoCopyId = copyId,
        };

        // CreateAsync(user), not CreateAsync(user, password): the password hash stays null.
        var created = await users.CreateAsync(user);
        created.Succeeded.Should().BeTrue(
            "ARRANGE: the copy's {0} is created ({1})",
            who.FirstName, string.Join("; ", created.Errors.Select(e => e.Description)));
        return user;
    }
}
