using Microsoft.EntityFrameworkCore;
using Obhijog.Domain.Complaints;
using Obhijog.Domain.Exceptions;
using Obhijog.Domain.Notifications;
using Obhijog.Domain.Sla;
using Obhijog.Domain.Users;
using Obhijog.Infrastructure.Auth;
using Obhijog.Infrastructure.Persistence;

namespace Obhijog.Infrastructure.Complaints;

/// <summary>
/// The **only** write path for a complaint's status. SPEC.md §12, CLAUDE.md non-negotiable 3.
///
/// Every one of the twelve transitions comes through here, from the single
/// <c>POST /complaints/{id}/transitions</c> endpoint, consulting
/// <see cref="ComplaintStateMachine"/>. There are no verb endpoints and
/// <c>complaint.Status = …</c> appears nowhere else in the codebase.
///
/// This service owns the rules §12.4 says the guard table cannot carry — assignee validity,
/// the recategorize reroute, the reopen clock reset — and it writes exactly one history row
/// per accepted transition, in the same transaction as the mutation.
/// </summary>
public class ComplaintTransitionService(
    ObhijogDbContext db,
    ComplaintService complaints,
    ICurrentUser currentUser,
    TimeProvider timeProvider)
{
    public async Task<ComplaintDetail> TransitionAsync(
        Guid complaintId,
        TransitionRequest request,
        CancellationToken cancellationToken = default)
    {
        // Tracked, not a projection: this is a write path and it loads what it mutates.
        // Scoped first, so a complaint the caller cannot see is 404 and never 403 (§9.2).
        var complaint = await ComplaintQueryScope
            .For(db.Complaints, currentUser)
            .SingleOrDefaultAsync(c => c.Id == complaintId, cancellationToken)
            ?? throw new NotFoundException($"Complaint '{complaintId}' was not found.");

        var check = ComplaintStateMachine.Check(
            complaint.Status,
            request.Action,
            currentUser.Role,
            currentUser.Id,
            complaint.AssignedStaffId);

        var rule = Guard(complaint, check);

        // Ownership is the one rule the guard table cannot express — it does not know who
        // the citizen is. A Citizen acting on someone else's complaint never gets this far
        // (the scope filter already made it a 404), so this only catches a Citizen acting
        // on a complaint of theirs in a way §12.3 allows.
        if (currentUser.Role == UserRole.Citizen && complaint.CitizenId != currentUser.Id)
        {
            throw new ForbiddenException("Only the citizen who filed a complaint may do this.");
        }

        var now = timeProvider.GetUtcNow();
        var from = complaint.Status;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var note = await ApplyAsync(complaint, request, rule, now, cancellationToken);

        complaint.Status = rule.To;

        db.ComplaintStatusHistories.Add(new ComplaintStatusHistory
        {
            Id = Guid.CreateVersion7(),
            ComplaintId = complaint.Id,
            FromStatus = from,
            ToStatus = rule.To,
            Action = request.Action,
            ChangedById = currentUser.Id,
            ChangedAt = now,
            Note = note,
            IsSystem = false,
        });

        await SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return await ReadBackAsync(complaint.Id, cancellationToken);
    }

    /// <summary>
    /// §12.4's priority edit. Status-independent, Dept Admin only, and **not** a transition —
    /// it never moves the complaint, so it has no guard-table row. It still writes a history
    /// row, because a field a Dept Admin can change silently is a field nobody can audit.
    /// </summary>
    public async Task<ComplaintDetail> ChangePriorityAsync(
        Guid complaintId,
        ComplaintPriority priority,
        CancellationToken cancellationToken = default)
    {
        var complaint = await ComplaintQueryScope
            .For(db.Complaints, currentUser)
            .SingleOrDefaultAsync(c => c.Id == complaintId, cancellationToken)
            ?? throw new NotFoundException($"Complaint '{complaintId}' was not found.");

        if (complaint.Priority == priority)
        {
            throw new ValidationException(
                "priority",
                $"The complaint is already '{priority}'.");
        }

        var now = timeProvider.GetUtcNow();
        var previous = complaint.Priority;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        complaint.Priority = priority;

        db.ComplaintStatusHistories.Add(new ComplaintStatusHistory
        {
            Id = Guid.CreateVersion7(),
            ComplaintId = complaint.Id,

            // Nothing moved. §8.5 allows FromStatus == ToStatus for exactly this kind of row.
            FromStatus = complaint.Status,
            ToStatus = complaint.Status,
            Action = ComplaintAction.Priority,
            ChangedById = currentUser.Id,
            ChangedAt = now,
            Note = $"Priority changed from {previous} to {priority}.",
            IsSystem = false,
        });

        await SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return await ReadBackAsync(complaint.Id, cancellationToken);
    }

    /// <summary>
    /// §11.5's auto-close, performed by the sweeper as the <c>System</c> actor.
    ///
    /// It lives here, on the single write path, rather than in <c>SlaSweeper</c>, because
    /// CLAUDE.md non-negotiable 3 is literal: <c>complaint.Status = …</c> appears in this
    /// file and nowhere else. The sweeper decides *which* complaints are due for closing;
    /// the guard table still decides whether closing them is legal, and row 11 of §12.3
    /// carries <c>AllowSystem: true</c> for exactly this caller.
    ///
    /// Two differences from <see cref="TransitionAsync"/>, both deliberate:
    ///
    /// <list type="bullet">
    /// <item>The complaint is loaded through <c>ComplaintQueryScope.ForSystem</c>, not
    /// <c>For</c>. A sweep is not a caller — there is no signed-in user to scope to, and
    /// scoping it to the ambient (unauthenticated) <c>ICurrentUser</c> would silently
    /// close nothing at all.</item>
    /// <item>It returns a <c>bool</c> rather than a <c>ComplaintDetail</c>. Nobody is
    /// waiting on the response, and projecting one would need a caller to compute
    /// <c>availableActions</c> for.</item>
    /// </list>
    ///
    /// Returns <c>false</c> when the complaint moved between the sweeper selecting it and
    /// this call — a citizen who closed or reopened it in that window wins, and the sweep
    /// records nothing rather than forcing the issue.
    /// </summary>
    internal async Task<bool> CloseAsSystemAsync(
        Guid complaintId,
        string note,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var complaint = await ComplaintQueryScope
            .ForSystem(db.Complaints)
            .SingleOrDefaultAsync(c => c.Id == complaintId, cancellationToken);

        if (complaint is null)
        {
            return false;
        }

        var check = ComplaintStateMachine.Check(
            complaint.Status,
            ComplaintAction.Close,

            // The role is ignored once isSystem is set, but the parameter is not optional,
            // so this passes the one role row 11 does **not** permit. AllowSystem is then
            // the only thing authorising the close, and a mutation that drops isSystem
            // fails closed instead of quietly succeeding on the role's own authority —
            // which is exactly what happened when this said UserRole.Citizen, a role §12.3
            // lets close a resolved complaint.
            //
            // AllowSystem is a separate column of §12.3 precisely so that "the system" never
            // has to impersonate a person.
            UserRole.Staff,
            Guid.Empty,
            complaint.AssignedStaffId,
            isSystem: true);

        if (!check.IsAllowed)
        {
            return false;
        }

        var from = complaint.Status;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        complaint.Status = check.Rule!.To;
        complaint.ClosedAt = now;

        db.ComplaintStatusHistories.Add(new ComplaintStatusHistory
        {
            Id = Guid.CreateVersion7(),
            ComplaintId = complaint.Id,
            FromStatus = from,
            ToStatus = check.Rule.To,
            Action = ComplaintAction.Close,

            // Null actor and IsSystem together — the check constraint of §8.5 rejects any
            // other combination, which is what stops a system row from being attributed to
            // a person.
            ChangedById = null,
            ChangedAt = now,
            Note = note,
            IsSystem = true,
        });

        await SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return true;
    }

    // ---------------------------------------------------------------------------------

    /// <summary>Turns a <see cref="TransitionCheck"/> into the status code §12.3 requires.</summary>
    private static TransitionRule Guard(Complaint complaint, TransitionCheck check) =>
        check.Outcome switch
        {
            TransitionOutcome.Allowed => check.Rule!,

            // An absent (from, action) pair. The message names the current status and what
            // *is* possible, because "409" alone tells the caller nothing actionable.
            TransitionOutcome.NotAllowedFromHere => throw new InvalidTransitionException(
                check.Message
                + " Available from here: "
                + string.Join(
                    ", ",
                    ComplaintStateMachine.DefinedTransitions
                        .Where(t => t.From == complaint.Status)
                        .Select(t => t.Action.ToString())
                        .DefaultIfEmpty("nothing"))
                + "."),

            _ => throw new ForbiddenException(check.Message),
        };

    /// <summary>
    /// The per-action effects of §12.4. Returns the note to record on the history row.
    /// </summary>
    private async Task<string?> ApplyAsync(
        Complaint complaint,
        TransitionRequest request,
        TransitionRule rule,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        RequirePayload(request, rule);

        switch (request.Action)
        {
            case ComplaintAction.Assign:
            case ComplaintAction.Reassign:
                {
                    var assigneeId = request.AssigneeId!.Value;

                    // §12.4: reassigning to the current assignee is a 400, not a no-op row.
                    if (complaint.AssignedStaffId == assigneeId)
                    {
                        throw new ValidationException(
                            "assigneeId",
                            "That staff member is already assigned to this complaint.");
                    }

                    await EnsureAssignableAsync(complaint, assigneeId, cancellationToken);
                    complaint.AssignedStaffId = assigneeId;

                    await NotifyAsync(
                        assigneeId,
                        complaint,
                        NotificationType.ComplaintAssigned,
                        $"Complaint {complaint.ReferenceNumber} assigned to you",
                        $"\"{complaint.Title}\" is now yours to work on.",
                        now);

                    return request.Note;
                }

            case ComplaintAction.Recategorize:
                {
                    var category = await db.ComplaintCategories
                        .AsNoTracking()
                        .SingleOrDefaultAsync(c => c.Id == request.CategoryId!.Value, cancellationToken);

                    if (category is null || !category.IsActive)
                    {
                        throw new ValidationException(
                            "categoryId",
                            "The category does not exist or is no longer active.");
                    }

                    complaint.CategoryId = category.Id;
                    complaint.DepartmentId = category.DepartmentId;

                    // §12.4: always back to New and always clears the assignee, even when the
                    // department did not change. Uniform beats clever (D5).
                    complaint.AssignedStaffId = null;

                    // §11.4: recomputed from the **original** creation time, not from now, and
                    // the breach markers are deliberately left alone — rerouting is not a way to
                    // erase a missed deadline.
                    complaint.SlaDueAt = SlaPolicy.DueAt(complaint.CreatedAt, category.SlaHours);

                    return request.Note ?? $"Recategorized to {category.Name}.";
                }

            case ComplaintAction.Start:
                return request.Note;

            case ComplaintAction.Resolve:
                // §11.1: the SLA is measured to ResolvedAt, not ClosedAt.
                complaint.ResolvedAt = now;
                complaint.ResolutionNote = request.Note;

                await NotifyAsync(
                    complaint.CitizenId,
                    complaint,
                    NotificationType.ComplaintResolved,
                    $"Complaint {complaint.ReferenceNumber} resolved",
                    request.Note ?? "Your complaint has been resolved.",
                    now);

                return request.Note;

            case ComplaintAction.Reject:
                complaint.RejectionReason = request.Note;

                await NotifyAsync(
                    complaint.CitizenId,
                    complaint,
                    NotificationType.ComplaintRejected,
                    $"Complaint {complaint.ReferenceNumber} rejected",
                    request.Note ?? "Your complaint was rejected.",
                    now);

                return request.Note;

            case ComplaintAction.Close:
                complaint.ClosedAt = now;
                return request.Note;

            case ComplaintAction.Reopen:
                {
                    var category = await db.ComplaintCategories
                        .AsNoTracking()
                        .SingleAsync(c => c.Id == complaint.CategoryId, cancellationToken);

                    // §11.4: a fresh window from now. CreatedAt is not touched — the complaint
                    // is still as old as it is — but the markers clear and the ladder may
                    // legitimately run again, which is why ReopenCount is part of the
                    // escalation unique key.
                    complaint.SlaDueAt = SlaPolicy.DueAt(now, category.SlaHours);
                    complaint.SlaWarnedAt = null;
                    complaint.SlaBreachedAt = null;
                    complaint.EscalationLevel = 0;
                    complaint.ResolvedAt = null;
                    complaint.ResolutionNote = null;
                    complaint.ClosedAt = null;
                    complaint.ReopenCount += 1;

                    // Back to Assigned per row 12, and the previous assignee keeps it.
                    if (complaint.AssignedStaffId is { } assignee)
                    {
                        await NotifyAsync(
                            assignee,
                            complaint,
                            NotificationType.ComplaintReopened,
                            $"Complaint {complaint.ReferenceNumber} reopened",
                            request.Note ?? "The citizen reopened this complaint.",
                            now);
                    }

                    return request.Note;
                }

            default:
                // Submit and Priority are history actions, not transitions, so they have no
                // guard-table row and cannot reach this switch. Reaching it means the table
                // and this method have drifted.
                throw new InvalidTransitionException(
                    $"'{request.Action}' is not a transition action.");
        }
    }

    private static void RequirePayload(TransitionRequest request, TransitionRule rule)
    {
        var missing = rule.RequiredPayload switch
        {
            TransitionPayload.AssigneeId when request.AssigneeId is null => "assigneeId",
            TransitionPayload.CategoryId when request.CategoryId is null => "categoryId",
            TransitionPayload.Note when string.IsNullOrWhiteSpace(request.Note) => "note",
            _ => null,
        };

        if (missing is not null)
        {
            throw new ValidationException(missing, $"'{missing}' is required for this action.");
        }
    }

    /// <summary>
    /// §12.4: the assignee must be an **active `Staff` user in the complaint's department**,
    /// checked against the *current* department — which a recategorize may just have changed.
    /// </summary>
    private async Task EnsureAssignableAsync(
        Complaint complaint,
        Guid assigneeId,
        CancellationToken cancellationToken)
    {
        var assignable = await db.Users
            .AsNoTracking()
            .AnyAsync(
                u => u.Id == assigneeId
                    && u.Role == UserRole.Staff
                    && u.IsActive
                    && u.DepartmentId == complaint.DepartmentId,
                cancellationToken);

        if (!assignable)
        {
            throw new ValidationException(
                "assigneeId",
                "The assignee must be an active staff member in this complaint's department.");
        }
    }

    private async Task NotifyAsync(
        Guid recipientId,
        Complaint complaint,
        NotificationType type,
        string subject,
        string body,
        DateTimeOffset now)
    {
        db.Notifications.Add(new Notification
        {
            Id = Guid.CreateVersion7(),
            RecipientId = recipientId,
            ComplaintId = complaint.Id,
            Type = type,
            Subject = subject,
            Body = body,
            CreatedAt = now,
        });

        await Task.CompletedTask;
    }

    /// <summary>
    /// §12.4: a concurrent conflicting transition loses on <c>xmin</c> and gets a <c>409</c>.
    /// It is not retried — the caller decided against stale state and needs to see that.
    /// </summary>
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Rethrown as itself: the one exception handler already maps it to 409, and
            // wrapping it would lose the type the handler keys on.
            throw;
        }
    }

    /// <summary>
    /// Reads the complaint back through the normal detail projection, so the response
    /// carries the same shape — and the same freshly computed <c>availableActions</c> — as
    /// a plain GET would.
    ///
    /// Through <c>GetAfterWriteAsync</c> rather than <c>GetAsync</c>: a `recategorize` can
    /// move the complaint into another department, and re-applying the scope would answer
    /// <c>404</c> to the very transition that just succeeded. The write was authorised on
    /// the way in; see that method for why this is not a scope leak — and why it still
    /// returns an empty <c>availableActions</c> when the caller has just rerouted the
    /// complaint away from themselves.
    /// </summary>
    private async Task<ComplaintDetail> ReadBackAsync(Guid id, CancellationToken cancellationToken)
    {
        // Cleared so the read is a genuine read: the tracked entity this method just mutated
        // would otherwise be handed back by identity resolution, hiding any divergence
        // between what was written and what the projection reports.
        db.ChangeTracker.Clear();

        return await complaints.GetAfterWriteAsync(id, cancellationToken);
    }
}

/// <summary>
/// <c>POST /complaints/{id}/transitions</c>. SPEC.md §13.2 — one endpoint, one body shape,
/// every movement.
/// </summary>
public record TransitionRequest
{
    public ComplaintAction Action { get; init; }

    public string? Note { get; init; }

    public Guid? AssigneeId { get; init; }

    public Guid? CategoryId { get; init; }
}

/// <summary><c>PUT /complaints/{id}/priority</c>.</summary>
public record ChangePriorityRequest
{
    public ComplaintPriority Priority { get; init; }
}
