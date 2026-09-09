using Microsoft.AspNetCore.Diagnostics.HealthChecks;

namespace Obhijog.Api.Endpoints;

/// <summary>
/// Liveness and readiness probes. These sit at the root rather than under
/// <c>/api/v1</c> — an orchestrator probing a versioned path would have to be
/// reconfigured the day the API version changes.
/// </summary>
public static class HealthEndpoints
{
    /// <summary>Tag marking the checks that gate readiness.</summary>
    public const string ReadyTag = "ready";

    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        // Liveness: the process is running. It deliberately runs no checks. A liveness
        // probe wired to the database gets the container killed during a database blip,
        // which a restart cannot fix and which removes capacity exactly when it is scarce.
        app.MapHealthChecks("/health", new HealthCheckOptions
        {
            Predicate = _ => false,
        });

        // Readiness: the database and the blob container are both reachable. SPEC.md §14 F1.
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains(ReadyTag),
        });

        return app;
    }
}
