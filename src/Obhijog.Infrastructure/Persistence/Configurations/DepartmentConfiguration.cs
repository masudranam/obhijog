using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Obhijog.Domain.Departments;

namespace Obhijog.Infrastructure.Persistence.Configurations;

/// <summary>SPEC.md §8.2.</summary>
public class DepartmentConfiguration : IEntityTypeConfiguration<Department>
{
    public void Configure(EntityTypeBuilder<Department> builder)
    {
        builder.HasKey(d => d.Id);

        builder.Property(d => d.Name).IsRequired().HasMaxLength(80);
        builder.Property(d => d.Code).IsRequired().HasMaxLength(12);
        builder.Property(d => d.IsActive).IsRequired().HasDefaultValue(true);

        builder.HasIndex(d => d.Name).IsUnique();
        builder.HasIndex(d => d.Code).IsUnique();

        // Uppercase is stated in §8.2 and is a natural key the seeder matches on, so it is
        // enforced rather than left to callers.
        builder.ToTable(t => t.HasCheckConstraint(
            "ck_department_code_uppercase",
            "code = upper(code)"));
    }
}
