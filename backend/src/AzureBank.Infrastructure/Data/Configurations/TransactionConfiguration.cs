using AzureBank.Infrastructure.Data.ValueGenerators;
using AzureBank.Shared.Constants;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AzureBank.Infrastructure.Data.Configurations;

public class TransactionConfiguration : IEntityTypeConfiguration<Transaction>
{
    public void Configure(EntityTypeBuilder<Transaction> builder)
    {
        builder.ToTable("Transactions");


        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id)
            .HasValueGenerator<GuidVersion7ValueGenerator>();


        // TRANSACTION NUMBER - unique, format: TXN-YYYYMMDD-XXXXXX
        builder.Property(t => t.TransactionNumber)
            .IsRequired()
            .HasMaxLength(ValidationRules.TransactionNumberLength);

        builder.HasIndex(t => t.TransactionNumber)
            .IsUnique();


        builder.Property(t => t.Type)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(ValidationRules.TransactionTypeMaxLength);


        // MONEY FIELDS - DECIMAL(19,4) for financial precision
        builder.Property(t => t.Amount)
            .HasPrecision(ValidationRules.MoneyPrecision, ValidationRules.MoneyScale)
            .IsRequired();

        builder.Property(t => t.BalanceBefore)
            .HasPrecision(ValidationRules.MoneyPrecision, ValidationRules.MoneyScale)
            .IsRequired();

        builder.Property(t => t.BalanceAfter)
            .HasPrecision(ValidationRules.MoneyPrecision, ValidationRules.MoneyScale)
            .IsRequired();


        builder.Property(t => t.Description)
            .HasMaxLength(ValidationRules.TransactionDescriptionMaxLength);


        builder.Property(t => t.RecipientAzureTag)
            .HasMaxLength(ValidationRules.AzureTagMaxLength);

        builder.Property(t => t.SenderAzureTag)
            .HasMaxLength(ValidationRules.AzureTagMaxLength);


        builder.Property(t => t.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(ValidationRules.TransactionStatusMaxLength)
            .HasDefaultValue(TransactionStatus.Completed)
            .HasSentinel(TransactionStatus.Unspecified);

        // ═══════════════════════════════════════════════════════════════
        // TIMESTAMP - managed by DbContext.UpdateTimestamps()
        // No default SQL needed - application handles this
        // ═══════════════════════════════════════════════════════════════
        builder.Property(t => t.CreatedAt)
            .IsRequired();

        // ═══════════════════════════════════════════════════════════════
        // RELATIONSHIPS
        // ═══════════════════════════════════════════════════════════════


        builder.HasOne(t => t.Account)
            .WithMany(a => a.Transactions)
            .HasForeignKey(t => t.AccountId)
            .OnDelete(DeleteBehavior.Restrict);

        // Self-referencing relationship for transfer pairs
        builder.HasOne(t => t.RelatedTransaction)
            .WithOne()
            .HasForeignKey<Transaction>(t => t.RelatedTransactionId)
            .OnDelete(DeleteBehavior.Restrict);

        // ═══════════════════════════════════════════════════════════════
        // INDEXES
        // ═══════════════════════════════════════════════════════════════

        // Account lookup
        builder.HasIndex(t => t.AccountId);

        // Time-based queries (recent first)
        builder.HasIndex(t => t.CreatedAt)
            .IsDescending();

        // Compound index for paginated account history
        builder.HasIndex(t => new { t.AccountId, t.CreatedAt })
            .IsDescending(false, true)
            .HasDatabaseName("IX_Transactions_AccountId_CreatedAt");

        // Related transaction lookup (filtered - only transfers)
        builder.HasIndex(t => t.RelatedTransactionId)
            .HasFilter("[RelatedTransactionId] IS NOT NULL");
    }
}
