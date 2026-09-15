using Obhijog.Domain.Sla;
using Xunit;

namespace Obhijog.Tests;

/// <summary>
/// SPEC.md §18's SLA-arithmetic suite. F10.
///
/// <see cref="SlaPolicy"/> is a pure function over values, which is the whole reason this
/// file is cheap: no database, no container, no clock of its own. Everything the ladder
/// decides is decided here, so an error in this file is an error in every rung.
/// </summary>
public class SlaPolicyTests
{
    private static readonly DateTimeOffset Created =
        new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    /// <summary>§11.1: calendar hours from creation. The clock never pauses (D4).</summary>
    [Theory]
    [InlineData(4, "2026-03-01T13:00:00Z")]
    [InlineData(8, "2026-03-01T17:00:00Z")]
    [InlineData(24, "2026-03-02T09:00:00Z")]
    [InlineData(48, "2026-03-03T09:00:00Z")]
    [InlineData(72, "2026-03-04T09:00:00Z")]
    public void DueAtIsCalendarHoursFromCreation(int slaHours, string expected)
    {
        Assert.Equal(
            DateTimeOffset.Parse(expected, null, System.Globalization.DateTimeStyles.RoundtripKind),
            SlaPolicy.DueAt(Created, slaHours));
    }

    /// <summary>
    /// A weekend in the middle changes nothing. This is the test that would fail the day
    /// someone "helpfully" adds a business-hours calendar without amending D4 first.
    /// </summary>
    [Fact]
    public void TheClockRunsThroughTheWeekend()
    {
        // 2026-03-06 is a Friday; 72 calendar hours lands on the Monday, not the Wednesday
        // a five-day working week would give.
        var friday = new DateTimeOffset(2026, 3, 6, 9, 0, 0, TimeSpan.Zero);

        Assert.Equal(
            new DateTimeOffset(2026, 3, 9, 9, 0, 0, TimeSpan.Zero),
            SlaPolicy.DueAt(friday, 72));
    }

    /// <summary>
    /// The three rungs of §11.2 over a 10-hour window: 80% is hour 8, 100% is the due date
    /// exactly, 150% is five hours past it.
    /// </summary>
    [Theory]
    [InlineData(80, 8)]
    [InlineData(100, 10)]
    [InlineData(150, 15)]
    [InlineData(0, 0)]
    public void AtPercentWalksTheWindow(double percent, double expectedHours)
    {
        var dueAt = SlaPolicy.DueAt(Created, 10);

        Assert.Equal(Created.AddHours(expectedHours), SlaPolicy.AtPercent(Created, dueAt, percent));
    }

