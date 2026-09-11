using Obhijog.Domain.Complaints;
using Obhijog.Domain.Users;
using Xunit;

namespace Obhijog.Tests;

/// <summary>
/// SPEC.md §18's state-machine suite, and F7's explicit requirement: walk **every** row of
/// the §12.3 guard table, and assert a representative sample of absent pairs is rejected.
///
/// The table is the specification of allowed movement, so these tests are a transcription
/// of §12.3 rather than a description of the implementation. When they disagree with the
/// SPEC table, the SPEC table is right.
/// </summary>
public class ComplaintStateMachineTests
{
    private static readonly Guid Assignee = Guid.CreateVersion7();
    private static readonly Guid Colleague = Guid.CreateVersion7();

    /// <summary>
    /// The twelve rows of §12.3, transcribed. Each is asserted to exist, to land in the
    /// stated status, and to be permitted for the stated role.
    /// </summary>
    public static TheoryData<ComplaintStatus, ComplaintAction, ComplaintStatus, UserRole> TheTwelveRows =>
        new()
        {
            //  #   From                        Action                          To                          Role
            { ComplaintStatus.New, ComplaintAction.Assign, ComplaintStatus.Assigned, UserRole.DeptAdmin },
            { ComplaintStatus.New, ComplaintAction.Recategorize, ComplaintStatus.New, UserRole.DeptAdmin },
            { ComplaintStatus.New, ComplaintAction.Reject, ComplaintStatus.Rejected, UserRole.DeptAdmin },
            { ComplaintStatus.Assigned, ComplaintAction.Reassign, ComplaintStatus.Assigned, UserRole.DeptAdmin },
            { ComplaintStatus.Assigned, ComplaintAction.Recategorize, ComplaintStatus.New, UserRole.DeptAdmin },
            { ComplaintStatus.Assigned, ComplaintAction.Start, ComplaintStatus.InProgress, UserRole.Staff },
            { ComplaintStatus.Assigned, ComplaintAction.Reject, ComplaintStatus.Rejected, UserRole.DeptAdmin },
            { ComplaintStatus.InProgress, ComplaintAction.Resolve, ComplaintStatus.Resolved, UserRole.Staff },
            { ComplaintStatus.InProgress, ComplaintAction.Reassign, ComplaintStatus.Assigned, UserRole.DeptAdmin },
            { ComplaintStatus.InProgress, ComplaintAction.Reject, ComplaintStatus.Rejected, UserRole.DeptAdmin },
            { ComplaintStatus.Resolved, ComplaintAction.Close, ComplaintStatus.Closed, UserRole.DeptAdmin },
            { ComplaintStatus.Resolved, ComplaintAction.Reopen, ComplaintStatus.Assigned, UserRole.DeptAdmin },
        };

    [Theory]
    [MemberData(nameof(TheTwelveRows))]
    public void EveryRowOfTheGuardTableIsAllowedAndLandsWhereSpecSays(
        ComplaintStatus from,
        ComplaintAction action,
        ComplaintStatus to,
        UserRole role)
    {
        var check = ComplaintStateMachine.Check(from, action, role, Assignee, Assignee);

        Assert.True(check.IsAllowed, $"{from} + {action} should be allowed for {role}");
        Assert.Equal(to, check.Rule!.To);
    }

    /// <summary>The table has exactly twelve rows. A thirteenth is a spec change.</summary>
    [Fact]
    public void TheTableHasTwelveRowsAndNoMore()
    {
        Assert.Equal(12, ComplaintStateMachine.DefinedTransitions.Count());
        Assert.Equal(
            TheTwelveRows.Select(row => ((ComplaintStatus)row[0]!, (ComplaintAction)row[1]!)).ToHashSet(),
            ComplaintStateMachine.DefinedTransitions.ToHashSet());
    }

