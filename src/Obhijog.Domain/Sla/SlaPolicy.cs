namespace Obhijog.Domain.Sla;

/// <summary>
/// The SLA clock. SPEC.md §11.1.
///
/// A pure function over values — no clock of its own, no I/O — which is the only reason
/// §18's SLA suite is cheap to write. Callers pass the time they already took from an
/// injected <c>TimeProvider</c>, and the thresholds they already read from
/// <c>Sla:WarningThresholdPercent</c> and <c>Sla:EscalationLevel2Percent</c>. Nothing here
/// knows that those default to 80 and 150 — a literal threshold in this file would be a
/// second copy of configuration.
/// </summary>
public static class SlaPolicy
{
    /// <summary>
    /// Calendar hours from creation. The clock never pauses — there is no business-hours
    /// calendar, no holiday table and no <c>OnHold</c> status. That is decision D4, a
    /// deliberate simplification rather than a gap to fill in later (§11.1).
    /// </summary>
    public static DateTimeOffset DueAt(DateTimeOffset createdAt, int slaHours) =>
        createdAt.AddHours(slaHours);

    /// <summary>
    /// The instant a complaint reaches <paramref name="percent"/> of its window, where the
    /// window is <c>dueAt − createdAt</c> and 100% is <paramref name="dueAt"/> exactly.
    ///
    /// The whole ladder of §11.2 is this one function at 80, 100 and 150 — which is why the
    /// sweeper has no arithmetic of its own to get wrong.
    /// </summary>
    public static DateTimeOffset AtPercent(
        DateTimeOffset createdAt,
        DateTimeOffset dueAt,
        double percent) =>
        createdAt + (dueAt - createdAt) * (percent / 100d);

    /// <summary>The warning rung. <c>Sla:WarningThresholdPercent</c>, 80 by default.</summary>
    public static DateTimeOffset WarnAt(
        DateTimeOffset createdAt,
        DateTimeOffset dueAt,
        double warningPercent) =>
        AtPercent(createdAt, dueAt, warningPercent);

    /// <summary>
    /// The second escalation rung. <c>Sla:EscalationLevel2Percent</c>, 150 by default —
    /// past the due date, which is why this one is always later than <paramref name="dueAt"/>
    /// for any sane configuration.
    /// </summary>
    public static DateTimeOffset Level2At(
        DateTimeOffset createdAt,
        DateTimeOffset dueAt,
        double level2Percent) =>
        AtPercent(createdAt, dueAt, level2Percent);

    /// <summary>
    /// How far through its window a complaint is, as a percentage. §11.1:
    /// <c>(now − createdAt) / (dueAt − createdAt) × 100</c>.
    ///
    /// Unbounded above: an hour into a one-hour window is 100, a day into it is 2400, and
    /// the caller needs that to distinguish "just breached" from "badly breached". It can
    /// go below zero for a clock skewed behind creation, which is likewise reported rather
    /// than clamped — a negative here means something is wrong and hiding it would not help.
    ///
    /// A zero-length window (a category with <c>SlaHours = 0</c>) has no percentage to
    /// compute, so it reports 100 from the moment it is due and 0 before — the answer that
    /// keeps the ladder's comparisons meaningful rather than returning infinity or throwing
    /// inside a sweep.
    /// </summary>
    public static double ElapsedPercent(
        DateTimeOffset createdAt,
        DateTimeOffset dueAt,
        DateTimeOffset now)
    {
        var window = dueAt - createdAt;

        if (window <= TimeSpan.Zero)
        {
            return now >= dueAt ? 100d : 0d;
        }

        return (now - createdAt) / window * 100d;
    }
}
