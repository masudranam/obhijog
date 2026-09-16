namespace Obhijog.Domain.Notifications;

/// <summary>
/// SPEC.md §8.9.
///
/// Writing the row and delivering it are separate concerns: the row is the record,
/// delivery is best-effort (F12). A failed send increments <see cref="Attempts"/> and
/// records <see cref="LastError"/> without failing the sweep.
/// </summary>
public class Notification
{
    public Guid Id { get; set; }

    public Guid RecipientId { get; set; }

    public Guid? ComplaintId { get; set; }

    /// <summary>
    /// The complaint's <c>ReopenCount</c> when this row was written, copied for the same
    /// reason <see cref="Obhijog.Domain.Complaints.EscalationEvent.ReopenCount"/> exists:
    /// a reopened complaint legitimately re-runs the SLA ladder, so "already notified" has
    /// to mean "already notified *this time round*".
    ///
    /// Zero for everything that is not an SLA notification; the unique index that reads it
    /// is filtered to the three SLA types (§11.3 defence 4).
    /// </summary>
    public int ReopenCount { get; set; }

    public NotificationType Type { get; set; }

    /// <summary>Max 160 characters.</summary>
    public required string Subject { get; set; }

    /// <summary>Max 2000 characters.</summary>
    public required string Body { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Stamped by <c>INotificationSender</c> on successful delivery.</summary>
    public DateTimeOffset? SentAt { get; set; }

    public int Attempts { get; set; }

    /// <summary>Max 500 characters.</summary>
    public string? LastError { get; set; }
}
