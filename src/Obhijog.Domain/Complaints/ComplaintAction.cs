namespace Obhijog.Domain.Complaints;

/// <summary>
/// SPEC.md §12.2.
///
/// Two groups, and the distinction matters. The first eight are **transition actions**: they
/// are the `action` an API caller may send, they key the guard table in §12.3, and the UI
/// renders a button for each one the server says is available.
///
/// <see cref="Submit"/> is a **history action**. §8.5 requires a history row for every
/// movement including ones that are not transitions, and a complaint's first row — the one
/// recording that it came into existence as <c>New</c> — has no guard-table entry because
/// nothing moved. A caller can never send it: it is written by
/// <c>ComplaintService.CreateAsync</c> and by nothing else.
///
/// The priority edit needs the same treatment and does not have it yet — see issue #25.
/// </summary>
public enum ComplaintAction
{
    // Transition actions — the guard table's vocabulary (§12.3).
    Assign,
    Reassign,
    Recategorize,
    Reject,
    Start,
    Resolve,
    Close,
    Reopen,

    /// <summary>
    /// The initial <c>New</c> history row written at submission. Not a transition, never
    /// accepted from a request payload, and absent from the guard table by design.
    /// </summary>
    Submit,
}
