using AzureBank.Shared.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AzureBank.Infrastructure.Data.Configurations;

/// <summary>
/// The shape of <see cref="SubscriberNotice"/> (ADR-0045).
/// </summary>
public class SubscriberNoticeConfiguration : IEntityTypeConfiguration<SubscriberNotice>
{
    public void Configure(EntityTypeBuilder<SubscriberNotice> builder)
    {
        /*
          THE SHAPE RULE LIVES IN THE DATABASE, for the reason AuditAnchorConfiguration gives: the
          writer is not the only thing that can write. A row marked delivered without a receipt, or
          carrying a receipt while still owed, is a state the tool never produces and the store
          should never accept — declared on the model so the snapshot carries it.

          ⚠️ The InMemory provider ignores CHECK constraints, filtered indexes and cascades entirely,
          so the assertions on this shape belong in the SQL-gated tests and are written there.
        */
        builder.ToTable("SubscriberNotices", t =>
        {
            t.HasCheckConstraint(
                "CK_SubscriberNotices_Delivery",
                "([DeliveredAt] IS NULL AND [DeliveryReceipt] IS NULL) "
                + "OR ([DeliveredAt] IS NOT NULL AND [DeliveryReceipt] IS NOT NULL)");

            // The lease is a pair for the same reason (ADR-0048): a row held until a time by nobody,
            // or by somebody until no time, is a state no runner produces.
            t.HasCheckConstraint(
                "CK_SubscriberNotices_Lease",
                "([LeasedUntil] IS NULL AND [LeasedBy] IS NULL) "
                + "OR ([LeasedUntil] IS NOT NULL AND [LeasedBy] IS NOT NULL)");
        });

        // Assigned by the writer, never by the store (see the entity's remarks).
        builder.HasKey(n => n.Id);
        builder.Property(n => n.Id).ValueGeneratedNever();

        // Same width and reason as AuditEvents.Event: an identifier from SecurityEvents, not a sentence.
        builder.Property(n => n.Event)
            .IsRequired()
            .HasMaxLength(40);

        builder.Property(n => n.OccurredAt)
            .IsRequired();

        /*
          THE MARK IS FENCED, the IdempotencyRecord.ClaimId idiom: every UPDATE that sets DeliveredAt
          carries WHERE DeliveredAt = <original>, so a second run that loaded the row while it was
          still owed loses with DbUpdateConcurrencyException instead of silently overwriting the
          first run's receipt. Whether the InMemory provider enforces a NULLABLE DateTime token is
          measured by the tests, not assumed here.
        */
        builder.Property(n => n.DeliveredAt)
            .IsConcurrencyToken();

        // A file name today; wide enough for a message id if a relay ever supplies one.
        builder.Property(n => n.DeliveryReceipt)
            .HasMaxLength(64);

        /*
          A PLAIN COLUMN, AND NO FOREIGN KEY TO AuditEvents (ADR-0052 D1). The AddSubscriberNotices
          migration decided that in as many words, and the reason survives the reference: a notice
          whose evidence has gone missing must be FOUND rather than refused, and a constraint would
          refuse the very write that makes it missing. Nothing about AuditEvents is touched.

          ⚠️ THE INDEX IS NOT FOR THE EVIDENCE CHECK, and saying it was is how this comment read
          until it was measured. DeliverAsync reads AuditEventId off a notice it has ALREADY
          materialised and then looks AuditEvents up by its PRIMARY KEY; no code path filters
          SubscriberNotices on this column, so that query would never touch this index.

          It is here for the one reader that does: the DUPLICATE-POINTER SWEEP in
          docs/runbooks/pin-enrolment-repudiated.md. ADR-0052 D3 compares (ActorUserId, Event)
          against the named row, which cannot see a re-point to another row of the same user and
          the same kind — but that attack leaves TWO notices naming ONE audit row, and

              SELECT AuditEventId, COUNT(*) FROM SubscriberNotices
              WHERE AuditEventId IS NOT NULL GROUP BY AuditEventId HAVING COUNT(*) > 1

          finds it. That is a scan of exactly the rows this filter keeps, which is also why the
          filter is right: a null here means "written before ADR-0052", and those rows name nothing
          and can collide with nothing.
        */
        builder.Property(n => n.AuditEventId);

        builder.HasIndex(n => n.AuditEventId)
            .HasFilter("[AuditEventId] IS NOT NULL");

        // The runner's name: host, process and a short id. Not an address, not a secret.
        builder.Property(n => n.LeasedBy)
            .HasMaxLength(64);

        // Erasure by cascade: the notice is the account holder's and goes with them (ADR-0045).
        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(n => n.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // What a runner's claim and the operator tool read: the rows still owed, oldest first. The
        // claim also filters on LeasedUntil, which this index does not carry; at this scale the
        // residual filter over the owed rows is cheap, and the index stays as ADR-0045 shipped it.
        builder.HasIndex(n => n.OccurredAt)
            .HasDatabaseName("IX_SubscriberNotices_Pending")
            .HasFilter("[DeliveredAt] IS NULL");

        // What the repudiation runbook and the cascade need.
        builder.HasIndex(n => n.UserId);
    }
}
