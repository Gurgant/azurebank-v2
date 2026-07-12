using AzureBank.Shared.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace AzureBank.Infrastructure.Data;

public class AzureBankDbContext : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>
{
    public AzureBankDbContext(DbContextOptions<AzureBankDbContext> options)
        : base(options)
    {
    }

    // Note: Users are accessed via Set<ApplicationUser>()
    // inherited from IdentityDbContext
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AzureBankDbContext).Assembly);

        //Global query filter for soft deletes
        modelBuilder.Entity<Account>().HasQueryFilter(a => !a.IsDeleted);
    }

    public override int SaveChanges()
    {
        EnforceTransactionImmutability();
        UpdateTimestamps();
        return base.SaveChanges();
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        EnforceTransactionImmutability();
        UpdateTimestamps();
        return await base.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Enforce immutability on Transaction entities.
    /// Transactions are financial records and cannot be modified or deleted.
    /// </summary>
    private void EnforceTransactionImmutability()
    {
        var invalidOperations = ChangeTracker.Entries<Transaction>()
            .Where(e => e.State is EntityState.Modified or EntityState.Deleted);

        if (invalidOperations.Any())
        {
            throw new InvalidOperationException(
                "Transactions are immutable. Financial records cannot be modified or deleted.");
        }
    }

    /// <summary>
    /// Automatically update CreatedAt and UpdatedAt timestamps
    /// </summary>
    private void UpdateTimestamps()
    {
        var entries = ChangeTracker.Entries<BaseEntity>();

        foreach (var entry in entries)
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Entity.CreatedAt = DateTime.UtcNow;
                    entry.Entity.UpdatedAt = DateTime.UtcNow;
                    break;

                case EntityState.Modified:
                    entry.Entity.UpdatedAt = DateTime.UtcNow;
                    break;
            }
        }

        // Handle Transaction entity (doesn't inherit BaseEntity)
        var transactionEntries = ChangeTracker.Entries<Transaction>()
            .Where(e => e.State == EntityState.Added);

        foreach (var entry in transactionEntries)
        {
            entry.Entity.CreatedAt = DateTime.UtcNow;
        }

        // Handle ApplicationUser entity (inherits IdentityUser, not BaseEntity)
        var userEntries = ChangeTracker.Entries<ApplicationUser>();

        foreach (var entry in userEntries)
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Entity.CreatedAt = DateTime.UtcNow;
                    entry.Entity.UpdatedAt = DateTime.UtcNow;
                    break;

                case EntityState.Modified:
                    entry.Entity.UpdatedAt = DateTime.UtcNow;
                    break;
            }
        }
    }
}
