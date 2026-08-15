namespace AzureBank.Infrastructure.Data.Configurations;

public class StepUpAuthorizationConfiguration : IEntityTypeConfiguration<StepUpAuthorization>
{
    public void Configure(EntityTypeBuilder<StepUpAuthorization> builder)
    {
        builder.ToTable("StepUpAuthorizations");

        builder.HasKey(a => a.Id);

        // Every lookup is (Id, UserId): the reference alone must not be enough to spend, or to
        // discover that someone else's authorisation exists. Indexed on UserId because the evidence
        // pack (B3) reads by subject, and because the consume statement filters on it.
        builder.HasIndex(a => a.UserId)
            .HasDatabaseName("IX_StepUpAuthorizations_UserId");

        // Stored as strings, same convention as Transaction.Status and IdempotencyRecord.Status:
        // a migration that reorders an enum must not silently reinterpret existing rows, and this
        // table is meant to be readable years later as evidence.
        builder.Property(a => a.Operation)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(30);

        builder.Property(a => a.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(20);

        // HMAC-SHA256 hex, always exactly 64 characters.
        builder.Property(a => a.BindingHash)
            .IsRequired()
            .HasMaxLength(64)
            .IsFixedLength();

        builder.Property(a => a.CreatedAt)
            .IsRequired();

        builder.Property(a => a.ExpiresAt)
            .IsRequired();

        // No index on ExpiresAt, unlike IdempotencyRecords: nothing sweeps this table. Rows are the
        // Art. 72 evidence and are kept. If a retention policy ever arrives it brings its own index.
    }
}
