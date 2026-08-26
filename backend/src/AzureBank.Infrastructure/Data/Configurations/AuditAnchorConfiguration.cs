using AzureBank.Shared.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AzureBank.Infrastructure.Data.Configurations;

public class AuditAnchorConfiguration : IEntityTypeConfiguration<AuditAnchor>
{
    public void Configure(EntityTypeBuilder<AuditAnchor> builder)
    {
        /*
          THE SHAPE RULES LIVE IN THE DATABASE, not only in the writer, because the writer is not the
          only thing that can insert. They are declared on the MODEL rather than hand-written into
          the migration so the snapshot carries them and a later migration does not try to "repair" a
          difference it cannot see.

          ⚠️ Stated honestly: the InMemory provider ignores CHECK constraints entirely, so the
          hundreds of tests that run on it are blind to all three. That is the green-and-false shape
          this project treats as the worst one, which is why the shape assertions belong in the
          SQL-gated tests and are written there.
        */
        builder.ToTable("AuditAnchors", t =>
        {
            // A gap marker covers NOTHING, and the database says so rather than trusting the writer:
            // it is what stops a flipped Kind from carrying a coverage claim it never had.
            t.HasCheckConstraint(
                "CK_AuditAnchors_Shape",
                "([Kind] = 'Anchor' AND [LowestCoveredSequence] IS NOT NULL "
                + "AND [CoveredThroughSequence] IS NOT NULL AND [CoveredRowCount] IS NOT NULL "
                + "AND [TailRowHash] IS NOT NULL AND [AnchoredValue] IS NOT NULL) "
                + "OR ([Kind] = 'GapMarker' AND [LowestCoveredSequence] IS NULL "
                + "AND [CoveredThroughSequence] IS NULL AND [CoveredRowCount] IS NULL "
                + "AND [TailRowHash] IS NULL AND [AnchoredValue] IS NULL)");

            // Exactly one record has no predecessor, and it is the first one. Anything else is a
            // chain that starts twice.
            t.HasCheckConstraint(
                "CK_AuditAnchors_FirstLink",
                "([AnchorSequence] = 1 AND [PreviousAnchorPayloadHash] IS NULL) "
                + "OR ([AnchorSequence] > 1 AND [PreviousAnchorPayloadHash] IS NOT NULL)");

            t.HasCheckConstraint(
                "CK_AuditAnchors_Range",
                "[AnchorSequence] >= 1 AND ([CoveredRowCount] IS NULL "
                + "OR ([CoveredRowCount] >= 1 AND [LowestCoveredSequence] <= [CoveredThroughSequence]))");
        });

        /*
          THE SEQUENCE IS THE KEY, and there is no surrogate beside it. AuditEvents carries a Guid id
          plus an indexed Sequence because a row is referenced from elsewhere; an anchor is not
          referenced by anything and is only ever read in chain order, so a second identity would be
          an object to maintain for no read path. The clustered key also turns two concurrent runs
          into a loud duplicate-key rather than a silent fork.

          ValueGeneratedNever, and this is the trap worth naming: IDENTITY exists only on a
          relational provider, so a generated value would stay 0 on the InMemory provider that most
          of this suite runs on -- and an anchor chain whose counter is always 0 has no order at all.
          The writer assigns it, exactly as the audit chain assigns Sequence.
        */
        builder.HasKey(a => a.AnchorSequence);
        builder.Property(a => a.AnchorSequence).ValueGeneratedNever();

        // nvarchar, NOT nchar: a version string's length is not invariant, and padding would read
        // back as "a1      " -- a scheme no build can render, on every record ever written.
        builder.Property(a => a.PayloadVersion)
            .IsRequired()
            .HasMaxLength(8);

        builder.Property(a => a.AnchorKeyId)
            .IsRequired()
            .HasMaxLength(16)
            .IsFixedLength();

        builder.Property(a => a.VerifiedUnderChainKeyId)
            .IsRequired()
            .HasMaxLength(16)
            .IsFixedLength();

        // Stored as its name rather than its number, same reasoning as AuditEvent.Outcome: an
        // integer in the table is a value somebody has to look up a mapping for at three in the
        // morning, and a reordered enum silently re-labels history.
        builder.Property(a => a.Kind)
            .IsRequired()
            .HasMaxLength(20)
            .HasConversion<string>();

        builder.Property(a => a.TailRowHash)
            .HasMaxLength(64)
            .IsFixedLength();

        builder.Property(a => a.AnchoredValue)
            .HasMaxLength(64)
            .IsFixedLength();

        builder.Property(a => a.PreviousAnchorPayloadHash)
            .HasMaxLength(64)
            .IsFixedLength();

        builder.Property(a => a.PayloadHash)
            .IsRequired()
            .HasMaxLength(64)
            .IsFixedLength();

        builder.Property(a => a.Mac)
            .IsRequired()
            .HasMaxLength(64)
            .IsFixedLength();

        /*
          NO INDEX BEYOND THE KEY, and no foreign key to AuditEvents.Sequence.

          The index part is easy: the table is tiny and is read in AnchorSequence order, which the
          clustered key already serves.

          The foreign key is the one worth explaining, because it looks like integrity going spare.
          ON DELETE CASCADE would hand an attacker the anchor-suffix deletion for free in the same
          statement that truncates the rows. NO ACTION would turn a detection control into an
          availability control that the same principal drops when it gets in the way. An anchor MUST
          be able to outlive the rows it names -- that mismatch is the evidence.
        */
    }
}
