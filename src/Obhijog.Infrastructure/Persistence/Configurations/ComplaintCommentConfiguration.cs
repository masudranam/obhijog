using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Obhijog.Domain.Complaints;
using Obhijog.Infrastructure.Identity;

namespace Obhijog.Infrastructure.Persistence.Configurations;

/// <summary>SPEC.md §8.6.</summary>
public class ComplaintCommentConfiguration : IEntityTypeConfiguration<ComplaintComment>
{
    public void Configure(EntityTypeBuilder<ComplaintComment> builder)
    {
        builder.HasKey(c => c.Id);

        builder.Property(c => c.Body).IsRequired().HasMaxLength(2000);
        builder.Property(c => c.IsInternal).IsRequired().HasDefaultValue(false);

        builder.HasIndex(c => new { c.ComplaintId, c.CreatedAt });

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(c => c.AuthorId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(c => c.Complaint)
            .WithMany(x => x.Comments)
            .HasForeignKey(c => c.ComplaintId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
