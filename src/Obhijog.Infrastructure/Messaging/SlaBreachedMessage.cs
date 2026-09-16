namespace Obhijog.Infrastructure.Messaging;

/// <summary>
/// The <c>SlaBreached</c> message of SPEC.md §14 F16. Published by the sweeper when
/// <c>Sla:Transport = ServiceBus</c>; consumed by <c>Obhijog.Functions</c>.
///
/// <b>It carries identifiers, not content.</b> The handler re-reads the complaint and
/// re-derives the recipients rather than trusting a subject line that was composed some
/// unknown time ago — a message can sit in a queue, be dead-lettered, and be replayed
/// hours later, by which point the assignee may have changed. The one thing that must
/// travel with the message is <see cref="ReopenCount"/>: it pins which run of the ladder
/// this message belongs to, so a replay after a reopen writes the new cycle&apos;s
/// notification rather than silently colliding with the old one (§11.3 defence 4).
/// </summary>
/// <param name="ComplaintId">The complaint that breached.</param>
/// <param name="ReopenCount">The complaint&apos;s reopen count at the moment of breach.</param>
/// <param name="BreachedAt">When the sweeper recorded the breach, from the injected clock.</param>
public sealed record SlaBreachedMessage(Guid ComplaintId, int ReopenCount, DateTimeOffset BreachedAt)
{
    /// <summary>
    /// The Service Bus <c>MessageId</c>, deterministic so that a broker configured for
    /// duplicate detection would drop an immediate re-publish for free.
    ///
    /// <b>The deployed namespace is Basic tier, which does not offer duplicate detection at
    /// all</b> (infra/main.bicep — Basic is the cheapest that carries a queue, and this is a
    /// portfolio project). So nothing here is deduplicating on it today, and that is fine:
    /// the guarantee was never meant to live in the broker. It is the partial unique index
    /// of §11.3 defence 4, which holds on any tier, and which also covers the case duplicate
    /// detection cannot — a redelivery after the detection window, which is ordinary.
    /// </summary>
    public string MessageId => $"sla-breached:{ComplaintId}:{ReopenCount}";
}
