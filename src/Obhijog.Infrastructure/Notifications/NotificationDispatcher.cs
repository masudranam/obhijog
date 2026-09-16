using Microsoft.Extensions.Logging;
using Obhijog.Domain.Notifications;
using Obhijog.Infrastructure.Persistence;

namespace Obhijog.Infrastructure.Notifications;

/// <summary>
/// Attempts delivery of notification rows and records what happened. SPEC.md §14 F12.
///
/// Extracted in M10 because it acquired a second caller. Until then this loop lived inside
/// <c>SlaSweeper</c>; the Service Bus handler needs exactly the same behaviour, and a second
/// copy of it would be a second place for <c>Attempts</c> to stop being incremented or
/// <c>SentAt</c> to stop being stamped — the kind of drift CLAUDE.md non-negotiable 9 is
/// about. Both callers now run this code or neither does.
///
/// <b>Always called after the caller's transaction has committed.</b> Delivery is best-effort
/// and the row is the record (F12): a sender that throws must not roll back the escalation it
/// was trying to announce.
/// </summary>
public class NotificationDispatcher(
    ObhijogDbContext db,
    INotificationSender sender,
    TimeProvider timeProvider,
    ILogger<NotificationDispatcher> logger)
{
    public async Task DeliverAsync(
        IReadOnlyList<Notification> notifications,
        CancellationToken cancellationToken = default)
    {
        if (notifications.Count == 0)
        {
            return;
        }

        foreach (var notification in notifications)
        {
            notification.Attempts++;

            try
            {
                var delivery = await sender.SendAsync(notification, cancellationToken);

                if (delivery.Delivered)
                {
                    notification.SentAt = timeProvider.GetUtcNow();
                    notification.LastError = null;
                }
                else
                {
                    notification.LastError = Truncate(delivery.Error);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A sender that throws instead of reporting is still not allowed to take the
                // caller down. The row keeps its attempt count and its error.
                notification.LastError = Truncate(exception.Message);

                logger.LogWarning(
                    exception,
                    "Notification {NotificationId} could not be delivered.",
                    notification.Id);
            }
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Delivery outcomes could not be recorded.");
        }
    }

    /// <summary>`Notification.LastError` is varchar(500) (§8.9).</summary>
    private static string? Truncate(string? value) =>
        value is null || value.Length <= 500 ? value : value[..500];
}
