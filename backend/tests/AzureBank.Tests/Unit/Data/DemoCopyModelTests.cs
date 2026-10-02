using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Entities;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AzureBank.Tests.Unit.Data;

/// <summary>
/// What the model says about a demo copy that the database's catalogue cannot show. The table, its
/// rules and its indexes are read back from SQL Server in <c>DemoCopySchemaSqlServerTests</c>.
/// </summary>
public class DemoCopyModelTests
{
    private static AzureBankDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AzureBankDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>
    /// A copy's id is the one its writer gave it. EF mints a random id for a <see cref="Guid"/> key
    /// that was left empty unless the model says otherwise, and a copy whose id nobody chose would
    /// be a copy the writer cannot name afterwards.
    /// </summary>
    [Fact]
    public void ACopyAddedWithoutAnId_IsNotGivenOneByTheStore()
    {
        using var db = NewContext();
        var copy = new DemoCopy { OwnerUserId = Guid.CreateVersion7(), CreatedAt = DateTime.UtcNow };
        var role = new IdentityRole<Guid>("control");

        db.DemoCopies.Add(copy);
        db.Add(role);

        copy.Id.Should().Be(Guid.Empty, "the store mints no id for a copy");

        // CONTROL: the same context does mint one for a key it was not told to leave alone.
        role.Id.Should().NotBe(Guid.Empty);
    }
}
