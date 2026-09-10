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

        builder.HasIndex(n => new { n.RecipientId, n.CreatedAt })
            .IsDescending(false, true);

        // The delivery queue: only rows still waiting to be sent. Raw SQL, so snake_case
        // by hand (§8.12).
        builder.HasIndex(n => n.SentAt).HasFilter("sent_at IS NULL");

        builder.ToTable(t => t.HasCheckConstraint(
            "ck_notification_attempts_non_negative",
            "attempts >= 0"));
    }
}
