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
