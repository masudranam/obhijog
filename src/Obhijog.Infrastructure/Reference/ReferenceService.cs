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

public record StaffDto(Guid Id, string Email, string FullName, string Role, bool IsActive);