    /// <summary>
    /// F7's "a representative sample of absent pairs is rejected". These are the mistakes a
    /// client is most likely to make, and each must be a <c>409</c> rather than a silent
    /// no-op or a 400.
    /// </summary>
    [Theory]
    // Skipping a step.
    [InlineData(ComplaintStatus.New, ComplaintAction.Start)]
    [InlineData(ComplaintStatus.New, ComplaintAction.Resolve)]
    [InlineData(ComplaintStatus.New, ComplaintAction.Close)]
    [InlineData(ComplaintStatus.Assigned, ComplaintAction.Resolve)]
    // Going backwards.
    [InlineData(ComplaintStatus.InProgress, ComplaintAction.Assign)]
    [InlineData(ComplaintStatus.InProgress, ComplaintAction.Start)]
    // Acting on a finished complaint.
    [InlineData(ComplaintStatus.Closed, ComplaintAction.Reopen)]
    [InlineData(ComplaintStatus.Closed, ComplaintAction.Assign)]
    [InlineData(ComplaintStatus.Rejected, ComplaintAction.Assign)]
    [InlineData(ComplaintStatus.Rejected, ComplaintAction.Reopen)]
    [InlineData(ComplaintStatus.Resolved, ComplaintAction.Resolve)]
    [InlineData(ComplaintStatus.Resolved, ComplaintAction.Start)]
    // Recategorizing after work has begun.
    [InlineData(ComplaintStatus.InProgress, ComplaintAction.Recategorize)]
    [InlineData(ComplaintStatus.Resolved, ComplaintAction.Recategorize)]
    public void AnAbsentPairIsRejected(ComplaintStatus from, ComplaintAction action)
    {
        Assert.Null(ComplaintStateMachine.Find(from, action));

        var check = ComplaintStateMachine.Check(from, action, UserRole.DeptAdmin, Assignee, Assignee);

        Assert.Equal(TransitionOutcome.NotAllowedFromHere, check.Outcome);
    }

    /// <summary>
    /// Every pair the table does *not* define must be refused — not just the sample above.
    /// Six statuses times ten actions is sixty pairs; twelve are legal and the other
    /// forty-eight must all be <c>NotAllowedFromHere</c> for every role.
    /// </summary>
    [Fact]
    public void EveryUndefinedPairIsRefusedForEveryRole()
    {
        var defined = ComplaintStateMachine.DefinedTransitions.ToHashSet();

        foreach (var from in Enum.GetValues<ComplaintStatus>())
        {
            foreach (var action in Enum.GetValues<ComplaintAction>())
            {
                if (defined.Contains((from, action)))
                {
                    continue;
                }

                foreach (var role in Enum.GetValues<UserRole>())
                {
                    var check = ComplaintStateMachine.Check(from, action, role, Assignee, Assignee);

                    Assert.Equal(TransitionOutcome.NotAllowedFromHere, check.Outcome);
                }
            }
        }
    }

    // -----------------------------------------------------------------------------------
    // The role and assignee-only columns
    // -----------------------------------------------------------------------------------

    /// <summary>
    /// §12.3's assignee-only column, and the reason it is not a role check: a Staff member
    /// in the right department who is not the assignee is refused. That is a <c>403</c> —
    /// they can see the complaint, they may not act on it (§9.2).
    /// </summary>
    [Theory]
    [InlineData(ComplaintStatus.Assigned, ComplaintAction.Start)]
    [InlineData(ComplaintStatus.InProgress, ComplaintAction.Resolve)]
    public void AssigneeOnlyActionsRefuseAColleague(ComplaintStatus from, ComplaintAction action)
    {
        var assignee = ComplaintStateMachine.Check(from, action, UserRole.Staff, Assignee, Assignee);
        var colleague = ComplaintStateMachine.Check(from, action, UserRole.Staff, Colleague, Assignee);

        Assert.True(assignee.IsAllowed);
        Assert.Equal(TransitionOutcome.NotTheAssignee, colleague.Outcome);
    }

