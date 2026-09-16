namespace Obhijog.Infrastructure.Messaging;

/// <summary>
/// Where a breach goes once the sweeper has recorded it. SPEC.md §14 F16.
///
/// Two implementations, chosen by <c>Sla:Transport</c>: <see cref="InProcessSlaEventPublisher"/>
/// (the default, and what M7 always did) and <c>ServiceBusSlaEventPublisher</c>. Detection
/// does not move between them — only delivery does, which is the whole point of D1&apos;s seam.
/// </summary>
public interface ISlaEventPublisher
{
    /// <summary>
    /// True when the sweeper should write the breach notification rows itself, inside its
    /// own transaction. False when this publisher will hand the work to something else,
    /// in which case the sweeper writes the escalation and the marker and stops there.
    /// </summary>
    bool WritesNotificationsInline { get; }

    /// <summary>
    /// Called <b>after</b> the sweeper&apos;s per-complaint transaction has committed, never
    /// inside it. Publishing inside would let a rolled-back transaction emit a message for a
    /// breach that never happened, and the handler would write notifications for a complaint
    /// that is not breached — a phantom the unique index cannot catch, because there is no
    /// competing row.
    ///
    /// The cost of that ordering is stated rather than hidden: if the process dies between
    /// the commit and this call, the message is lost and no notification is ever written.
    /// The breach itself is durable — the marker, the escalation row, the breach list and the
    /// dashboard all come from the complaint — so the failure loses a notice, not a record.
    /// Closing it properly needs a transactional outbox, which is named here as the follow-up
    /// rather than half-built.
    /// </summary>
    Task PublishBreachAsync(SlaBreachedMessage message, CancellationToken cancellationToken = default);
}
