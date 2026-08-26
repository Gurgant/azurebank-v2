using AzureBank.Shared.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AzureBank.Infrastructure.Data;

public class AzureBankDbContext : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>
{
    private readonly TimeProvider _clock;
    private readonly IAuditChain? _auditChain;

    /// <param name="options">Standard EF Core options.</param>
    /// <param name="timeProvider">
    /// Optional deliberately. It defaults to <see cref="TimeProvider.System"/>, which is the correct
    /// production behaviour, so the fourteen existing construction sites keep compiling and no DI
    /// registration is required for the app to be right. Tests that need an exact instant pass a
    /// <c>FakeTimeProvider</c> here.
    ///
    /// <para>
    /// No <c>AddSingleton(TimeProvider.System)</c> is registered yet, on purpose: nothing would
    /// resolve it, and a registration nothing consumes is the same dead-setting defect as the BFF's
    /// <c>MaxPinAttempts</c>. The registration lands when a service — not just this context —
    /// needs it.
    /// </para>
    /// </param>
    /// <param name="auditChain">
    /// Fills the tamper-evident chain on any <see cref="AuditEvent"/> saved through this context
    /// (ADR-0044). Optional in the SIGNATURE only, so the fourteen existing direct construction sites
    /// keep compiling — but NOT optional in effect: saving an audit row without it throws below,
    /// rather than writing a row with an empty hash that would look audited and prove nothing.
    /// </param>
    public AzureBankDbContext(
        DbContextOptions<AzureBankDbContext> options,
        TimeProvider? timeProvider = null,
        IAuditChain? auditChain = null)
        : base(options)
    {
        _clock = timeProvider ?? TimeProvider.System;
        _auditChain = auditChain;
    }

    // Note: Users are accessed via Set<ApplicationUser>()
    // inherited from IdentityDbContext
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<StepUpAuthorization> StepUpAuthorizations => Set<StepUpAuthorization>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    /// <summary>
    /// What the audit chain looked like each time somebody ran the verifier. Insert-only: nothing
    /// updates a record after it is written, which is what makes "any UPDATE here is tampering" a
    /// rule rather than a hope.
    /// </summary>
    public DbSet<AuditAnchor> AuditAnchors => Set<AuditAnchor>();

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

        var chain = RequireAuditChain();
        if (!NeedsOwnChainTransaction())
        {
            chain.Apply(this);
            return base.SaveChanges(acceptAllChangesOnSuccess);
        }

