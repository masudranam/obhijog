using System.ComponentModel.DataAnnotations;

namespace Obhijog.Infrastructure.Options;

/// <summary>SPEC.md §19, §11. The whole SLA engine's configuration, in one place.</summary>
public class SlaOptions
{
    public const string SectionName = "Sla";

    /// <summary>
    /// How often <c>SlaSweepService</c> runs. <b>Zero disables the hosted service</b> — the
    /// tests and the manual <c>POST /admin/sla/sweep</c> endpoint still work, so a disabled
    /// sweeper is a scheduling decision and never a missing feature (§19).
    /// </summary>
    [Range(0, 86_400)]
    public int SweepIntervalSeconds { get; set; } = 60;

    /// <summary>The warning rung of §11.2. Percent of the window, so below 100.</summary>
    [Range(1, 99)]
    public int WarningThresholdPercent { get; set; } = 80;

    /// <summary>
    /// The second escalation rung of §11.2. Above 100 by definition — level 2 is a
    /// complaint that has been overdue for half its window again, so a value at or below
    /// the breach point would make the two rungs fire together and is refused at startup.
    /// </summary>
    [Range(101, 1000)]
    public int EscalationLevel2Percent { get; set; } = 150;

    /// <summary>§11.5. A complaint <c>Resolved</c> longer than this is closed by the sweeper.</summary>
    [Range(1, 365)]
    public int AutoCloseAfterDays { get; set; } = 7;

    /// <summary>
    /// Rows per phase per pass (§11.6). The cap exists so one pass cannot hold a
    /// transaction open over an unbounded backlog; hitting it is logged so the backlog is
    /// visible rather than silent.
    /// </summary>
    [Range(1, 10_000)]
    public int SweepBatchSize { get; set; } = 200;

    /// <summary>
    /// <c>InProcess</c> today. M10 swaps the *caller* to a Service Bus message without
    /// touching the sweep logic (§13.2, F16), which is the point of naming it here now.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string Transport { get; set; } = "InProcess";
}

/// <summary>SPEC.md §19. Delivery is a separate concern from writing the notification row.</summary>
public class NotificationOptions
{
    public const string SectionName = "Notifications";

    /// <summary><c>Log</c> is the MVP of F12; <c>Email</c> is a later swap.</summary>
    [Required(AllowEmptyStrings = false)]
    public string Delivery { get; set; } = "Log";
}
