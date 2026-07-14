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
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AzureBankDbContext).Assembly);

        //Global query filter for soft deletes
        modelBuilder.Entity<Account>().HasQueryFilter(a => !a.IsDeleted);
    }

    // Override the EF Core funnel overloads (the ones every other
    // SaveChanges/SaveChangesAsync entry point ultimately delegates to) so no
    // call path — including a direct SaveChanges(acceptAllChangesOnSuccess: false)
    // — can bypass the immutability guard or timestamping. The parameterless and
    // CancellationToken overloads are NOT overridden: their base implementations
    // delegate to these funnels, so the guard still runs exactly once per save.
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnforceTransactionImmutability();
        UpdateTimestamps();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override async Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnforceTransactionImmutability();
        UpdateTimestamps();
        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// Enforce immutability on Transaction entities.
    /// Transactions are financial records and cannot be modified or deleted.
    ///
    /// Single exception (write-once): RelatedTransactionId may go from null
    /// to a value. Transfer pairs reference EACH OTHER, and two mutually
    /// referencing inserts are a circular FK dependency no relational
    /// provider can order (EF throws "circular dependency was detected") —
    /// so the pair is inserted one-directional and the back-link is written
    /// once afterwards, inside the same database transaction. All financial
    /// fields remain immutable; an already-set link cannot change.
    /// </summary>
    private void EnforceTransactionImmutability()
    {
        var invalidOperations = ChangeTracker.Entries<Transaction>()
            .Where(e => e.State == EntityState.Deleted
                || (e.State == EntityState.Modified && !IsWriteOnceLinkUpdate(e)));

        if (invalidOperations.Any())
        {
            throw new InvalidOperationException(
                "Transactions are immutable. Financial records cannot be modified or deleted.");
        }
    }

    private static bool IsWriteOnceLinkUpdate(
        Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<Transaction> entry)
    {
        var modified = entry.Properties.Where(p => p.IsModified).ToList();
        return modified.Count == 1
            && modified[0].Metadata.Name == nameof(Transaction.RelatedTransactionId)
            && entry.Property(t => t.RelatedTransactionId).OriginalValue is null;
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