        return Database.CreateExecutionStrategy().Execute(() =>
        {
            using var owned = Database.BeginTransaction();
            chain.Apply(this);
            var written = base.SaveChanges(acceptAllChangesOnSuccess);
            owned.Commit();
            return written;
        });
    }

    public override async Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnforceTransactionImmutability();
        UpdateTimestamps();

        var chain = RequireAuditChain();
        if (!NeedsOwnChainTransaction())
        {
            await chain.ApplyAsync(this, cancellationToken);
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }

        return await Database.CreateExecutionStrategy().ExecuteAsync(async ct =>
        {
            await using var owned = await Database.BeginTransactionAsync(ct);
            await chain.ApplyAsync(this, ct);
            var written = await base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);
            await owned.CommitAsync(ct);
            return written;
        }, cancellationToken);
    }

    /*
      THE LOCK NEEDS A TRANSACTION TO BE HELD BY, and this is a CORRECTION of what the first version
      of this class claimed. That version asserted the funnel was already "inside the transaction EF
      is using" and applied the chain immediately before base.SaveChanges. It is not: EF opens its
      implicit transaction INSIDE SaveChanges, so the tail read ran in its own auto-committed
      statement and released UPDLOCK/HOLDLOCK before the INSERT was ever sent. Measured on SQL Server
      with 24 concurrent writers: "Cannot insert duplicate key row in object 'dbo.AuditEvents' with
      unique index 'IX_AuditEvents_Sequence'. The duplicate key value is (2)" — the fork the lock
      exists to prevent, caught only because the unique index made it loud instead of silent.

      So the transaction is opened here, before the tail is read, and committed after the insert.

      AND IT GOES THROUGH THE EXECUTION STRATEGY, which is a SECOND correction and one that only the
      running API could produce. EnableRetryOnFailure is on in production, and EF refuses a
      user-initiated transaction under a retrying strategy: every audited request answered 500 with
      "The configured execution strategy 'SqlServerRetryingExecutionStrategy' does not support
      user-initiated transactions", while the whole suite stayed green — because
      CustomWebApplicationFactory rebuilds the DbContext registration WITHOUT the retrying strategy.
      AuthService.RegisterAsync already had to solve exactly this; the idiom here is deliberately
      the same one. AuditChainRetryingStrategySqlServerTests is what stops it coming back.

      No transaction is opened at all — and the ordinary write path is untouched — when:
        - the save carries no audit row;
        - a caller already has an explicit transaction: it is the one holding the lock, and
          committing it here would end the caller's unit of work early;
        - the provider is not relational (the InMemory provider, where ~585 tests run): it has no
          transactions and no locks, and IsRelational() rather than IsInMemory() keeps the InMemory
          package out of the production API, the same choice IdempotencyCleanupService made.
    */
    private bool NeedsOwnChainTransaction() =>
        Database.CurrentTransaction is null
        && Database.IsRelational()
        && ChangeTracker.Entries<AuditEvent>().Any(e => e.State == EntityState.Added);

    /*
      The chain runs HERE, in the same funnel as the immutability guard and the timestamps, and for
      the same stated reason: no call path can bypass it. It also has to be here rather than in
      IAuditService — the tail must be read under a lock inside the transaction SaveChanges is already
      using, and AccountService.DeleteAccountAsync has no explicit transaction for the writer to
      borrow. See AuditChain's remarks for why a SaveChangesInterceptor was rejected (the test host
      rebuilds the DbContext registration and would silently drop it).
    */
    private IAuditChain RequireAuditChain()
    {
        if (_auditChain is not null)
        {
            return _auditChain;
        }

        var writingAudit = ChangeTracker.Entries<AuditEvent>().Any(e => e.State == EntityState.Added);
        if (!writingAudit)
        {
            // Nothing to chain. The fourteen contexts constructed by hand in tests that never touch
            // AuditEvents stay valid, which is why the parameter is optional at all.
            return NoAuditRows.Instance;
        }

        throw new InvalidOperationException(
            "An AuditEvent was added to a DbContext constructed without an IAuditChain, so its hash "
            + "chain cannot be computed. Resolve AzureBankDbContext from DI, or pass an IAuditChain. "
            + "Writing the row unchained is refused deliberately: a row with an empty RowHash reads "
            + "as audited and proves nothing.");
    }

    /// <summary>No-op used only when there are no audit rows to chain.</summary>
    private sealed class NoAuditRows : IAuditChain
    {
        internal static readonly NoAuditRows Instance = new();
        public Task ApplyAsync(DbContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Apply(DbContext context) { }
        public Task<AuditChainVerification> VerifyAsync(DbContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AuditChainVerification(0, null, null));
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

        /*
          ANCHORS ARE INSERT-ONLY, WITH NO WRITE-ONCE EXCEPTION. Transactions get one above, for a
          circular foreign key no provider can order; nothing forces the equivalent here, and
          refusing every update is what lets the design say "any UPDATE against AuditAnchors is
          tampering" without a caveat. It is also why the timestamp token that will bind an anchor to
          an instant arrives in its own table rather than filling a reserved column: a nullable slot
          somebody later populates is a legitimate-looking UPDATE path on an append-only table.

          ⚠️ STATED LIMIT: this defends against our own future code, never against the adversary. The
          attacker this table exists for uses raw SQL and never passes through the change tracker.
        */
        if (ChangeTracker.Entries<AuditAnchor>()
            .Any(e => e.State is EntityState.Deleted or EntityState.Modified))
        {
            throw new InvalidOperationException(
                "Audit anchors are insert-only. A record says what the chain looked like at an "
                + "instant, so changing one after the fact is not an update -- it is a different "
                + "claim about a moment that has passed.");
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
    /// Automatically update CreatedAt and UpdatedAt timestamps.
    ///
    /// <para>
    /// ONE clock read per save, captured once below. It used to read
    /// <c>DateTime.UtcNow</c> six times, and two of those cost real accuracy:
    /// <c>CreatedAt == UpdatedAt</c> on an insert was true only by clock granularity, and the read
    /// for <c>Transaction</c> sat INSIDE its loop — so the two legs of a transfer, one event, were
    /// stamped with two independent instants. Anything that later reconstructs a transfer from its
    /// rows would see them as two events milliseconds apart.
    /// </para>
    /// </summary>
    private void UpdateTimestamps()
    {
        var now = _clock.GetUtcNow().UtcDateTime;

        var entries = ChangeTracker.Entries<BaseEntity>();

        foreach (var entry in entries)
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Entity.CreatedAt = now;
                    entry.Entity.UpdatedAt = now;
                    break;

                case EntityState.Modified:
                    entry.Entity.UpdatedAt = now;
                    break;
            }
        }

        // Handle Transaction entity (doesn't inherit BaseEntity)
        var transactionEntries = ChangeTracker.Entries<Transaction>()
            .Where(e => e.State == EntityState.Added);

        foreach (var entry in transactionEntries)
        {
            entry.Entity.CreatedAt = now;
        }

        // Handle ApplicationUser entity (inherits IdentityUser, not BaseEntity)
        var userEntries = ChangeTracker.Entries<ApplicationUser>();

        foreach (var entry in userEntries)
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Entity.CreatedAt = now;
                    entry.Entity.UpdatedAt = now;
                    break;

                case EntityState.Modified:
                    entry.Entity.UpdatedAt = now;
                    break;
            }
        }
    }
}
