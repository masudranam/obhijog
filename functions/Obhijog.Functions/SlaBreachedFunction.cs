using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Obhijog.Infrastructure.Messaging;

namespace Obhijog.Functions;

/// <summary>
/// The Service Bus trigger of SPEC.md §14 F16.
///
/// <b>There is deliberately no logic here.</b> Every decision — who the recipients are, what
/// happens when the complaint was reopened after publication, and above all why a redelivered
/// message writes nothing — lives in <see cref="SlaBreachNotificationHandler"/> in
/// <c>Obhijog.Infrastructure</c>, where a test can reach it against a real PostgreSQL with no
/// Azure subscription, no Service Bus namespace and no Functions host. A handler that could
/// only be exercised end-to-end would be one this project could never verify at all, and F16's
/// "a test asserts it" would be a promise it could not keep.
/// </summary>
public class SlaBreachedFunction(
    SlaBreachNotificationHandler handler,
    ILogger<SlaBreachedFunction> logger)
{
    [Function(nameof(SlaBreachedFunction))]
    public async Task RunAsync(
        // Flat names on purpose. `%NAME%` is a literal app-setting lookup, and a colon in an
        // app-setting name does not survive the Linux Functions host the way `__` does — so
        // the queue name is `ServiceBusQueueName`, not `ServiceBus:QueueName`. `Connection`
        // names the setting holding the connection string, which Bicep fills from Key Vault.
        [ServiceBusTrigger("%ServiceBusQueueName%", Connection = "ServiceBus")]
        SlaBreachedMessage message,
        CancellationToken cancellationToken)
    {
        // Not wrapped in a try/catch. A genuine fault — the database unreachable, say —
        // should abandon the message so Service Bus redelivers it, and redelivery is safe
        // precisely because of §11.3 defence 4. The one case that must *not* throw is a
        // message already handled, and the handler returns 0 for that rather than raising,
        // so it never becomes a poison loop over work that already succeeded.
        var written = await handler.HandleAsync(message, cancellationToken);

        logger.LogInformation(
            "SlaBreachedFunction handled complaint {ComplaintId} (reopen {ReopenCount}): "
            + "{Written} notifications written.",
            message.ComplaintId,
            message.ReopenCount,
            written);
    }
}
