using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Obhijog.Infrastructure.Identity;

namespace Obhijog.Infrastructure.Persistence.Configurations;

/// <summary>SPEC.md §8.1.</summary>
public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.Property(u => u.FullName).IsRequired().HasMaxLength(120);
        builder.Property(u => u.Phone).HasMaxLength(24);
        builder.Property(u => u.Role).IsRequired().HasMaxLength(16).HasConversion<string>();
        builder.Property(u => u.IsActive).IsRequired().HasDefaultValue(true);

        // Serves "the active staff of this department", which is every assignment picker
        // and the level-1 escalation recipient list.
        builder.HasIndex(u => new { u.DepartmentId, u.Role }).HasFilter("is_active");

        builder.HasOne(u => u.Department)
            .WithMany()
            .HasForeignKey(u => u.DepartmentId)
            .OnDelete(DeleteBehavior.Restrict);

        // §8.1: null for a Citizen, required for Staff and DeptAdmin. A convention would
        // drift; a constraint cannot.
        builder.ToTable(t => t.HasCheckConstraint(
            "ck_user_department_matches_role",
            "(role = 'Citizen' AND department_id IS NULL) OR (role <> 'Citizen' AND department_id IS NOT NULL)"));
    }
}
