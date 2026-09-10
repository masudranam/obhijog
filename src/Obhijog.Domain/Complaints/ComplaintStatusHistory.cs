namespace Obhijog.Domain.Complaints;

/// <summary>
/// SPEC.md §8.5. Append-only: never updated, never deleted.
///
/// A row is written for every accepted transition, including <c>recategorize</c> (where
/// <c>FromStatus == ToStatus</c> is legal), the separate priority edit, and system auto-close.
/// </summary>
public class ComplaintStatusHistory
{
    public Guid Id { get; set; }

    public Guid ComplaintId { get; set; }

    public Complaint? Complaint { get; set; }

    public ComplaintStatus FromStatus { get; set; }

    public ComplaintStatus ToStatus { get; set; }

    public ComplaintAction Action { get; set; }

    /// <summary>Null when <see cref="IsSystem"/>.</summary>
    public Guid? ChangedById { get; set; }

    public DateTimeOffset ChangedAt { get; set; }

    /// <summary>Max 1000 characters.</summary>
    public string? Note { get; set; }

    public bool IsSystem { get; set; }
}
