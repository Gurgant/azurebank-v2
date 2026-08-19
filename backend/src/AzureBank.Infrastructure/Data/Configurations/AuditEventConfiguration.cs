using AzureBank.Shared.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AzureBank.Infrastructure.Data.Configurations;

public class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        builder.ToTable("AuditEvents");

        builder.HasKey(e => e.Id);

        /*
          The clustered key is Sequence, not Id. Two reasons, and both matter for a table that only
          ever grows: inserts land at the end instead of splitting pages all over the index, and the
          hash chain is defined over this order — a verifier walking the chain wants a range scan,
          not a lookup per row.
        */
        /*
          ASSIGNED BY AuditChain, NOT BY THE DATABASE, and that is a correction rather than a
          preference. An IDENTITY column exists only on a relational provider, so on the EF InMemory
          provider — where ~585 of this project's tests run — Sequence would stay 0 on every row and
          the chain would have no order at all. The first version ordered by Id instead and was
          wrong for a reason STATE.md already warns about in capitals: Guid ordering in .NET is not
          creation order, and SQL Server collates uniqueidentifier differently again. Assigning the
          number ourselves under the same lock that reads the tail makes the order explicit,
          identical on both providers, and therefore actually testable where the tests are.
        */
        builder.Property(e => e.Sequence)
            .ValueGeneratedNever();

        builder.HasIndex(e => e.Sequence)
            .IsUnique()
            .IsClustered()
            .HasDatabaseName("IX_AuditEvents_Sequence");

        builder.HasKey(e => e.Id).IsClustered(false);

        // Stored as a string, same convention as Transaction.Status and StepUpAuthorization.Operation:
        // reordering an enum must never silently reinterpret rows that are meant to be read as
        // evidence years from now.
        builder.Property(e => e.Outcome)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(20);

        /*
          The event name is one of the SecurityEvents constants. 40 characters is roughly twice the
          longest today (RefreshTokenReuseRevokeFailed, 29), which leaves room without inviting a
          sentence — this column is grouped and alerted on, so it has to stay an identifier.
        */
        builder.Property(e => e.Event)
            .IsRequired()
            .HasMaxLength(40);

        builder.Property(e => e.OccurredAt)
            .IsRequired();

        builder.Property(e => e.SubjectType)
            .HasMaxLength(40);

        // Bare 32-hex trace id, the form this codebase emits everywhere except the framework's own
        // 404s. Sized for the W3C form too so a future change of mind does not need a migration.
        builder.Property(e => e.TraceId)
            .HasMaxLength(64);

        // JSON. Capped rather than MAX: an unbounded column on an append-only table is an invitation
        // to put a stack trace in it, and a stack trace is the kind of thing that carries PII into a
        // store designed never to be purged.
        builder.Property(e => e.Detail)
            .HasMaxLength(1024);

        // HMAC-SHA256 hex, always exactly 64 characters — same shape and reasoning as
        // StepUpAuthorization.BindingHash.
        builder.Property(e => e.RowHash)
            .IsRequired()
            .HasMaxLength(64)
            .IsFixedLength();

        builder.Property(e => e.PreviousHash)
            .HasMaxLength(64)
            .IsFixedLength();

        /*
          Read paths this table is expected to serve, and nothing else:
            - "what happened to this account/user"      -> (SubjectId), (ActorUserId)
            - "every occurrence of this event, in time" -> (Event, OccurredAt)
          B3's evidence pack (PSD2 Art. 72) reads by subject and by time, which is why those two
          exist before anyone asks for them. No index on Outcome: it has four values, so it filters
          almost nothing on its own.
        */
        builder.HasIndex(e => e.ActorUserId)
            .HasDatabaseName("IX_AuditEvents_ActorUserId");

        builder.HasIndex(e => e.SubjectId)
            .HasDatabaseName("IX_AuditEvents_SubjectId");

        builder.HasIndex(e => new { e.Event, e.OccurredAt })
            .HasDatabaseName("IX_AuditEvents_Event_OccurredAt");

        /*
          NO soft-delete filter and no cleanup service, deliberately, and unlike IdempotencyRecords
          or RefreshTokens which both have sweepers. Every regime that binds this system states a
          MINIMUM retention — PCI DSS v4 10.5.1 twelve months, AMLD Art. 40 and PSD2 Art. 21 five
          years — and a minimum can never be violated by keeping more. The genuine conflict is GDPR
          erasure, and the answer is upstream: this table holds pseudonymous ids only, so there is
          nothing here to erase.
        */
    }
}
