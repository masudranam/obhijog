namespace Obhijog.Infrastructure.Messaging;

/// <summary>
/// <c>Sla:Transport = InProcess</c> — the default, and exactly what M7 did before M10 existed.
///
/// The sweeper writes its own notification rows, so there is nothing to publish and
/// <see cref="PublishBreachAsync"/> is never called. It is not a no-op by accident: the
/// contract is that <see cref="WritesNotificationsInline"/> and this method are two halves of
/// one decision, and this implementation answers &quot;I already did it&quot; to the first.
/// </summary>
public sealed class InProcessSlaEventPublisher : ISlaEventPublisher
{
    public bool WritesNotificationsInline => true;

    public Task PublishBreachAsync(
        SlaBreachedMessage message,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
