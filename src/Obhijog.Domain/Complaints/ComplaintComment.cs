namespace Obhijog.Domain.Complaints;

/// <summary>
/// SPEC.md §8.6.
///
/// <see cref="IsInternal"/> comments are **never** returned to a Citizen — filtered in the
/// query, not in a DTO mapper and not in the UI (§9, data-ef.md). Only Staff and Dept Admins
/// may create one.
/// </summary>
public class ComplaintComment
{
    public Guid Id { get; set; }

    public Guid ComplaintId { get; set; }

    public Complaint? Complaint { get; set; }

    public Guid AuthorId { get; set; }

    /// <summary>Max 2000 characters.</summary>
    public required string Body { get; set; }

    public bool IsInternal { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
