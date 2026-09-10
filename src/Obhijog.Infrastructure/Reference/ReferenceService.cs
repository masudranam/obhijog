using Microsoft.EntityFrameworkCore;
using Obhijog.Domain.Exceptions;
using Obhijog.Domain.Users;
using Obhijog.Infrastructure.Auth;
using Obhijog.Infrastructure.Persistence;

namespace Obhijog.Infrastructure.Reference;

/// <summary>
/// Reference-data reads. SPEC.md §13.2, §16.2.
///
/// The EF queries live here rather than in the endpoint file: §16.1 allows an endpoint to
/// bind, call one service and map a status code, and nothing else.
/// </summary>
public class ReferenceService(ObhijogDbContext db, ICurrentUser currentUser)
{
    /// <summary>
    /// §13.2. Active categories only unless a Dept Admin asks otherwise — an inactive
    /// category cannot receive new complaints (§8.3), so offering one on the submit form
    /// would produce a guaranteed 400.
    /// </summary>
    public async Task<IReadOnlyList<CategoryDto>> ListCategoriesAsync(
        bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        var query = db.ComplaintCategories.AsNoTracking();

        // `includeInactive` is a Dept Admin affordance; anyone else asking gets the active
        // list regardless, rather than a 403 for a parameter they may simply have copied.
        if (!includeInactive || currentUser.Role != UserRole.DeptAdmin)
        {
            query = query.Where(c => c.IsActive);
        }

        return await query
            .OrderBy(c => c.Name)
            .Select(c => new CategoryDto(
                c.Id,
                c.Name,
                c.DepartmentId,
                c.Department!.Name,
                c.SlaHours,
                c.DefaultPriority.ToString(),
                c.IsActive))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DepartmentDto>> ListDepartmentsAsync(
        CancellationToken cancellationToken = default) =>
        await db.Departments
            .AsNoTracking()
            .OrderBy(d => d.Name)
            .Select(d => new DepartmentDto(d.Id, d.Code, d.Name, d.IsActive))
            .ToListAsync(cancellationToken);

    /// <summary>
    /// The caller's own department only.
    ///
    /// Another department's id throws <see cref="NotFoundException"/>, so it surfaces as
    /// <c>404</c> and not <c>403</c>. §9.2's invariant is a domain decision and applies to
    /// reference data exactly as it does to complaints: a <c>403</c> would confirm the
    /// department exists and that the caller merely lacks rights to it. The endpoint does
    /// not get to choose which status this is.
    /// </summary>
    public async Task<IReadOnlyList<StaffDto>> ListStaffAsync(
        Guid departmentId,
        CancellationToken cancellationToken = default)
    {
        if (currentUser.DepartmentId != departmentId)
        {
            throw new NotFoundException($"Department '{departmentId}' was not found.");
        }

        return await db.Users
            .AsNoTracking()
            .Where(u => u.DepartmentId == departmentId && u.Role != UserRole.Citizen)
            .OrderBy(u => u.FullName)
            .Select(u => new StaffDto(
                u.Id,
                u.Email ?? string.Empty,
                u.FullName,
                u.Role.ToString(),
                u.IsActive))
            .ToListAsync(cancellationToken);
    }
}

public record DepartmentDto(Guid Id, string Code, string Name, bool IsActive);

/// <summary>
/// Carries <c>SlaHours</c> so the submit form can tell a citizen what response time they
/// are about to be promised. That is display of a server-side value, not the client
/// re-deriving policy.
/// </summary>
public record CategoryDto(
    Guid Id,
    string Name,
    Guid DepartmentId,
    string DepartmentName,
    int SlaHours,
    string DefaultPriority,
    bool IsActive);

public record StaffDto(Guid Id, string Email, string FullName, string Role, bool IsActive);
