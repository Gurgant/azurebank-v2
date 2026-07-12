using AzureBank.Infrastructure.Data.ValueGenerators;
using AzureBank.Shared.Constants;


namespace AzureBank.Infrastructure.Data.Configurations;

public class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("RefreshTokens");


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

        // Index for cleanup job (find expired tokens)
        builder.HasIndex(r => r.ExpiresAt);

        // ═══════════════════════════════════════════════════════════════
        // TIMESTAMP - managed by DbContext.UpdateTimestamps()
        // No default SQL needed - application handles this
        // ═══════════════════════════════════════════════════════════════
        builder.Property(r => r.CreatedAt)
            .IsRequired();

        // ═══════════════════════════════════════════════════════════════
        // RELATIONSHIPS
        // ═══════════════════════════════════════════════════════════════


        builder.HasOne(r => r.User)
            .WithMany()
            .HasForeignKey(r => r.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Self-referencing for token rotation chain
        // When token is refreshed, old points to new via ReplacedByTokenId
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
