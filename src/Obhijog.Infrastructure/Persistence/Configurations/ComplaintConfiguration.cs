using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Obhijog.Domain.Complaints;
using Obhijog.Infrastructure.Identity;

namespace Obhijog.Infrastructure.Persistence.Configurations;

/// <summary>SPEC.md §8.4 — the aggregate root, and the five indexes that serve real queries.</summary>
public class ComplaintConfiguration : IEntityTypeConfiguration<Complaint>
{
    /// <summary>
    /// The open statuses, as raw SQL for the partial sweep index. <c>HasFilter</c> takes raw
    /// SQL and the snake_case convention does **not** rewrite it (§8.12), so the column and
    /// the enum literals are written out by hand here.
    /// </summary>
    private const string OpenStatusesFilter =
        "status IN ('New', 'Assigned', 'InProgress')";

    public void Configure(EntityTypeBuilder<Complaint> builder)
    {
        builder.HasKey(c => c.Id);

        builder.Property(c => c.ReferenceNumber).IsRequired().HasMaxLength(20);
        builder.Property(c => c.Title).IsRequired().HasMaxLength(140);
        builder.Property(c => c.Description).IsRequired().HasMaxLength(4000);
        builder.Property(c => c.AddressText).HasMaxLength(250);
        builder.Property(c => c.RejectionReason).HasMaxLength(500);
        builder.Property(c => c.ResolutionNote).HasMaxLength(1000);

        builder.Property(c => c.Status)
            .IsRequired()
            .HasMaxLength(16)
            .HasConversion<string>();

        builder.Property(c => c.Priority)
            .IsRequired()
            .HasMaxLength(8)
            .HasConversion<string>();

        builder.Property(c => c.Latitude).IsRequired().HasPrecision(9, 6);
        builder.Property(c => c.Longitude).IsRequired().HasPrecision(9, 6);

        builder.Property(c => c.EscalationLevel).IsRequired().HasDefaultValue((short)0);
        builder.Property(c => c.ReopenCount).IsRequired().HasDefaultValue(0);

        // PostgreSQL has no rowversion; xmin is the system column every UPDATE bumps for
        // free (§8.12). No extra column, no trigger.
        // IsRowVersion() on a uint is how Npgsql maps an explicit property to xmin.
        // UseXminAsConcurrencyToken() is the alternative and creates a *shadow* property —
        // using both would declare two tokens for one column.
        builder.Property(c => c.Version).IsRowVersion();

        builder.HasIndex(c => c.ReferenceNumber).IsUnique();
        builder.HasIndex(c => new { c.CitizenId, c.CreatedAt })
            .IsDescending(false, true);
        builder.HasIndex(c => new { c.DepartmentId, c.Status, c.SlaDueAt });

        builder.HasIndex(c => new { c.AssignedStaffId, c.Status })
            .HasFilter("assigned_staff_id IS NOT NULL");

        // Load-bearing: without it the sweep table-scans every complaint every 60 seconds
        // (SPEC §8.4, §17).
        builder.HasIndex(c => new { c.Status, c.SlaDueAt })
            .HasFilter(OpenStatusesFilter);

        // §8.4 names both of these FK → User. There is no navigation property, because
        // Complaint lives in Domain and User does not (D13) — but this configuration class
        // is in Infrastructure, where User is visible, so the constraint costs nothing.
        // Restrict, not Cascade: deleting a user must never delete their complaints.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(c => c.CitizenId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(c => c.AssignedStaffId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(c => c.Category)
            .WithMany()
            .HasForeignKey(c => c.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(c => c.Department)
            .WithMany()
            .HasForeignKey(c => c.DepartmentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.ToTable(t =>
        {
            t.HasCheckConstraint(
                "ck_complaint_latitude_range",
                "latitude >= -90 AND latitude <= 90");
            t.HasCheckConstraint(
                "ck_complaint_longitude_range",
                "longitude >= -180 AND longitude <= 180");
            t.HasCheckConstraint(
                "ck_complaint_escalation_level_range",
                "escalation_level >= 0 AND escalation_level <= 2");
            t.HasCheckConstraint(
                "ck_complaint_reopen_count_non_negative",
                "reopen_count >= 0");

            // §12.1 and §8.4: the reason and the note are required by the status that
            // demands them, which is a database concern rather than a validation nicety.
            t.HasCheckConstraint(
                "ck_complaint_rejection_reason_required",
                "status <> 'Rejected' OR rejection_reason IS NOT NULL");
            t.HasCheckConstraint(
                "ck_complaint_resolution_note_required",
                "status NOT IN ('Resolved', 'Closed') OR resolution_note IS NOT NULL");
        });
    }
}
