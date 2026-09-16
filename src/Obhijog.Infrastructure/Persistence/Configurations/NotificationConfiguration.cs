using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Obhijog.Domain.Notifications;
using Obhijog.Infrastructure.Identity;

namespace Obhijog.Infrastructure.Persistence.Configurations;

/// <summary>SPEC.md §8.9.</summary>
public class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.HasKey(n => n.Id);

        builder.Property(n => n.Type).IsRequired().HasMaxLength(40).HasConversion<string>();
        builder.Property(n => n.Subject).IsRequired().HasMaxLength(160);
        builder.Property(n => n.Body).IsRequired().HasMaxLength(2000);
        builder.Property(n => n.LastError).HasMaxLength(500);
        builder.Property(n => n.Attempts).IsRequired().HasDefaultValue(0);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(n => n.RecipientId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(n => n.ReopenCount).IsRequired().HasDefaultValue(0);

        builder.HasIndex(n => new { n.RecipientId, n.CreatedAt })
            .IsDescending(false, true);

        // §11.3 defence 4, and the reason M10 can move delivery off the sweeper's
        // transaction at all.
        //
        // Until now "one notification per recipient per level" was not enforced here — it
        // was borrowed from defence 3. The escalation row and the notification rows were
        // written in one transaction, so the unique violation on
        // EscalationEvent(ComplaintId, ReopenCount, Level) rolled the notifications back
        // with it. A Service Bus consumer writes notifications in its *own* transaction
        // with no escalation row to collide with, which severs that borrowing completely:
        // a redelivered message would write a second full set and nothing would stop it.
        // This index is what stops it.
        //
        // **Partial, and that is not an optimisation.** A blanket unique index would be
        // wrong: ComplaintAssigned legitimately recurs for the same recipient inside one
        // reopen cycle — reassign A -> B -> A and A is notified twice, correctly. Only the
        // three SLA rungs carry a "once per recipient per reopen cycle" rule, so only they
        // are constrained. Raw SQL, so snake_case by hand and the enum values as stored
        // (§8.12).
        builder.HasIndex(n => new { n.ComplaintId, n.RecipientId, n.Type, n.ReopenCount })
            .IsUnique()
            .HasFilter("type IN ('SlaWarning', 'SlaBreached', 'SlaEscalatedLevel2')");

        // The delivery queue: only rows still waiting to be sent. Raw SQL, so snake_case
        // by hand (§8.12).
        builder.HasIndex(n => n.SentAt).HasFilter("sent_at IS NULL");

        builder.ToTable(t => t.HasCheckConstraint(
            "ck_notification_attempts_non_negative",
            "attempts >= 0"));
    }
}
