using Microsoft.Extensions.Logging;
using Obhijog.Domain.Notifications;

namespace Obhijog.Infrastructure.Notifications;

/// <summary>
/// The MVP delivery channel of F12: a structured log line. <c>Notifications:Delivery = Log</c>.
///
/// It is not a stub. The notification row is the durable record and it is already written;
/// this is the channel, and a log line is a real one for a system whose operators read logs.
/// Swapping in email means registering a different <see cref="INotificationSender"/> and
/// changing nothing else — which is why the sweeper never formats a message itself.
/// </summary>
public class LogNotificationSender(ILogger<LogNotificationSender> logger) : INotificationSender
{
    public Task<NotificationDelivery> SendAsync(
        Notification notification,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "Notification {Type} to {RecipientId} for complaint {ComplaintId}: {Subject}",
            notification.Type,
            notification.RecipientId,
            notification.ComplaintId,
            notification.Subject);

        return Task.FromResult(NotificationDelivery.Success);
    }
}
