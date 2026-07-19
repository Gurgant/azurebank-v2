using AzureBank.Shared.Constants;
using AzureBank.Shared.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AzureBank.Infrastructure.Data.Configurations;

public class ApplicationUserConfiguration : IEntityTypeConfiguration<ApplicationUser>
{
    public void Configure(EntityTypeBuilder<ApplicationUser> builder)
    {
        // Note: IdentityDbContext already configures:
        // - Table name (AspNetUsers)
        // - Primary key (Id)
        // - Email, UserName, PasswordHash, etc.
        // We only configure our CUSTOM properties here.

        // AzureTag - public @username for transfers (stored without @)
        builder.Property(e => e.AzureTag)
            .HasMaxLength(ValidationRules.AzureTagMaxLength)
            .IsRequired();

        builder.HasIndex(e => e.AzureTag)
            .IsUnique();

        // Email must be unique at the DATABASE level, not just via Identity's advisory
        // RequireUniqueEmail (a FindByEmailAsync check that two concurrent registrations can
        // both pass). This overrides the base Identity EmailIndex (non-unique) so a same-email
        // race loses the unique-index write and is neutralised to a 409 (ADR-0013) instead of
        // creating a duplicate account. NULL-filtered because NormalizedEmail is nullable.
        builder.HasIndex(e => e.NormalizedEmail)
            .IsUnique()
            .HasFilter("[NormalizedEmail] IS NOT NULL");

        // Name fields
        builder.Property(e => e.FirstName)
            .HasMaxLength(ValidationRules.FirstNameMaxLength)
            .IsRequired();

        builder.Property(e => e.LastName)
            .HasMaxLength(ValidationRules.LastNameMaxLength)
            .IsRequired();

        // PIN hash for step-up authentication
        builder.Property(e => e.PinHash)
            .HasMaxLength(ValidationRules.PinHashMaxLength);

        // Timestamps
        builder.Property(e => e.CreatedAt)
            .IsRequired();


        builder.HasMany(e => e.Accounts)
            .WithOne(a => a.User)
            .HasForeignKey(a => a.UserId)
            .OnDelete(DeleteBehavior.Restrict); // Don't cascade delete accounts - why? soft delete
    }
}
