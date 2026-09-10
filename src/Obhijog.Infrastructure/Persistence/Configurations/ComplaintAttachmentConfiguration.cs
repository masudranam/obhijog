using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Obhijog.Domain.Complaints;

namespace Obhijog.Infrastructure.Persistence.Configurations;

/// <summary>SPEC.md §8.7.</summary>
public class ComplaintAttachmentConfiguration : IEntityTypeConfiguration<ComplaintAttachment>
{
    public void Configure(EntityTypeBuilder<ComplaintAttachment> builder)
    {
        builder.HasKey(a => a.Id);

        builder.Property(a => a.BlobName).IsRequired().HasMaxLength(200);
        builder.Property(a => a.OriginalFileName).IsRequired().HasMaxLength(200);
        builder.Property(a => a.ContentType).IsRequired().HasMaxLength(80);
        builder.Property(a => a.SizeBytes).IsRequired();

        builder.HasIndex(a => a.BlobName).IsUnique();
        builder.HasIndex(a => new { a.ComplaintId, a.UploadedAt });

        builder.HasOne(a => a.Complaint)
            .WithMany(c => c.Attachments)
            .HasForeignKey(a => a.ComplaintId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.ToTable(t => t.HasCheckConstraint(
            "ck_complaint_attachment_size_positive",
            "size_bytes > 0"));
    }
}
