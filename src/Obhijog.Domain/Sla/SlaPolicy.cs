namespace Obhijog.Domain.Sla;

/// <summary>
/// The SLA clock. SPEC.md §11.1.
///
/// A pure function over values — no clock of its own, no I/O — which is the only reason
/// §18's SLA suite is cheap to write. Callers pass the time they already took from an
/// injected <c>TimeProvider</c>.
///
/// M4 needs only <see cref="DueAt"/>, which submission stores on the complaint. The
/// warning, breach and escalation arithmetic arrives with M7 and belongs here beside it.
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
}