    /// <summary>100% of the window is <c>SlaDueAt</c> itself — the breach rung needs no arithmetic.</summary>
    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(24)]
    [InlineData(72)]
    public void OneHundredPercentIsExactlyTheDueDate(int slaHours)
    {
        var dueAt = SlaPolicy.DueAt(Created, slaHours);

        Assert.Equal(dueAt, SlaPolicy.AtPercent(Created, dueAt, 100));
    }

    /// <summary>
    /// <c>WarnAt</c> and <c>Level2At</c> are <c>AtPercent</c> under two names, and the
    /// thresholds come from configuration rather than from a literal in the domain.
    /// </summary>
    [Fact]
    public void TheNamedRungsAreTheSameArithmetic()
    {
        var dueAt = SlaPolicy.DueAt(Created, 24);

        Assert.Equal(SlaPolicy.AtPercent(Created, dueAt, 80), SlaPolicy.WarnAt(Created, dueAt, 80));
        Assert.Equal(
            SlaPolicy.AtPercent(Created, dueAt, 150),
            SlaPolicy.Level2At(Created, dueAt, 150));
    }

    /// <summary>
    /// The ladder must be strictly ordered, at every window length, or two rungs fire in the
    /// same pass and the escalation level jumps without a breach ever being recorded.
    /// </summary>
    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(24)]
    [InlineData(48)]
    [InlineData(72)]
    public void TheRungsAreStrictlyOrdered(int slaHours)
    {
        var dueAt = SlaPolicy.DueAt(Created, slaHours);

        Assert.True(SlaPolicy.WarnAt(Created, dueAt, 80) < dueAt);
        Assert.True(dueAt < SlaPolicy.Level2At(Created, dueAt, 150));
    }

    /// <summary>§11.1: <c>(now − createdAt) / (dueAt − createdAt) × 100</c>.</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(5, 50)]
    [InlineData(8, 80)]
    [InlineData(10, 100)]
    [InlineData(15, 150)]
    [InlineData(20, 200)]
    public void ElapsedPercentIsTheWindowPosition(double hoursSinceCreation, double expected)
    {
        var dueAt = SlaPolicy.DueAt(Created, 10);

        Assert.Equal(
            expected,
            SlaPolicy.ElapsedPercent(Created, dueAt, Created.AddHours(hoursSinceCreation)),
            precision: 6);
    }

    /// <summary>
    /// Unbounded above, deliberately. A complaint a week into a four-hour window is at
    /// 4200%, and the breach list's "how badly" depends on that not being clamped to 100.
    /// </summary>
    [Fact]
    public void ElapsedPercentIsNotClamped()
    {
        var dueAt = SlaPolicy.DueAt(Created, 4);

        Assert.Equal(4200, SlaPolicy.ElapsedPercent(Created, dueAt, Created.AddDays(7)), precision: 6);
    }

    /// <summary>
    /// A zero-length window has no percentage to compute. Reporting 0 before and 100 after
    /// keeps the rung comparisons meaningful; returning infinity or throwing would take the
    /// whole sweep down over one misconfigured category.
    /// </summary>
    [Fact]
    public void AZeroLengthWindowDoesNotDivideByZero()
    {
        var dueAt = SlaPolicy.DueAt(Created, 0);

        Assert.Equal(0, SlaPolicy.ElapsedPercent(Created, dueAt, Created.AddSeconds(-1)));
        Assert.Equal(100, SlaPolicy.ElapsedPercent(Created, dueAt, Created));
        Assert.Equal(100, SlaPolicy.ElapsedPercent(Created, dueAt, Created.AddHours(1)));
    }

    /// <summary>
    /// The two halves must agree: the instant <c>AtPercent</c> names is the instant
    /// <c>ElapsedPercent</c> reports that percentage for. The sweeper selects with one and
    /// reports with the other, so a disagreement would show up as a complaint warned at 79%.
    /// </summary>
    [Theory]
    [InlineData(80)]
    [InlineData(100)]
    [InlineData(150)]
    [InlineData(37)]
    public void AtPercentAndElapsedPercentAreInverses(double percent)
    {
        var dueAt = SlaPolicy.DueAt(Created, 13);

        var instant = SlaPolicy.AtPercent(Created, dueAt, percent);

        Assert.Equal(percent, SlaPolicy.ElapsedPercent(Created, dueAt, instant), precision: 6);
    }

    /// <summary>
    /// The policy takes the time it is given and has no clock of its own — CLAUDE.md
    /// non-negotiable 5. Driving it from a <see cref="TestClock"/> is what makes
    /// every rung above testable at all, so it is asserted rather than assumed.
    /// </summary>
    [Fact]
    public void TheCallerSuppliesTheClock()
    {
        var clock = new TestClock(Created);
        var dueAt = SlaPolicy.DueAt(Created, 10);

        Assert.Equal(0, SlaPolicy.ElapsedPercent(Created, dueAt, clock.GetUtcNow()));

        clock.Advance(TimeSpan.FromHours(8));

        Assert.Equal(80, SlaPolicy.ElapsedPercent(Created, dueAt, clock.GetUtcNow()), precision: 6);
    }
}
