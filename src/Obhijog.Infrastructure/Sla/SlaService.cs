using Microsoft.EntityFrameworkCore;
using Obhijog.Domain.Complaints;
using Obhijog.Infrastructure.Auth;
using Obhijog.Infrastructure.Complaints;
using Obhijog.Infrastructure.Persistence;

namespace Obhijog.Infrastructure.Sla;

/// <summary>
/// The read side of the SLA engine. <c>GET /sla/breaches</c>, SPEC.md §13.2, F12.
///
/// Scoped through <c>ComplaintQueryScope.For</c> like every other complaint read — the
/// breach list is not an ops back door, it is a Dept Admin's own department's overdue work
/// (§9.3). The unfiltered view of the whole municipality does not exist, here or anywhere.
/// </summary>
public class SlaService(ObhijogDbContext db, ICurrentUser currentUser, TimeProvider timeProvider)
{
    /// <summary>
    /// Breached complaints still open, worst first.
    ///
    /// "Still open" is the sweep's own definition (§11.2): a complaint resolved after it
    /// breached keeps its <c>SlaBreachedAt</c> for reporting, but it is no longer work
    /// anyone has to chase, so it does not belong on this list.
    /// </summary>
    public async Task<IReadOnlyList<SlaBreach>> ListBreachesAsync(
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();

        var rows = await ComplaintQueryScope
            .For(db.Complaints, currentUser)
            .AsNoTracking()
            .Where(c => c.SlaBreachedAt != null
                && (c.Status == ComplaintStatus.New
                    || c.Status == ComplaintStatus.Assigned
                    || c.Status == ComplaintStatus.InProgress))

            // Worst first: the deepest escalation, then the longest overdue.
            .OrderByDescending(c => c.EscalationLevel)
            .ThenBy(c => c.SlaDueAt)
            .Select(c => new
            {
                c.Id,
                c.ReferenceNumber,
                c.Title,
                CategoryName = c.Category!.Name,
                c.Status,
                c.Priority,

                // No navigation property to User exists — Complaint lives in Domain and
                // User does not (D13) — so the name comes from a correlated subquery
                // rather than an Include.
                AssignedStaffName = db.Users
                    .Where(u => u.Id == c.AssignedStaffId)
                    .Select(u => u.FullName)
                    .FirstOrDefault(),
                c.SlaDueAt,
                c.SlaBreachedAt,
                c.EscalationLevel,
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(r => new SlaBreach(
                r.Id,
                r.ReferenceNumber,
                r.Title,
                r.CategoryName,
                r.Status,
                r.Priority,
                r.AssignedStaffName,
                r.SlaDueAt,
                r.SlaBreachedAt!.Value,
                r.EscalationLevel,

                // Display arithmetic over a server clock, computed here rather than in the
                // client so "overdue by" means the same thing in the CSV, the dashboard and
                // the browser. Rounded to a tenth — an hours figure to five decimal places
                // reads as precision nobody has.
                Math.Round((now - r.SlaDueAt).TotalHours, 1)))
            .ToList();
    }
}

/// <summary>
/// One row of <c>GET /sla/breaches</c>. F12: the complaint, its escalation level and how
/// far past its deadline it is.
/// </summary>
public record SlaBreach(
    Guid Id,
    string ReferenceNumber,
    string Title,
    string CategoryName,
    ComplaintStatus Status,
    ComplaintPriority Priority,
    string? AssignedStaffName,
    DateTimeOffset SlaDueAt,
    DateTimeOffset SlaBreachedAt,
    short EscalationLevel,
    double HoursOverdue);