    [Theory]
    [InlineData(ComplaintStatus.Assigned, ComplaintAction.Start)]
    [InlineData(ComplaintStatus.InProgress, ComplaintAction.Resolve)]
    public void AssigneeOnlyActionsRefuseAnUnassignedComplaint(
        ComplaintStatus from,
        ComplaintAction action)
    {
        var check = ComplaintStateMachine.Check(
            from,
            action,
            UserRole.Staff,
            Assignee,
            assignedStaffId: null);

        Assert.Equal(TransitionOutcome.NotTheAssignee, check.Outcome);
    }

    /// <summary>
    /// A Dept Admin is not a superset of Staff. §9.1 has no super-admin, and `start` and
    /// `resolve` belong to the assignee alone — an admin cannot do the work for them.
    /// </summary>
    [Theory]
    [InlineData(ComplaintStatus.Assigned, ComplaintAction.Start)]
    [InlineData(ComplaintStatus.InProgress, ComplaintAction.Resolve)]
    public void ADeptAdminCannotDoTheAssigneesWork(ComplaintStatus from, ComplaintAction action)
    {
        var check = ComplaintStateMachine.Check(from, action, UserRole.DeptAdmin, Assignee, Assignee);

        Assert.Equal(TransitionOutcome.WrongRole, check.Outcome);
    }

    /// <summary>A Citizen may only ever close or reopen, and only from Resolved.</summary>
    [Theory]
    [InlineData(ComplaintStatus.New, ComplaintAction.Assign)]
    [InlineData(ComplaintStatus.New, ComplaintAction.Reject)]
    [InlineData(ComplaintStatus.New, ComplaintAction.Recategorize)]
    [InlineData(ComplaintStatus.Assigned, ComplaintAction.Start)]
    [InlineData(ComplaintStatus.InProgress, ComplaintAction.Resolve)]
    public void ACitizenCannotRunTheDepartment(ComplaintStatus from, ComplaintAction action)
    {
        var check = ComplaintStateMachine.Check(from, action, UserRole.Citizen, Assignee, Assignee);

        Assert.Equal(TransitionOutcome.WrongRole, check.Outcome);
    }

    [Theory]
    [InlineData(ComplaintAction.Close)]
    [InlineData(ComplaintAction.Reopen)]
    public void ACitizenMayCloseAndReopenAResolvedComplaint(ComplaintAction action)
    {
        var check = ComplaintStateMachine.Check(
            ComplaintStatus.Resolved,
            action,
            UserRole.Citizen,
            Assignee,
            Assignee);

        Assert.True(check.IsAllowed);
    }

    /// <summary>
    /// §11.5: the sweeper auto-closes as `System`, and that is the **only** thing it may do
    /// unattended. A system actor must not be able to reject or reassign anything.
    /// </summary>
    [Fact]
    public void TheSystemMayOnlyClose()
    {
        var close = ComplaintStateMachine.Check(
            ComplaintStatus.Resolved,
            ComplaintAction.Close,
            UserRole.Staff,
            Guid.Empty,
            null,
            isSystem: true);

        Assert.True(close.IsAllowed);

        foreach (var (from, action) in ComplaintStateMachine.DefinedTransitions)
        {
            if (action == ComplaintAction.Close)
            {
                continue;
            }

            var check = ComplaintStateMachine.Check(
                from,
                action,
                UserRole.DeptAdmin,
                Guid.Empty,
                null,
                isSystem: true);

            Assert.False(check.IsAllowed, $"the system must not be able to {action}");
        }
    }

    // -----------------------------------------------------------------------------------
    // Required payload
    // -----------------------------------------------------------------------------------

