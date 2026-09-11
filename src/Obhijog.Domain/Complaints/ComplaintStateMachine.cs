using Obhijog.Domain.Users;

namespace Obhijog.Domain.Complaints;

/// <summary>
/// One row of the guard table. SPEC.md §12.3.
/// </summary>
/// <param name="To">The status the complaint lands in.</param>
/// <param name="AllowedRoles">Which roles may attempt it at all.</param>
/// <param name="AssigneeOnly">
/// When true the caller must be the complaint's current assignee — a department colleague
/// with the right role is still refused. This is the distinction between "may this role
/// reach the route" (a policy) and "may this person do this to this complaint right now".
/// </param>
/// <param name="RequiredPayload">The field the request must carry, or <c>null</c>.</param>
/// <param name="AllowSystem">
/// Whether the sweeper may perform it unattended. Only <c>close</c> may (§11.2's auto-close).
/// </param>
public record TransitionRule(
    ComplaintStatus To,
    IReadOnlySet<UserRole> AllowedRoles,
    bool AssigneeOnly,
    TransitionPayload RequiredPayload,
    bool AllowSystem = false);

/// <summary>What a transition must carry beyond the action itself.</summary>
public enum TransitionPayload
{
    None,
    AssigneeId,
    CategoryId,
    Note,
}

/// <summary>
/// **The** specification of allowed movement, as executable code. SPEC.md §12.3.
///
/// Twelve rows, and anything absent from them is rejected. This type is pure — no clock, no
/// database, no <c>HttpContext</c> — which is the only reason §18's state-machine suite is
/// cheap enough to walk every row.
///
/// It answers two questions and no others: *is this move legal*, and *what could this caller
/// do next*. Whether the complaint is visible at all belongs to <c>ComplaintQueryScope</c>;
/// actually performing the move belongs to <c>ComplaintTransitionService</c>.
/// </summary>
public static class ComplaintStateMachine
{
    private static readonly IReadOnlySet<UserRole> DeptAdminOnly = Set(UserRole.DeptAdmin);
    private static readonly IReadOnlySet<UserRole> StaffOnly = Set(UserRole.Staff);
    private static readonly IReadOnlySet<UserRole> CitizenOrAdmin =
        Set(UserRole.Citizen, UserRole.DeptAdmin);

    /// <summary>
    /// The twelve rows of §12.3, in the order they appear there. Keyed by
    /// <c>(From, Action)</c>, so an absent pair is by definition not allowed.
    /// </summary>
    private static readonly IReadOnlyDictionary<(ComplaintStatus From, ComplaintAction Action), TransitionRule>
        Rules = new Dictionary<(ComplaintStatus, ComplaintAction), TransitionRule>
        {
            //  1. New → assign → Assigned
            [(ComplaintStatus.New, ComplaintAction.Assign)] =
                new(ComplaintStatus.Assigned, DeptAdminOnly, false, TransitionPayload.AssigneeId),

            //  2. New → recategorize → New  (FromStatus == ToStatus is legal, §8.5)
            [(ComplaintStatus.New, ComplaintAction.Recategorize)] =
                new(ComplaintStatus.New, DeptAdminOnly, false, TransitionPayload.CategoryId),

            //  3. New → reject → Rejected
            [(ComplaintStatus.New, ComplaintAction.Reject)] =
                new(ComplaintStatus.Rejected, DeptAdminOnly, false, TransitionPayload.Note),

            //  4. Assigned → reassign → Assigned
            [(ComplaintStatus.Assigned, ComplaintAction.Reassign)] =
                new(ComplaintStatus.Assigned, DeptAdminOnly, false, TransitionPayload.AssigneeId),

            //  5. Assigned → recategorize → New  (and clears the assignee, §12.4)
            [(ComplaintStatus.Assigned, ComplaintAction.Recategorize)] =
                new(ComplaintStatus.New, DeptAdminOnly, false, TransitionPayload.CategoryId),

            //  6. Assigned → start → InProgress  (the assignee alone)
            [(ComplaintStatus.Assigned, ComplaintAction.Start)] =
                new(ComplaintStatus.InProgress, StaffOnly, true, TransitionPayload.None),

            //  7. Assigned → reject → Rejected
            [(ComplaintStatus.Assigned, ComplaintAction.Reject)] =
                new(ComplaintStatus.Rejected, DeptAdminOnly, false, TransitionPayload.Note),

            //  8. InProgress → resolve → Resolved  (the assignee alone)
            [(ComplaintStatus.InProgress, ComplaintAction.Resolve)] =
                new(ComplaintStatus.Resolved, StaffOnly, true, TransitionPayload.Note),

            //  9. InProgress → reassign → Assigned
            [(ComplaintStatus.InProgress, ComplaintAction.Reassign)] =
                new(ComplaintStatus.Assigned, DeptAdminOnly, false, TransitionPayload.AssigneeId),

            // 10. InProgress → reject → Rejected
            [(ComplaintStatus.InProgress, ComplaintAction.Reject)] =
                new(ComplaintStatus.Rejected, DeptAdminOnly, false, TransitionPayload.Note),

            // 11. Resolved → close → Closed  (owner, DeptAdmin, or the sweeper)
            [(ComplaintStatus.Resolved, ComplaintAction.Close)] =
                new(ComplaintStatus.Closed, CitizenOrAdmin, false, TransitionPayload.None,
                    AllowSystem: true),

            // 12. Resolved → reopen → Assigned
            [(ComplaintStatus.Resolved, ComplaintAction.Reopen)] =
                new(ComplaintStatus.Assigned, CitizenOrAdmin, false, TransitionPayload.Note),
        };

