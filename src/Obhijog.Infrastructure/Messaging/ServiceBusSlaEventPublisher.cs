using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Obhijog.Infrastructure.Options;

namespace Obhijog.Infrastructure.Messaging;

/// <summary>
/// <c>Sla:Transport = ServiceBus</c>. SPEC.md §14 F16.
///
/// The sweeper records the breach and this carries it to <c>Obhijog.Functions</c>, which
/// writes the notification rows. Detection does not move; only delivery does.
/// </summary>
public sealed class ServiceBusSlaEventPublisher(
    ServiceBusClient client,
    IOptions<ServiceBusOptions> options,
    ILogger<ServiceBusSlaEventPublisher> logger) : ISlaEventPublisher, IAsyncDisposable
{
    private readonly Lazy<ServiceBusSender> _sender =
        new(() => client.CreateSender(options.Value.QueueName));

    public bool WritesNotificationsInline => false;

    public async Task PublishBreachAsync(
        SlaBreachedMessage message,
        CancellationToken cancellationToken = default)
    {
        var payload = new ServiceBusMessage(JsonSerializer.SerializeToUtf8Bytes(message))
        {
            ContentType = "application/json",
            Subject = nameof(SlaBreachedMessage),

            // Deterministic, so a queue with duplicate detection enabled drops an immediate
            // re-publish for free. It is a cheap first line and not the guarantee: the
            // detection window is finite and redelivery after it is ordinary, which is why
            // the handler leans on the unique index instead (§11.3 defence 4).
            MessageId = message.MessageId,
        };

        try
        {
            await _sender.Value.SendMessageAsync(payload, cancellationToken);

            logger.LogInformation(
                "Published SlaBreached for complaint {ComplaintId} (reopen {ReopenCount}) "
                + "to {Queue}.",
                message.ComplaintId,
                message.ReopenCount,
                options.Value.QueueName);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The breach is already committed and is not lost: the marker, the escalation row,
            // the breach list and the dashboard all read from the complaint. What is lost is
            // the notice. Throwing here would abort the rest of the pass over a notification,
            // so this is logged at error and the pass continues.
            //
            // Nothing retries it. That gap is the honest cost of publishing after the commit
            // rather than inside it, and closing it needs a transactional outbox — named as
            // the follow-up rather than half-built.
            logger.LogError(
                exception,
                "Could not publish SlaBreached for complaint {ComplaintId}. The breach is "
                + "recorded; the notification will not be sent and nothing will retry it.",
                message.ComplaintId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_sender.IsValueCreated)
        {
            await _sender.Value.DisposeAsync();
        }
    }
}
