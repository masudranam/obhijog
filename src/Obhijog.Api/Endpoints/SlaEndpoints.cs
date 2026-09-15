using Obhijog.Api.Auth;
using Obhijog.Infrastructure.Sla;

namespace Obhijog.Api.Endpoints;

/// <summary>
/// The SLA engine's two routes. SPEC.md §13.2, F11, F12.
///
/// Bind, call one service, map a status code. The sweep endpoint in particular holds no
/// logic of its own: it calls <see cref="ISlaSweeper.SweepAsync"/>, the same method the
/// hosted service and the tests call, and returns its counters.
/// </summary>
public static class SlaEndpoints
{
    public static IEndpointRouteBuilder MapSlaEndpoints(this IEndpointRouteBuilder app)
    {
        // A department's own overdue work, scoped in the service like every other complaint
        // read (§9.3). DeptAdmin: this is the escalation list, and escalation is theirs.
        app.MapGroup("/sla")
            .WithTags("sla")
            .MapGet("/breaches", ListBreachesAsync)
            .RequireAuthorization(Policies.DeptAdmin);

        // Manual trigger. DeptAdmin-only because a sweep writes — it is the same pass the
        // background service runs, not a read-only preview of one.
        app.MapGroup("/admin/sla")
            .WithTags("sla")
            .MapPost("/sweep", SweepAsync)
            .RequireAuthorization(Policies.DeptAdmin);

        return app;
    }

    private static async Task<IResult> ListBreachesAsync(
        SlaService sla,
        CancellationToken cancellationToken) =>
        Results.Ok(await sla.ListBreachesAsync(cancellationToken));

    private static async Task<IResult> SweepAsync(
        ISlaSweeper sweeper,
        CancellationToken cancellationToken) =>
        Results.Ok(await sweeper.SweepAsync(cancellationToken));
}
