using System.Text;
using Microsoft.EntityFrameworkCore;
using Obhijog.Infrastructure.Complaints;
using Obhijog.Infrastructure.Persistence;

namespace Obhijog.Infrastructure.Export;

/// <summary>
/// `GET /complaints/export`. SPEC.md §13.2, §14 F14.
///
/// <b>It does not have a query of its own.</b> Scope and filters come from
/// <c>ComplaintService.ScopedAndFiltered</c> and the order from <c>ComplaintService.Sorted</c>
/// — the same two calls the list endpoint makes. F14 says a divergence between the export
/// and the list is a bug rather than a feature, and the way to honour that is to leave the
/// export no way to diverge: it cannot filter differently because it does not know how to
/// filter at all.
///
/// The consequence worth stating: a Citizen exporting gets their own complaints, a Staff
/// member their department's, because the scope is the list's scope (§9.3). There is no
/// "export everything" path, here or anywhere.
/// </summary>
public class ComplaintExportService(ObhijogDbContext db, ComplaintService complaints)
{
    /// <summary>
    /// The columns F14 names, in the order it names them.
    ///
    /// Not run through <c>Csv.Field</c> at construction: these are literals in this file,
    /// not user input, and quoting them here would make the header row look like it needed
    /// the same defence the data rows do. <see cref="WriteAsync"/> quotes them on the way out.
    /// </summary>
    private static readonly string[] Columns =
    [
        "reference",
        "title",
        "category",
        "department",
        "status",
        "priority",
        "assignee",
        "created",
        "due",
        "resolved",
        "breached",
        "escalation level",
    ];

    /// <summary>
    /// Renders the caller's filtered complaints as CSV.
    ///
    /// Returns a string rather than streaming to the response. The export is bounded by the
    /// caller's own scope — one department at most — and a string is honest about the fact
    /// that this is not built for a million rows. Streaming is the change to make when that
    /// stops being true, and it is a change to this method alone.
    /// </summary>
    public async Task<string> WriteAsync(
        ComplaintListQuery query,
        CancellationToken cancellationToken = default)
    {
        var rows = await ComplaintService
            .Sorted(complaints.ScopedAndFiltered(query), query.Sort)
            .Select(c => new
            {
                c.ReferenceNumber,
                c.Title,
                CategoryName = c.Category!.Name,
                DepartmentName = c.Department!.Name,
                c.Status,
                c.Priority,

                // A subquery, not a navigation: Complaint lives in Domain and User does not
                // (D13). The list projection does the same thing for the same reason.
                AssignedStaffName = db.Users
                    .Where(u => u.Id == c.AssignedStaffId)
                    .Select(u => u.FullName)
                    .FirstOrDefault(),
                c.CreatedAt,
                c.SlaDueAt,
                c.ResolvedAt,
                c.SlaBreachedAt,
                c.EscalationLevel,
            })
            .ToListAsync(cancellationToken);

        var builder = new StringBuilder();

        builder.Append(Csv.Row(Columns)).Append("\r\n");

        foreach (var row in rows)
        {
            builder
                .Append(Csv.Row(
                    row.ReferenceNumber,
                    row.Title,
                    row.CategoryName,
                    row.DepartmentName,
                    row.Status.ToString(),
                    row.Priority.ToString(),
                    row.AssignedStaffName,
                    Csv.Instant(row.CreatedAt),
                    Csv.Instant(row.SlaDueAt),
                    Csv.Instant(row.ResolvedAt),
                    Csv.Instant(row.SlaBreachedAt),
                    Csv.Number(row.EscalationLevel)))

                // CRLF, which RFC 4180 specifies and Excel is happiest with. A bare LF is
                // read as a line break by most tools and as part of the field by some.
                .Append("\r\n");
        }

        return builder.ToString();
    }

    /// <summary>
    /// The <c>Content-Disposition</c> filename. Dated so a folder of exports sorts and reads
    /// sensibly, and fixed-format so it carries no caller-supplied text into a header.
    /// </summary>
    public static string FileName(DateTimeOffset now) =>
        $"complaints-{now.UtcDateTime:yyyy-MM-dd}.csv";
}
