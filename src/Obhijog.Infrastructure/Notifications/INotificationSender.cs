using Obhijog.Domain.Notifications;

namespace Obhijog.Infrastructure.Notifications;

/// <summary>
/// Delivery. SPEC.md §14 F12.
///
/// Writing the <see cref="Notification"/> row and delivering it are deliberately separate:
/// the row is the record and lands inside the sweep's transaction, delivery is best-effort
/// and happens after the commit. A sender that threw inside the transaction would roll back
/// the escalation it was trying to announce — which is exactly the failure mode §11.3's
/// idempotency is built to survive, and exactly the one worth not needing.
/// </summary>
public interface INotificationSender
{
    /// <summary>
    /// Attempts delivery of one notification.
    ///
    /// Implementations <b>report</b> failure rather than throwing: the sweep must not die
    /// because an SMTP server is down. <see cref="NotificationDelivery.Failed"/> carries the
    /// message that goes into <c>Notification.LastError</c>.
    /// </summary>
    Task<NotificationDelivery> SendAsync(
        Notification notification,
        CancellationToken cancellationToken = default);
}

/// <summary>The outcome of one delivery attempt.</summary>
public record NotificationDelivery(bool Delivered, string? Error)
{
    public static readonly NotificationDelivery Success = new(true, null);

    public static NotificationDelivery Failed(string error) => new(false, error);
}
