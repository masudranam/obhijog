using System.Text;
using Obhijog.Api.Auth;
using Obhijog.Infrastructure.Complaints;
using Obhijog.Infrastructure.Dashboard;
using Obhijog.Infrastructure.Export;

namespace Obhijog.Api.Endpoints;

/// <summary>
/// The dashboard and the CSV export. SPEC.md §13.2, F13, F14.
///
/// Both are reads, and both are scoped in their service rather than here — an endpoint binds,
/// calls one service and maps a status code (§16.1).
/// </summary>
public static class DashboardEndpoints
{
    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        // Any authenticated role. What differs between them is the scope the service
        // applies, not the route they may reach: a Citizen gets the same shape over their
        // own complaints (F13).
        app.MapGroup("/dashboard")
            .WithTags("dashboard")
            .MapGet("/summary", SummaryAsync)
            .RequireAuthorization();

        return app;
    }

    /// <summary>
    /// Registered on the complaints group rather than its own, because the route is
    /// `/complaints/export` and it takes the list's query string verbatim.
    /// </summary>
    public static RouteGroupBuilder MapExportEndpoint(this RouteGroupBuilder complaints)
    {
        // DeptAdmin (§13.2). Narrower than the list it shares a query with: a whole
        // department in one file is a different disclosure from a page of twenty on screen,
        // and the scope code cannot express "the same rows but fewer of them at once".
        complaints.MapGet("/export", ExportAsync).RequireAuthorization(Policies.DeptAdmin);

        return complaints;
    }

    private static async Task<IResult> SummaryAsync(
        DashboardService dashboard,
        CancellationToken cancellationToken) =>
        Results.Ok(await dashboard.GetAsync(cancellationToken));

    /// <summary>
    /// The same `[AsParameters]` binding the list uses, so the two cannot drift in what they
    /// accept any more than they can in what they return.
    /// </summary>
    private static async Task<IResult> ExportAsync(
        [AsParameters] ComplaintListQueryBinding query,
        ComplaintExportService export,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var csv = await export.WriteAsync(query.ToQuery(), cancellationToken);

        // UTF-8 with a BOM. Without it Excel on Windows reads the file as the system code
        // page and mangles every non-ASCII character — which in this application means
        // every Bangla name and address. The BOM is the one thing that makes the export
        // openable by the people who would open it.
        var bytes = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(csv))
            .ToArray();

        return Results.File(
            bytes,
            "text/csv; charset=utf-8",
            ComplaintExportService.FileName(timeProvider.GetUtcNow()));
    }
}
