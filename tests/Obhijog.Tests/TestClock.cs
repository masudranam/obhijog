namespace Obhijog.Tests;

/// <summary>
/// A <see cref="TimeProvider"/> the test drives by hand.
///
/// CLAUDE.md non-negotiable 5 exists for this: with <c>TimeProvider</c> injected everywhere,
/// the SLA suites of SPEC.md §18 can put a complaint at 80%, 100% or 150% of its window
/// instantly instead of waiting out real hours. A single <c>DateTimeOffset.UtcNow</c> in the
/// sweeper would make every test in <c>SlaSweepTests</c> impossible to write.
///
/// Hand-rolled rather than <c>Microsoft.Extensions.TimeProvider.Testing</c>: overriding one
/// method is cheaper than a package reference, and the tests need nothing else from it — no
/// timer virtualisation, no scheduling.
/// </summary>
public sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    /// <summary>Moves the clock forward. Time does not run backwards in these tests.</summary>
    public void Advance(TimeSpan by)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(by, TimeSpan.Zero);

        _now += by;
    }

    /// <summary>Places the clock at an exact instant, for walking to a known rung.</summary>
    public void Set(DateTimeOffset instant) => _now = instant;
}
