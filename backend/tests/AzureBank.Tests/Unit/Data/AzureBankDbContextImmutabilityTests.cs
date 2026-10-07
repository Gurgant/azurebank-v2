using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace AzureBank.Tests.Unit.Data;

/// <summary>
/// Proves the write-once immutability guard runs on the EF Core *funnel*
/// SaveChanges overloads — SaveChanges(bool) and SaveChangesAsync(bool, ct) —
/// which every other entry point delegates to. Before the guard was moved onto
/// the funnels, a direct SaveChanges(acceptAllChangesOnSuccess: ...) call
/// bypassed it entirely (C-L2).
/// </summary>
public class AzureBankDbContextImmutabilityTests
{
    private static AzureBankDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AzureBankDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static Transaction NewTransaction(Guid accountId) => new()
    {
        Id = Guid.NewGuid(),
        TransactionNumber = "TXN-20260714-000001",
        AccountId = accountId,
        Type = TransactionType.Deposit,
        Amount = 100m,
        BalanceBefore = 0m,
        BalanceAfter = 100m,
        Status = TransactionStatus.Completed,
        Account = null! // FK not enforced on InMemory; nav not needed here
    };

    private static Transaction PersistTransaction(AzureBankDbContext ctx)
    {
        var txn = NewTransaction(Guid.NewGuid());
        ctx.Transactions.Add(txn);
        ctx.SaveChanges();
        return txn;
    }

    [Fact]
    public void SaveChanges_BoolFunnel_ModifiedFinancialField_IsBlocked()
    {
        using var ctx = NewContext();
        var txn = PersistTransaction(ctx);

        txn.Amount += 1m; // mutate a write-once financial field

        var act = () => ctx.SaveChanges(acceptAllChangesOnSuccess: true);

        act.Should().Throw<InvalidOperationException>().WithMessage("*immutable*");
    }

    [Fact]
    public async Task SaveChangesAsync_BoolFunnel_ModifiedFinancialField_IsBlocked()
    {
        using var ctx = NewContext();
        var txn = PersistTransaction(ctx);

        txn.Amount += 1m;

        var act = () => ctx.SaveChangesAsync(acceptAllChangesOnSuccess: false, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*immutable*");
    }

    [Fact]
    public void SaveChanges_BoolFunnel_AddedTransaction_Succeeds()
    {
        using var ctx = NewContext();
        ctx.Transactions.Add(NewTransaction(Guid.NewGuid()));

        // The guard blocks Modified/Deleted only: a legitimate Added financial
        // record must still persist through the funnel overload.
        var act = () => ctx.SaveChanges(acceptAllChangesOnSuccess: true);

        act.Should().NotThrow();
        ctx.Transactions.Count().Should().Be(1);
    }

    [Fact]
    public void SaveChanges_NoOpModifiedTransaction_DoesNotFalsePositive()
    {
        using var ctx = NewContext();
        var txn = PersistTransaction(ctx);

        // Regression proof for the Gemini review note that a Transaction marked
        // Modified with zero actually-modified columns might trip a false
        // immutability violation. It cannot here: forcing Modified and then
        // clearing every column's IsModified makes EF downgrade the entity to
        // Unchanged, so EnforceTransactionImmutability never even inspects it.
        // The guard is already correct; no count==0 special case is needed.
        var entry = ctx.Entry(txn);
        entry.State = EntityState.Modified;
        foreach (var property in entry.Properties)
        {
            property.IsModified = false;
        }

        var act = () => ctx.SaveChanges(acceptAllChangesOnSuccess: true);

        act.Should().NotThrow();
    }
}
