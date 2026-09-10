using Obhijog.Domain.Complaints;

namespace Obhijog.Domain.Departments;

/// <summary>
/// SPEC.md §8.3 — the routing table and the SLA policy in one row, which is the
/// design's main lever.
///
/// Changing <see cref="SlaHours"/> does **not** retroactively move existing deadlines:
/// <c>Complaint.SlaDueAt</c> is stored per complaint at creation, and only a
/// recategorize recomputes it (§11.4).
/// </summary>
public class ComplaintCategory
{
    /// <summary>Upper bound on <see cref="SlaHours"/> — one year (§8.3).</summary>
    public const int MaxSlaHours = 8760;

    public Guid Id { get; set; }

    /// <summary>Unique. Max 80 characters.</summary>
    public required string Name { get; set; }

    /// <summary>Routing target.</summary>
    public Guid DepartmentId { get; set; }

    public Department? Department { get; set; }

    /// <summary>The SLA window. Greater than zero, at most <see cref="MaxSlaHours"/>.</summary>
    public int SlaHours { get; set; }

    public ComplaintPriority DefaultPriority { get; set; }

    /// <summary>
    /// An inactive category cannot receive **new** complaints; existing ones keep working.
    /// </summary>
    public bool IsActive { get; set; } = true;
}
