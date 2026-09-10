namespace Obhijog.Domain.Complaints;

/// <summary>
/// SPEC.md §12.2. Eight actions, and the list is closed — the API payload, the guard
/// table, the history <c>Action</c> column and the UI buttons all use exactly these names.
/// </summary>
public enum ComplaintAction
{
    Assign,
    Reassign,
    Recategorize,
    Reject,
    Start,
    Resolve,
    Close,
    Reopen,
}
