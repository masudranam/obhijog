namespace Obhijog.Domain.Users;

/// <summary>
/// SPEC.md §3. Mirrored into the Identity role table; <c>User.Role</c> is the queryable copy.
/// </summary>
public enum UserRole
{
    Citizen,
    Staff,
    DeptAdmin,
}
