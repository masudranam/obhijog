namespace Obhijog.Domain.Complaints;

/// <summary>
/// SPEC.md §12.1. <see cref="Closed"/> and <see cref="Rejected"/> are terminal —
/// no transition leaves them.
/// </summary>
public enum ComplaintStatus
{
    New,
    Assigned,
    InProgress,
    Resolved,
    Closed,
    Rejected,
}
