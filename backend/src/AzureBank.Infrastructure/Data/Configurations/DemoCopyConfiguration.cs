using AzureBank.Shared.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AzureBank.Infrastructure.Data.Configurations;

/// <summary>
/// The shape of <see cref="DemoCopy"/>: the pool's one table.
/// </summary>
public class DemoCopyConfiguration : IEntityTypeConfiguration<DemoCopy>
{
    public void Configure(EntityTypeBuilder<DemoCopy> builder)
    {
        /*
          THE TWO RULES LIVE IN THE DATABASE, for the reason AuditAnchorConfiguration gives: the code
          is not the only thing that can write. A claim that set its instant without its id, or a
          free copy marked deleted, is a state every later statement would have to defend against.

          ⚠️ The InMemory provider ignores CHECK constraints, filtered indexes and foreign keys, so
          the assertions on this shape are in the SQL-gated tests (DemoCopySchemaSqlServerTests).
        */
        builder.ToTable("DemoCopies", t =>
        {
            // A claim is whole: its instant and its id are set together or not at all.
            t.HasCheckConstraint(
                "CK_DemoCopies_ClaimIsWhole",
                "([ClaimedAt] IS NULL AND [ClaimId] IS NULL) "
                + "OR ([ClaimedAt] IS NOT NULL AND [ClaimId] IS NOT NULL)");

            // Only a copy somebody claimed is kept as a record; a free copy is deleted with its row.
            t.HasCheckConstraint(
                "CK_DemoCopies_DeletedWasClaimed",
                "[DeletedAt] IS NULL OR [ClaimedAt] IS NOT NULL");
        });

        // Minted by the Seeder, never by the store.
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();

        // One copy per owner. NO FOREIGN KEY, deliberately: the key goes the other way
        // (AspNetUsers.DemoCopyId), and the record of a deleted copy outlives its owner.
        builder.HasIndex(c => c.OwnerUserId)
            .IsUnique();

        // A claim id names one copy, so "was this claim written?" has one answer. Free copies all
        // carry null, and any number of them may exist.
        builder.HasIndex(c => c.ClaimId)
            .IsUnique()
            .HasFilter("[ClaimId] IS NOT NULL");

        // HMAC-SHA256: always exactly 32 bytes.
        builder.Property(c => c.ClientKey)
            .HasMaxLength(32)
            .IsFixedLength();

        // The default is the database's, so a row written by a statement that names no count
        // starts at 0 as well.
        builder.Property(c => c.Writes)
            .HasDefaultValue(0);

        // The free copies by age, without the claimed ones or the records of deleted copies, which
        // only grow in number. "Newest" is read from CreatedAt: SQL Server sorts a uniqueidentifier
        // by its last bytes first, so the id is no order.
        builder.HasIndex(c => c.CreatedAt)
            .HasDatabaseName("IX_DemoCopies_Free")
            .HasFilter("[ClaimedAt] IS NULL");

        // One client's claims in a span of time. Filtered: free copies and the records of deleted
        // ones carry no client key.
        builder.HasIndex(c => new { c.ClientKey, c.ClaimedAt })
            .HasDatabaseName("IX_DemoCopies_ClientKey")
            .HasFilter("[ClientKey] IS NOT NULL");
    }
}
