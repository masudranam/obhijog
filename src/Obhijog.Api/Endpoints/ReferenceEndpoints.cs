using Obhijog.Api.Auth;
using Obhijog.Infrastructure.Reference;

namespace Obhijog.Api.Endpoints;

/// <summary>
/// Reference data. SPEC.md §13.2.
///
/// M3 ships the two department reads because the §20 Definition of Done requires proving
/// that a Staff token on a DeptAdmin endpoint returns <c>403</c> — which needs a DeptAdmin
/// endpoint to exist. Categories and user management arrive with the milestones that use
/// them.
/// </summary>
public static class ReferenceEndpoints
{
    public static RouteGroupBuilder MapReferenceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/departments").WithTags("reference");

        group.MapGet("/", ListDepartmentsAsync).RequireAuthorization();
        group.MapGet("/{id:guid}/staff", ListStaffAsync).RequireAuthorization(Policies.DeptAdmin);

        return group;
    }

    private static async Task<IResult> ListDepartmentsAsync(
        ReferenceService reference,
        CancellationToken cancellationToken) =>
        Results.Ok(await reference.ListDepartmentsAsync(cancellationToken));

    /// <summary>
    /// A Dept Admin's own department only. The service throws <c>NotFoundException</c> for
    /// any other, which the one exception handler maps to <c>404</c> — §9.2. The endpoint
    /// does not get to choose between <c>404</c> and <c>403</c>.
    /// </summary>
    private static async Task<IResult> ListStaffAsync(
        Guid id,
        ReferenceService reference,
        CancellationToken cancellationToken) =>
        Results.Ok(await reference.ListStaffAsync(id, cancellationToken));
}
