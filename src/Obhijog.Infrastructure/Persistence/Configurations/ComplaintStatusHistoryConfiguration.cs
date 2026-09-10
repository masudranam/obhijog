using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Obhijog.Domain.Complaints;
using Obhijog.Infrastructure.Identity;

namespace Obhijog.Infrastructure.Persistence.Configurations;

/// <summary>SPEC.md §8.5 — append-only.</summary>
public class ComplaintStatusHistoryConfiguration : IEntityTypeConfiguration<ComplaintStatusHistory>
{
    public void Configure(EntityTypeBuilder<ComplaintStatusHistory> builder)
    {
        builder.HasKey(h => h.Id);

        builder.Property(h => h.FromStatus).IsRequired().HasMaxLength(16).HasConversion<string>();
        builder.Property(h => h.ToStatus).IsRequired().HasMaxLength(16).HasConversion<string>();
        builder.Property(h => h.Action).IsRequired().HasMaxLength(16).HasConversion<string>();
        builder.Property(h => h.Note).HasMaxLength(1000);
        builder.Property(h => h.IsSystem).IsRequired();

        builder.HasIndex(h => new { h.ComplaintId, h.ChangedAt });

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(h => h.ChangedById)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(h => h.Complaint)
            .WithMany(c => c.History)
            .HasForeignKey(h => h.ComplaintId)
            .OnDelete(DeleteBehavior.Cascade);

        // A system row has no actor; a user row must name one. Nothing else is valid.
        builder.ToTable(t => t.HasCheckConstraint(
            "ck_complaint_status_history_actor",
            "(is_system AND changed_by_id IS NULL) OR (NOT is_system AND changed_by_id IS NOT NULL)"));
    }
}
