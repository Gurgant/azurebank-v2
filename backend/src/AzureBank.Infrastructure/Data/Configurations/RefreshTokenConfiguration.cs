using AzureBank.Infrastructure.Data.ValueGenerators;
using AzureBank.Shared.Constants;


namespace AzureBank.Infrastructure.Data.Configurations;

public class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("RefreshTokens", t =>
        {
            // The runbook's SQL writes RevokedReason by hand (ADR-0057 §5), and EF cannot read back
            // a name the enum does not have: a typo there would turn every later renewal of that
            // grant into a 500 instead of the uniform 401. The database refuses the typo instead.
            // Not seen on InMemory, which ignores CHECK constraints; the SQL-gated tests are where
            // it holds.
            t.HasCheckConstraint(
                "CK_RefreshTokens_RevokedReason",
                "[RevokedReason] IS NULL OR [RevokedReason] IN "
                + "('SessionEnded', 'SignOutEverywhere', 'ReuseContainment', 'Incident', 'Deployment')");
        });


        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id)
            .HasValueGenerator<GuidVersion7ValueGenerator>();

        // ═══════════════════════════════════════════════════════════════
        // TOKEN HASH - SHA256 hash of the actual token (NEVER store plain!)
        // ═══════════════════════════════════════════════════════════════
        builder.Property(r => r.TokenHash)
            .IsRequired()
            .HasMaxLength(ValidationRules.TokenHashLength);

        builder.HasIndex(r => r.TokenHash)
            .IsUnique();

        // ═══════════════════════════════════════════════════════════════
        // SECURITY TRACKING - for theft detection
        // ═══════════════════════════════════════════════════════════════
        builder.Property(r => r.IpAddress)
            .IsRequired()
            .HasMaxLength(ValidationRules.IpAddressMaxLength);

        builder.Property(r => r.UserAgent)
            .IsRequired()
            .HasMaxLength(ValidationRules.UserAgentMaxLength);

        // ═══════════════════════════════════════════════════════════════
        // EXPIRATION & REVOCATION
        // ═══════════════════════════════════════════════════════════════
        builder.Property(r => r.ExpiresAt)
            .IsRequired();

        builder.Property(r => r.RevokedAt);

        // By name, the convention of AuditEvent.Outcome and Transaction.Status: a reason is read by
        // people and by the runbook's SQL, and reordering the enum must never reinterpret a row.
        // Nullable, because a legacy row revoked before the column existed has no reason.
        builder.Property(r => r.RevokedReason)
            .HasConversion<string>()
            .HasMaxLength(32);

        // Index for cleanup job (find expired tokens)
        builder.HasIndex(r => r.ExpiresAt);

        // ═══════════════════════════════════════════════════════════════
        // TIMESTAMP - managed by DbContext.UpdateTimestamps()
        // No default SQL needed - application handles this
        // ═══════════════════════════════════════════════════════════════
        builder.Property(r => r.CreatedAt)
            .IsRequired();

        // A rowversion. It made rotation un-forkable while renewal rotated; since PR-1 a renewal
        // writes nothing, so it changes only when the grant is revoked (ADR-0057 §10 O2g reads it).
        builder.Property(r => r.RowVersion)
            .IsRowVersion();

        // ═══════════════════════════════════════════════════════════════
        // RELATIONSHIPS
        // ═══════════════════════════════════════════════════════════════


        builder.HasOne(r => r.User)
            .WithMany()
            .HasForeignKey(r => r.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Self-reference of the LEGACY rotation chain: old rows point at their successor. Nothing
        // writes it since PR-1 (ADR-0057 §4.3); the column and its key stay for the rows written
        // before.
        builder.HasOne(r => r.ReplacedByToken)
            .WithOne()
            .HasForeignKey<RefreshToken>(r => r.ReplacedByTokenId)
            .OnDelete(DeleteBehavior.Restrict);

        // ═══════════════════════════════════════════════════════════════
        // INDEXES
        // ═══════════════════════════════════════════════════════════════

        // User lookup (find all tokens for a user - for revoke all)
        builder.HasIndex(r => r.UserId);

        // Compound index for active token lookup
        builder.HasIndex(r => new { r.UserId, r.RevokedAt, r.ExpiresAt })
            .HasDatabaseName("IX_RefreshTokens_UserId_Active");
    }
}
