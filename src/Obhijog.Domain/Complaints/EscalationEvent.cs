namespace Obhijog.Domain.Complaints;

/// <summary>
/// SPEC.md §8.8.
///
/// The unique index on <c>(ComplaintId, ReopenCount, Level)</c> is the database-level
/// backstop that makes double-escalation impossible even if two sweeps overlap — the
/// sweeper's <c>WHERE</c> clause is the first line of defence, this index is the second
/// (§11.3). <see cref="ReopenCount"/> is in the key because a reopened complaint
/// legitimately re-runs the ladder while its earlier history is preserved.
/// </summary>
public class EscalationEvent
{
    public Guid Id { get; set; }

    public Guid ComplaintId { get; set; }

    public Complaint? Complaint { get; set; }

    public int ReopenCount { get; set; }

    /// <summary>1 or 2. <c>smallint</c> (§8.12).</summary>
    public short Level { get; set; }

    public DateTimeOffset RaisedAt { get; set; }

    /// <summary>Max 200 characters.</summary>
    public required string Reason { get; set; }

    public Guid? NotifiedUserId { get; set; }
}