    /// <summary>Every <c>(from, action)</c> pair the table defines. For the tests.</summary>
    public static IEnumerable<(ComplaintStatus From, ComplaintAction Action)> DefinedTransitions =>
        Rules.Keys;

    /// <summary>
    /// The rule for a pair, or <c>null</c> when the table has no such row — which is the
    /// <c>409</c> case of §12.3.
    /// </summary>
    public static TransitionRule? Find(ComplaintStatus from, ComplaintAction action) =>
        Rules.GetValueOrDefault((from, action));

    /// <summary>
    /// Whether this caller may perform this action on this complaint right now.
    ///
    /// The three failures are deliberately distinguishable, because they map to different
    /// status codes: no rule is a <c>409</c>, a wrong role or a non-assignee is a
    /// <c>403</c>, and a missing payload is a <c>400</c>. Collapsing them into a bool would
    /// force the caller to re-derive which.
    /// </summary>
    public static TransitionCheck Check(
        ComplaintStatus from,
        ComplaintAction action,
        UserRole role,
        Guid callerId,
        Guid? assignedStaffId,
        bool isSystem = false)
    {
        var rule = Find(from, action);

        if (rule is null)
        {
            return TransitionCheck.NotAllowedFromHere(from, action);
        }

        if (isSystem)
        {
            return rule.AllowSystem
                ? TransitionCheck.Allowed(rule)
                : TransitionCheck.WrongRole(rule);
        }

        if (!rule.AllowedRoles.Contains(role))
        {
            return TransitionCheck.WrongRole(rule);
        }

        // §12.3's assignee-only column. A Staff member in the right department who is not
        // the assignee is refused — that is a 403 and not a 404, because they can see the
        // complaint perfectly well, they just may not act on it (§9.2).
        if (rule.AssigneeOnly && (assignedStaffId is null || assignedStaffId != callerId))
        {
            return TransitionCheck.NotTheAssignee(rule);
        }

        return TransitionCheck.Allowed(rule);
    }

    /// <summary>
    /// What this caller may do to this complaint right now — the <c>availableActions</c> the
    /// API returns and the UI renders buttons from (§13.3, §15).
    ///
    /// Computed from the same table as <see cref="Check"/>, so a button can never appear for
    /// an action the write path would then refuse.
    /// </summary>
    public static IReadOnlyList<ComplaintAction> AvailableActions(
        ComplaintStatus from,
        UserRole role,
        Guid callerId,
        Guid? assignedStaffId,
        bool isOwner) =>
        Rules
            .Where(entry => entry.Key.From == from)
            .Where(entry => Check(from, entry.Key.Action, role, callerId, assignedStaffId).IsAllowed)

            // §12.4: a Citizen may close or reopen only their **own** complaint. The guard
            // table cannot express ownership — it does not know who the citizen is — so the
            // one rule it cannot carry is applied here rather than left to the UI.
            .Where(entry => role != UserRole.Citizen || isOwner)
            .Select(entry => entry.Key.Action)
            .OrderBy(action => action)
            .ToList();

    private static IReadOnlySet<UserRole> Set(params UserRole[] roles) => roles.ToHashSet();
}

/// <summary>The outcome of <see cref="ComplaintStateMachine.Check"/>.</summary>
public record TransitionCheck(
    TransitionOutcome Outcome,
    TransitionRule? Rule,
    string Message)
{
    public bool IsAllowed => Outcome == TransitionOutcome.Allowed;

    public static TransitionCheck Allowed(TransitionRule rule) =>
        new(TransitionOutcome.Allowed, rule, string.Empty);

    public static TransitionCheck NotAllowedFromHere(ComplaintStatus from, ComplaintAction action) =>
        new(
            TransitionOutcome.NotAllowedFromHere,
            null,
            $"'{action}' is not possible while the complaint is '{from}'.");

    public static TransitionCheck WrongRole(TransitionRule rule) =>
        new(TransitionOutcome.WrongRole, rule, "Your role may not perform this action.");

    public static TransitionCheck NotTheAssignee(TransitionRule rule) =>
        new(
            TransitionOutcome.NotTheAssignee,
            rule,
            "Only the assigned staff member may perform this action.");
}

public enum TransitionOutcome
{
    Allowed,

    /// <summary>No row for this <c>(from, action)</c> pair — <c>409</c>.</summary>
    NotAllowedFromHere,

    /// <summary>The role is not on the row — <c>403</c>.</summary>
    WrongRole,

    /// <summary>Assignee-only, and the caller is not the assignee — <c>403</c>.</summary>
    NotTheAssignee,
}
