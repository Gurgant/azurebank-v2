namespace AzureBank.Infrastructure.Data.Configurations;

public class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> builder)
    {
        builder.ToTable("IdempotencyRecords");

        // ═══════════════════════════════════════════════════════════════
        // COMPOSITE PRIMARY KEY - the uniqueness IS the distributed lock:
        // exactly one concurrent claim INSERT can succeed (ADR-0009).
        // Key scope: per user, per logical endpoint.
        // ═══════════════════════════════════════════════════════════════
        builder.HasKey(r => new { r.UserId, r.Endpoint, r.Key });

        // Logical endpoint identity: "{METHOD} {RoutePattern.RawText}"
        builder.Property(r => r.Endpoint)
            .IsRequired()
            .HasMaxLength(100);

        // ═══════════════════════════════════════════════════════════════
        // CLAIM ID - fencing/owner token (Guid concurrency token; enforced
        // by SQL Server AND the InMemory provider, unlike rowversion).
        // Any UPDATE/DELETE carries WHERE ClaimId = <original>, so a stale
        // claimant loses with DbUpdateConcurrencyException instead of
        // silently double-executing.
        // ═══════════════════════════════════════════════════════════════
        builder.Property(r => r.ClaimId)
            .IsConcurrencyToken();

        // HMAC-SHA256 hex fingerprint of the raw request body (64 chars)
        builder.Property(r => r.RequestHash)
            .IsRequired()
            .HasMaxLength(64)
            .IsFixedLength();

        // Status stored as string for readability (same convention as
        // Transaction.Status / Account.Type)
        builder.Property(r => r.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(r => r.ResponseContentType)
            .HasMaxLength(100);

        // ResponseBody: server-generated envelope (~1KB), nvarchar(max) default

        builder.Property(r => r.CreatedAt)
            .IsRequired();

        builder.Property(r => r.ExpiresAt)
            .IsRequired();

        // Cleanup sweep scans by expiry
        builder.HasIndex(r => r.ExpiresAt)
            .HasDatabaseName("IX_IdempotencyRecords_ExpiresAt");
    }
}
