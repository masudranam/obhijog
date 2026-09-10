using Microsoft.AspNetCore.Identity;
using Obhijog.Domain.Users;

namespace Obhijog.Infrastructure.Identity;

/// <summary>
/// SPEC.md §8.1.
///
/// This entity lives in Infrastructure rather than Domain because it extends
/// <see cref="IdentityUser{TKey}"/>, and the domain layer takes no reference to the
/// ASP.NET Identity stack (SPEC.md §16.3, CLAUDE.md non-negotiable 7). Domain entities
/// therefore reference users by <see cref="Guid"/> only — see D13.
/// </summary>
public class User : IdentityUser<Guid>
{
    /// <summary>Max 120 characters.</summary>
    public required string FullName { get; set; }

    /// <summary>Optional. Max 24 characters.</summary>
    public string? Phone { get; set; }

    /// <summary>
    /// The queryable copy of the role, mirrored into the Identity role table.
    /// </summary>
    public UserRole Role { get; set; }

    /// <summary>
    /// Null for a Citizen, required for Staff and DeptAdmin — enforced by a check
    /// constraint, not by convention.
    /// </summary>
    public Guid? DepartmentId { get; set; }

    public Domain.Departments.Department? Department { get; set; }

    /// <summary>Inactive users cannot log in and cannot be assigned.</summary>
    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }
}
