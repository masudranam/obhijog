using Microsoft.EntityFrameworkCore;
using Obhijog.Domain.Complaints;
using Obhijog.Infrastructure.Auth;
using Obhijog.Infrastructure.Complaints;
using Obhijog.Infrastructure.Persistence;

namespace Obhijog.Infrastructure.Dashboard;

/// <summary>
/// `GET /dashboard/summary`. SPEC.md §13.2, §14 F13.
///
/// Role-scoped through <c>ComplaintQueryScope.For</c> like every other complaint read, which
/// is the whole of the role handling: a Citizen gets the same shape computed over their own
/// complaints, a Staff member over their department's. There is no separate citizen
/// dashboard and no `if (role == …)` in this file (§9.3).
///
/// <b>Nothing here recomputes an SLA threshold.</b> `warningOpen` and `breachedOpen` read the
/// markers the sweeper wrote, exactly as the list endpoint's `slaState` filter does. The
/// 80/100/150 rungs of §11.2 are the sweeper's business, and a dashboard that worked out its
/// own 80% would be a second copy that drifts — and would disagree with the list the moment
/// a sweep was late.
/// </summary>
public class DashboardService(
    ObhijogDbContext db,
    ICurrentUser currentUser,
    TimeProvider timeProvider)
{
    /// <summary>§11.2's "still open", and the definition `totalOpen` uses.</summary>
    private static readonly ComplaintStatus[] OpenStatuses =
    [
        ComplaintStatus.New,
        ComplaintStatus.Assigned,
        ComplaintStatus.InProgress,
    ];

    public async Task<DashboardSummary> GetAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var next24h = now.AddHours(24);
        var thirtyDaysAgo = now.AddDays(-30);

        var scoped = ComplaintQueryScope.For(db.Complaints.AsNoTracking(), currentUser);

        // One round trip for every count. GroupBy over a constant is the EF idiom for
        // "aggregate the whole set", and each Count(predicate) becomes a COUNT(...) FILTER
        // in one SELECT rather than a dozen separate queries.
        var counts = await scoped
            .GroupBy(_ => 1)
            .Select(g => new
            {
                TotalOpen = g.Count(c => OpenStatuses.Contains(c.Status)),

                New = g.Count(c => c.Status == ComplaintStatus.New),
                Assigned = g.Count(c => c.Status == ComplaintStatus.Assigned),
                InProgress = g.Count(c => c.Status == ComplaintStatus.InProgress),
                Resolved = g.Count(c => c.Status == ComplaintStatus.Resolved),
                Closed = g.Count(c => c.Status == ComplaintStatus.Closed),
                Rejected = g.Count(c => c.Status == ComplaintStatus.Rejected),

                // Warned but not yet breached — the 80-100% band, read from the markers
                // rather than recomputed. Same predicate as slaState=warning on the list.
                WarningOpen = g.Count(c =>
                    OpenStatuses.Contains(c.Status)
                    && c.SlaWarnedAt != null
                    && c.SlaBreachedAt == null),

                BreachedOpen = g.Count(c =>
                    OpenStatuses.Contains(c.Status) && c.SlaBreachedAt != null),

                // The window ahead, not everything already overdue: a complaint whose
                // deadline passed last week is in breachedOpen and would double-count here
                // as "due soon", which is the opposite of what the number is for.
                DueNext24h = g.Count(c =>
                    OpenStatuses.Contains(c.Status)
                    && c.SlaDueAt >= now
                    && c.SlaDueAt <= next24h),

                EscalatedLevel1 = g.Count(c =>
                    OpenStatuses.Contains(c.Status) && c.EscalationLevel == 1),

                EscalatedLevel2 = g.Count(c =>
                    OpenStatuses.Contains(c.Status) && c.EscalationLevel == 2),
            })
            .SingleOrDefaultAsync(cancellationToken);

        // GroupBy yields no row at all for an empty set, which is not the same as zero. A
        // caller with no complaints gets a dashboard of zeros, never a 500.
        var byStatus = new Dictionary<ComplaintStatus, int>
        {
            [ComplaintStatus.New] = counts?.New ?? 0,
            [ComplaintStatus.Assigned] = counts?.Assigned ?? 0,
            [ComplaintStatus.InProgress] = counts?.InProgress ?? 0,
            [ComplaintStatus.Resolved] = counts?.Resolved ?? 0,
            [ComplaintStatus.Closed] = counts?.Closed ?? 0,
            [ComplaintStatus.Rejected] = counts?.Rejected ?? 0,
        };

        var resolution = await ResolutionAsync(scoped, thirtyDaysAgo, cancellationToken);

        return new DashboardSummary(
            counts?.TotalOpen ?? 0,
            byStatus,
            counts?.WarningOpen ?? 0,
            counts?.BreachedOpen ?? 0,
            counts?.DueNext24h ?? 0,
            counts?.EscalatedLevel1 ?? 0,
            counts?.EscalatedLevel2 ?? 0,
            resolution.Count,
            resolution.AverageHours,
            resolution.CompliancePercent);
    }

    /// <summary>
    /// The three figures over complaints resolved in the last 30 days: how many,
    /// how long they took on average, and what share met their deadline.
    ///
    /// §11.1: the SLA is measured to <c>ResolvedAt</c>, not <c>ClosedAt</c>. A complaint
    /// resolved inside its window and closed a week later met its SLA, so `Closed`
    /// complaints are counted here through their <c>ResolvedAt</c> and never excluded by
    /// status.
    ///
    /// The average is computed over a projection of two timestamps rather than in SQL.
    /// PostgreSQL will happily average an interval, but <c>TimeSpan.TotalHours</c> has no
    /// dependable Npgsql translation, and a silent client-side evaluation of the whole
    /// <c>complaints</c> table is the failure this codebase has already been bitten by once
    /// (see <c>SlaSweeper.SelectAtOrPastPercentAsync</c>). Pulling two timestamps per row is
    /// an explicit, bounded read: the caller's own scope, narrowed to a 30-day window.
    /// </summary>
    private static async Task<(int Count, double? AverageHours, double? CompliancePercent)>
        ResolutionAsync(
            IQueryable<Complaint> scoped,
            DateTimeOffset since,
            CancellationToken cancellationToken)
    {
        var resolved = await scoped
            .Where(c => c.ResolvedAt != null && c.ResolvedAt >= since)
            .Select(c => new
            {
                ResolvedAt = c.ResolvedAt!.Value,
                c.CreatedAt,
                c.SlaDueAt,
            })
            .ToListAsync(cancellationToken);

        if (resolved.Count == 0)
        {
            // Null, not zero. "No complaints were resolved" and "every complaint took no
            // time and none met its SLA" are different statements, and a dashboard showing
            // 0% compliance for an idle month would be read as a catastrophe.
            return (0, null, null);
        }

        var averageHours = resolved.Average(r => (r.ResolvedAt - r.CreatedAt).TotalHours);
        var met = resolved.Count(r => r.ResolvedAt <= r.SlaDueAt);

        return (
            resolved.Count,
            Math.Round(averageHours, 1),
            Math.Round(met * 100d / resolved.Count, 1));
    }
}

/// <summary>
/// SPEC.md §14 F13's table, field for field.
/// </summary>
/// <param name="ByStatus">
/// All six statuses, always present — a status with no complaints is a zero rather than an
/// absent key, so the client renders a stable set of rows instead of a shifting one.
/// </param>
/// <param name="AvgResolutionHours">
/// Null when nothing was resolved in the window. See <c>ResolutionAsync</c> for why that is
/// not zero.
/// </param>
/// <param name="SlaCompliancePct">
/// The share of complaints resolved in the window that met their deadline, measured to
/// <c>ResolvedAt</c> (§11.1). Null when the window is empty.
/// </param>
public record DashboardSummary(
    int TotalOpen,
    IReadOnlyDictionary<ComplaintStatus, int> ByStatus,
    int WarningOpen,
    int BreachedOpen,
    int DueNext24h,
    int EscalatedLevel1,
    int EscalatedLevel2,
    int ResolvedLast30Days,
    double? AvgResolutionHours,
    double? SlaCompliancePct);
