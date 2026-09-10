using Microsoft.EntityFrameworkCore;
using Obhijog.Api.Auth;
using Obhijog.Domain.Exceptions;
using Obhijog.Domain.Users;
using Obhijog.Infrastructure.Auth;
using Obhijog.Infrastructure.Persistence;

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
    public record DepartmentResponse(Guid Id, string Code, string Name, bool IsActive);

    public record StaffResponse(
        Guid Id,
        string Email,
        string FullName,
        string Role,
        bool IsActive);

    public static RouteGroupBuilder MapReferenceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/departments").WithTags("reference");

        group.MapGet("/", ListDepartmentsAsync).RequireAuthorization();
        group.MapGet("/{id:guid}/staff", ListStaffAsync).RequireAuthorization(Policies.DeptAdmin);

        return group;
    }

    private static async Task<IResult> ListDepartmentsAsync(
        ObhijogDbContext db,
        CancellationToken cancellationToken)
    {
        var departments = await db.Departments
            .AsNoTracking()
            .OrderBy(d => d.Name)
            .Select(d => new DepartmentResponse(d.Id, d.Code, d.Name, d.IsActive))
            .ToListAsync(cancellationToken);

        return Results.Ok(departments);
    }

    /// <summary>
    /// A Dept Admin's own department only.
    ///
    /// Another department's id is <c>404</c>, not <c>403</c> — §9.2's invariant applies to
    /// reference data exactly as it does to complaints, because a <c>403</c> would confirm
    /// the department exists and that the caller simply lacks rights to it.
    /// </summary>
    private static async Task<IResult> ListStaffAsync(
        Guid id,
        ObhijogDbContext db,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        if (currentUser.DepartmentId != id)
        {
            throw new NotFoundException($"Department '{id}' was not found.");
        }

        var staff = await db.Users
            .AsNoTracking()
            .Where(u => u.DepartmentId == id && u.Role != UserRole.Citizen)
            .OrderBy(u => u.FullName)
            .Select(u => new StaffResponse(
                u.Id,
                u.Email ?? string.Empty,
                u.FullName,
                u.Role.ToString(),
                u.IsActive))
            .ToListAsync(cancellationToken);

        return Results.Ok(staff);
    }
}
