using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Obhijog.Domain.Complaints;
using Obhijog.Domain.Notifications;
using Obhijog.Domain.Users;
using Obhijog.Infrastructure.Complaints;
using Obhijog.Infrastructure.Notifications;
using Obhijog.Infrastructure.Persistence;

namespace Obhijog.Infrastructure.Messaging;

/// <summary>
/// Writes the notification rows for one <see cref="SlaBreachedMessage"/>. SPEC.md §14 F16.
///
/// <b>This lives in Infrastructure, not in the Function.</b> The Function project is a
/// trigger attribute and a one-line call; every decision this milestone makes is here, where
/// it can be tested against a real PostgreSQL without an Azure subscription, a Service Bus
/// namespace or the Functions host. A handler that could only be exercised end-to-end would
/// be a handler this project could never verify at all.
///
/// <b>Why a redelivered message is harmless, precisely.</b> Not because §11.3's three defences
/// cover it — they do not, and that was the thing worth finding in M10. Defences 1 and 3 are
/// the sweeper's: the not-yet-done <c>WHERE</c> clause and the unique index on
/// <c>EscalationEvent</c>. Neither is in play here, because this handler does not select work
/// and writes no escalation row. Until M10 the notification guarantee was *borrowed* from
/// defence 3 through defence 2's shared transaction — the escalation insert failed and rolled
/// the notifications back with it. Moving delivery onto a queue severs that borrowing.
///
/// What replaces it is defence 4: the partial unique index on
/// <c>(ComplaintId, RecipientId, Type, ReopenCount)</c> over the three SLA types. A second
/// delivery of the same message inserts the same four-tuple, PostgreSQL rejects it, the
/// transaction rolls back, and the handler reports that it wrote nothing. That is a real
/// guarantee rather than a hopeful one, and it is enforced by the database rather than by
/// this code remembering to check.
/// </summary>
public class SlaBreachNotificationHandler(
    ObhijogDbContext db,
    NotificationDispatcher dispatcher,
    ILogger<SlaBreachNotificationHandler> logger)
{
    /// <summary>
    /// Handles one message. Returns the number of notification rows written — zero when the
    /// message is a redelivery, which is a success and not a failure.
    /// </summary>
    /// <remarks>
    /// Never throws for a message it has already handled. A Service Bus trigger that throws
    /// abandons the message, which redelivers it, which throws again — a poison loop built out
    /// of a message that was in fact processed correctly the first time.
    /// </remarks>
    public async Task<int> HandleAsync(
        SlaBreachedMessage message,
        CancellationToken cancellationToken = default)
    {
        var complaint = await ComplaintQueryScope
            .ForSystem(db.Complaints)
            .AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == message.ComplaintId, cancellationToken);

        if (complaint is null)
        {
            // Deleted between publish and delivery. Nothing to notify anyone about, and
            // retrying will not bring it back, so this completes the message rather than
            // dead-lettering it.
            logger.LogWarning(
                "SlaBreached message for unknown complaint {ComplaintId}; completing it.",
                message.ComplaintId);

            return 0;
        }

        if (complaint.ReopenCount != message.ReopenCount)
        {
            // The complaint was reopened after this message was published, so the breach it
            // describes belongs to a finished cycle. Writing the notification now would tell
            // someone their complaint is overdue when its clock has already been reset (§11.4).
            logger.LogInformation(
                "SlaBreached message for complaint {ComplaintId} is from reopen cycle "
                + "{MessageReopenCount}; the complaint is now on {CurrentReopenCount}. Skipping.",
                message.ComplaintId,
                message.ReopenCount,
                complaint.ReopenCount);

            return 0;
        }

        var recipients = await RecipientsAsync(complaint, cancellationToken);

        var written = recipients
            .Select(recipient => new Notification
            {
                Id = Guid.CreateVersion7(),
                RecipientId = recipient,
                ComplaintId = complaint.Id,
                ReopenCount = complaint.ReopenCount,
                Type = NotificationType.SlaBreached,
                Subject = $"Complaint {complaint.ReferenceNumber} has breached its SLA",
                Body = $"\"{complaint.Title}\" was due {complaint.SlaDueAt:u} and is still open.",
                CreatedAt = message.BreachedAt,
            })
            .ToList();

        db.Notifications.AddRange(written);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsDuplicate(exception))
        {
            // Defence 4 firing. This is the redelivery case and it is the designed outcome,
            // so it is logged at information and reported as zero rows — not rethrown, which
            // would abandon a message that has already been handled correctly.
            db.ChangeTracker.Clear();

            logger.LogInformation(
                "SlaBreached message for complaint {ComplaintId} (reopen {ReopenCount}) was "
                + "already handled; the unique index refused the duplicate and nothing was "
                + "written. This is redelivery working as designed (§11.3 defence 4).",
                message.ComplaintId,
                message.ReopenCount);

            return 0;
        }

        // After the save, exactly as the sweeper does it, and through the same code: delivery
        // is best-effort and a failing sender must not roll back the rows recording what
        // should be sent (F12). NotificationDispatcher stamps Attempts, SentAt and LastError
        // — an earlier draft of this handler looped over the sender itself and silently
        // dropped all three, which is exactly the drift non-negotiable 9 warns about.
        await dispatcher.DeliverAsync(written, cancellationToken);

        logger.LogInformation(
            "SlaBreached handled for complaint {ComplaintId}: {Count} notifications written.",
            message.ComplaintId,
            written.Count);

        return written.Count;
    }

    /// <summary>
    /// §11.2's breach recipients: the assignee and every active Dept Admin in the department.
    ///
    /// Re-derived here rather than carried in the message, so a reassignment between publish
    /// and delivery notifies whoever actually holds the complaint now.
    /// </summary>
    private async Task<List<Guid>> RecipientsAsync(
        Complaint complaint,
        CancellationToken cancellationToken)
    {
        var admins = await db.Users
            .AsNoTracking()
            .Where(u => u.DepartmentId == complaint.DepartmentId
                && u.Role == UserRole.DeptAdmin
                && u.IsActive)
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);

        return complaint.AssignedStaffId is { } assignee
            ? admins.Prepend(assignee).Distinct().ToList()
            : admins;
    }

    /// <summary>
    /// PostgreSQL's unique-violation SQLSTATE. Matched on the code rather than on the index
    /// name so that renaming the index does not quietly turn this catch into a poison loop.
    /// </summary>
    private static bool IsDuplicate(DbUpdateException exception) =>
        exception.InnerException is Npgsql.PostgresException { SqlState: "23505" };
}
