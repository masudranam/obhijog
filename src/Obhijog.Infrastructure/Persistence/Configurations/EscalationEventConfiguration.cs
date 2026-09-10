using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Obhijog.Domain.Complaints;

namespace Obhijog.Infrastructure.Persistence.Configurations;

/// <summary>SPEC.md §8.8.</summary>
public class EscalationEventConfiguration : IEntityTypeConfiguration<EscalationEvent>
{
    public void Configure(EntityTypeBuilder<EscalationEvent> builder)
    {
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Reason).IsRequired().HasMaxLength(200);
        builder.Property(e => e.Level).IsRequired();
        builder.Property(e => e.ReopenCount).IsRequired();

        // The second line of defence against double-escalation, and the one that holds even
        // when two sweeps overlap — the case the sweeper's WHERE clause cannot see (§11.3).
        // Never remove this because the WHERE clause "already covers it".
        builder.HasIndex(e => new { e.ComplaintId, e.ReopenCount, e.Level }).IsUnique();

        builder.HasOne(e => e.Complaint)
            .WithMany(c => c.Escalations)
            .HasForeignKey(e => e.ComplaintId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.ToTable(t => t.HasCheckConstraint(
            "ck_escalation_event_level_range",
            "level IN (1, 2)"));
    }
}
