namespace Obhijog.Domain.Complaints;

/// <summary>
/// SPEC.md §8.4 — the aggregate root.
///
/// Users are referenced by id only. <c>User</c> extends <c>IdentityUser&lt;Guid&gt;</c> and
/// therefore lives in Infrastructure (D13), so a navigation property here would drag the
/// Identity stack into the domain layer.
/// </summary>
public class Complaint
{
    public Guid Id { get; set; }

    /// <summary>Unique, <c>MC-{year}-{seq:D6}</c>. Max 20 characters. See §8.10.</summary>
    public required string ReferenceNumber { get; set; }

    public Guid CitizenId { get; set; }

    public Guid CategoryId { get; set; }

    public Departments.ComplaintCategory? Category { get; set; }

    /// <summary>
    /// Denormalized from the category at creation, so rerouting is an explicit, audited
    /// act rather than a side effect of editing a category (§8.4).
    /// </summary>
    public Guid DepartmentId { get; set; }

    public Departments.Department? Department { get; set; }

    /// <summary>Max 140 characters.</summary>
    public required string Title { get; set; }

    /// <summary>Max 4000 characters.</summary>
    public required string Description { get; set; }

    public ComplaintStatus Status { get; set; } = ComplaintStatus.New;

    /// <summary>Seeded from the category, editable by a Dept Admin.</summary>
    public ComplaintPriority Priority { get; set; }

    /// <summary>−90…90.</summary>
    public decimal Latitude { get; set; }

    /// <summary>−180…180.</summary>
    public decimal Longitude { get; set; }

    /// <summary>Optional free text. Max 250 characters.</summary>
    public string? AddressText { get; set; }

    /// <summary>Null unless <c>Assigned</c> or <c>InProgress</c>.</summary>
    public Guid? AssignedStaffId { get; set; }

    /// <summary>The SLA clock start.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Stored, not computed — changing the category's SlaHours does not move it.</summary>
    public DateTimeOffset SlaDueAt { get; set; }

    /// <summary>Set once per window by the sweeper.</summary>
    public DateTimeOffset? SlaWarnedAt { get; set; }

    /// <summary>Set once per window by the sweeper.</summary>
    public DateTimeOffset? SlaBreachedAt { get; set; }

    /// <summary>0, 1 or 2. <c>smallint</c> — PostgreSQL has no <c>tinyint</c> (§8.12).</summary>
    public short EscalationLevel { get; set; }

    /// <summary>The SLA measurement point.</summary>
    public DateTimeOffset? ResolvedAt { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>Required when <c>Rejected</c>. Max 500 characters.</summary>
    public string? RejectionReason { get; set; }

    /// <summary>Required when <c>Resolved</c>. Max 1000 characters.</summary>
    public string? ResolutionNote { get; set; }

    public int ReopenCount { get; set; }

    /// <summary>
    /// Optimistic concurrency token, mapped to PostgreSQL's system <c>xmin</c> column
    /// (§8.12). Never exposed in a DTO.
    /// </summary>
    public uint Version { get; set; }

    public ICollection<ComplaintStatusHistory> History { get; set; } = [];

    public ICollection<ComplaintComment> Comments { get; set; } = [];

    public ICollection<ComplaintAttachment> Attachments { get; set; } = [];

    public ICollection<EscalationEvent> Escalations { get; set; } = [];
}