    /// <summary>§12.3's last column, transcribed.</summary>
    [Theory]
    [InlineData(ComplaintStatus.New, ComplaintAction.Assign, TransitionPayload.AssigneeId)]
    [InlineData(ComplaintStatus.New, ComplaintAction.Recategorize, TransitionPayload.CategoryId)]
    [InlineData(ComplaintStatus.New, ComplaintAction.Reject, TransitionPayload.Note)]
    [InlineData(ComplaintStatus.Assigned, ComplaintAction.Reassign, TransitionPayload.AssigneeId)]
    [InlineData(ComplaintStatus.Assigned, ComplaintAction.Start, TransitionPayload.None)]
    [InlineData(ComplaintStatus.InProgress, ComplaintAction.Resolve, TransitionPayload.Note)]
    [InlineData(ComplaintStatus.Resolved, ComplaintAction.Close, TransitionPayload.None)]
    [InlineData(ComplaintStatus.Resolved, ComplaintAction.Reopen, TransitionPayload.Note)]
    public void RequiredPayloadMatchesTheTable(
        ComplaintStatus from,
        ComplaintAction action,
        TransitionPayload expected)
    {
        Assert.Equal(expected, ComplaintStateMachine.Find(from, action)!.RequiredPayload);
    }

    // -----------------------------------------------------------------------------------
    // availableActions — what the UI renders buttons from
    // -----------------------------------------------------------------------------------

    /// <summary>
    /// The list the client renders buttons from must never contain an action the write path
    /// would refuse. Checked against the guard table for every status and every role.
    /// </summary>
    [Fact]
    public void EveryAvailableActionWouldActuallyBeAccepted()
    {
        foreach (var status in Enum.GetValues<ComplaintStatus>())
        {
            foreach (var role in Enum.GetValues<UserRole>())
            {
                var available = ComplaintStateMachine.AvailableActions(
                    status,
                    role,
                    Assignee,
                    Assignee,
                    isOwner: true);

                foreach (var action in available)
                {
                    var check = ComplaintStateMachine.Check(status, action, role, Assignee, Assignee);

                    Assert.True(
                        check.IsAllowed,
                        $"{role} was offered {action} from {status} but the table refuses it");
                }
            }
        }
    }

    /// <summary>
    /// §12.4: a Citizen may close or reopen only their **own** complaint. The guard table
    /// cannot express ownership, so this is the one rule applied beside it — and a citizen
    /// who is not the owner must be offered nothing at all.
    /// </summary>
    [Fact]
    public void ACitizenWhoIsNotTheOwnerIsOfferedNothing()
    {
        var owner = ComplaintStateMachine.AvailableActions(
            ComplaintStatus.Resolved, UserRole.Citizen, Assignee, Assignee, isOwner: true);

        var stranger = ComplaintStateMachine.AvailableActions(
            ComplaintStatus.Resolved, UserRole.Citizen, Assignee, Assignee, isOwner: false);

        Assert.Equal([ComplaintAction.Close, ComplaintAction.Reopen], owner.OrderBy(a => a));
        Assert.Empty(stranger);
    }

    [Fact]
    public void AClosedComplaintOffersNobodyAnything()
    {
        foreach (var role in Enum.GetValues<UserRole>())
        {
            Assert.Empty(ComplaintStateMachine.AvailableActions(
                ComplaintStatus.Closed, role, Assignee, Assignee, isOwner: true));

            Assert.Empty(ComplaintStateMachine.AvailableActions(
                ComplaintStatus.Rejected, role, Assignee, Assignee, isOwner: true));
        }
    }

    /// <summary>
    /// <c>Submit</c> and <c>Priority</c> are history actions, not transitions (§12.2). They
    /// must never appear in the guard table, or a caller could send one to the transitions
    /// endpoint and move a complaint with it.
    /// </summary>
    [Theory]
    [InlineData(ComplaintAction.Submit)]
    [InlineData(ComplaintAction.Priority)]
    public void HistoryOnlyActionsAreNotInTheGuardTable(ComplaintAction action)
    {
        Assert.DoesNotContain(action, ComplaintStateMachine.DefinedTransitions.Select(t => t.Action));

        foreach (var status in Enum.GetValues<ComplaintStatus>())
        {
            Assert.Null(ComplaintStateMachine.Find(status, action));
        }
    }
}
