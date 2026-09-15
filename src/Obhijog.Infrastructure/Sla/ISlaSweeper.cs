namespace Obhijog.Infrastructure.Sla;

/// <summary>
/// One pass of the SLA ladder. SPEC.md §11, F11.
///
/// The hosted service, the manual <c>POST /admin/sla/sweep</c> endpoint and the sweep tests
/// all call <b>this same method</b> (§13.2). M10 moves the *caller* to a Service Bus
/// message; it never moves the logic, which is the whole reason this interface exists
/// rather than the endpoint calling the class directly.
/// </summary>
public interface ISlaSweeper
{
    Task<SlaSweepResult> SweepAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The counters <c>POST /admin/sla/sweep</c> returns (F11), and the ones the single
/// structured log line of §11.6 carries.
/// </summary>
/// <param name="Examined">
/// Distinct complaints the pass acted on across all four phases — not the sum of the phase
/// counts, because one complaint that crosses 80% and 100% in the same pass is one
/// complaint examined and two rungs climbed.
/// </param>
public record SlaSweepResult(
    int Examined,
    int Warned,
    int Breached,
    int EscalatedLevel2,
    int AutoClosed,
    long DurationMs);
