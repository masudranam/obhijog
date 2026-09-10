using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Obhijog.Domain.Departments;

namespace Obhijog.Infrastructure.Persistence.Configurations;

/// <summary>SPEC.md §8.3.</summary>
public class ComplaintCategoryConfiguration : IEntityTypeConfiguration<ComplaintCategory>
{
    public void Configure(EntityTypeBuilder<ComplaintCategory> builder)
    {
        builder.HasKey(c => c.Id);

        builder.Property(c => c.Name).IsRequired().HasMaxLength(80);
        builder.Property(c => c.SlaHours).IsRequired();
        builder.Property(c => c.IsActive).IsRequired().HasDefaultValue(true);

        // Enums cross to the database as varchar, never a native PostgreSQL enum (§8.12).
        builder.Property(c => c.DefaultPriority)
            .IsRequired()
            .HasMaxLength(8)
            .HasConversion<string>();

        builder.HasIndex(c => c.Name).IsUnique();
        builder.HasIndex(c => new { c.IsActive, c.DepartmentId });

        builder.HasOne(c => c.Department)
            .WithMany()
            .HasForeignKey(c => c.DepartmentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.ToTable(t => t.HasCheckConstraint(
            "ck_complaint_category_sla_hours_range",
            $"sla_hours > 0 AND sla_hours <= {ComplaintCategory.MaxSlaHours}"));
    }
}
