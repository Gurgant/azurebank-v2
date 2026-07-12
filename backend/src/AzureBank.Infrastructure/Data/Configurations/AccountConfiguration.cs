using AzureBank.Infrastructure.Data.ValueGenerators;
using AzureBank.Shared.Constants;


namespace AzureBank.Infrastructure.Data.Configurations;

public class AccountConfiguration : IEntityTypeConfiguration<Account>
{
    public void Configure(EntityTypeBuilder<Account> builder)
    {
        builder.ToTable("Accounts");

        // ═══════════════════════════════════════════════════════════════
        // PRIMARY KEY - UUID v7 (time-sortable, server-generated not on db)
        // ═══════════════════════════════════════════════════════════════
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id)
            .HasValueGenerator<GuidVersion7ValueGenerator>();

        // ═══════════════════════════════════════════════════════════════
        // ACCOUNT NUMBER - unique, format: AB-XXXX-XXXX-XX
        // ═══════════════════════════════════════════════════════════════
        builder.Property(a => a.AccountNumber)
            .IsRequired()
            .HasMaxLength(ValidationRules.AccountNumberLength);

        builder.HasIndex(a => a.AccountNumber)
            .IsUnique()
            .HasFilter("[IsDeleted] = 0");

        // ═══════════════════════════════════════════════════════════════
        // ACCOUNT NAME
        // ═══════════════════════════════════════════════════════════════
        builder.Property(a => a.Name)
            .IsRequired()
            .HasMaxLength(ValidationRules.AccountNameMaxLength);

        // ═══════════════════════════════════════════════════════════════
        // ACCOUNT TYPE - stored as string for readability
        // ═══════════════════════════════════════════════════════════════
        builder.Property(a => a.Type)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(ValidationRules.AccountTypeMaxLength);


        // BALANCE - DECIMAL(19,4) for financial precision
        builder.Property(a => a.Balance)
            .HasPrecision(ValidationRules.MoneyPrecision, ValidationRules.MoneyScale)
            .HasDefaultValue(0m);


        // IS PRIMARY - only one per user can be true
        builder.Property(a => a.IsPrimary)
            .IsRequired()
            .HasDefaultValue(false);

        // Filtered unique index: ensures max ONE primary account per user
        builder.HasIndex(a => new { a.UserId, a.IsPrimary })
            .IsUnique()
            .HasFilter("[IsPrimary] = 1 AND [IsDeleted] = 0")
            .HasDatabaseName("UX_Accounts_UserId_Primary");


        // ROW VERSION - optimistic concurrency control
        builder.Property(a => a.RowVersion)
            .IsRowVersion();

        // ═══════════════════════════════════════════════════════════════
        // TIMESTAMPS - managed by DbContext.UpdateTimestamps()
        // No default SQL needed - application handles this
        // ═══════════════════════════════════════════════════════════════
        builder.Property(a => a.CreatedAt)
            .IsRequired();

        builder.Property(a => a.UpdatedAt)
            .IsRequired();


        // SOFT DELETE
        builder.Property(a => a.IsDeleted)
            .IsRequired()
            .HasDefaultValue(false);

        // ═══════════════════════════════════════════════════════════════
        // RELATIONSHIPS
        // ═══════════════════════════════════════════════════════════════


        builder.HasOne(a => a.User)
            .WithMany(u => u.Accounts)
            .HasForeignKey(a => a.UserId)
            .OnDelete(DeleteBehavior.Restrict);


        builder.HasMany(a => a.Transactions)
            .WithOne(t => t.Account)
            .HasForeignKey(t => t.AccountId)
            .OnDelete(DeleteBehavior.Restrict);

        // ═══════════════════════════════════════════════════════════════
        // INDEXES
        // ═══════════════════════════════════════════════════════════════

        // User lookup index (filtered by soft delete)
        builder.HasIndex(a => a.UserId)
            .HasFilter("[IsDeleted] = 0");
    }
}
