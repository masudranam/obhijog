using Microsoft.AspNetCore.Authorization;
using Obhijog.Domain.Users;

namespace Obhijog.Api.Auth;

/// <summary>
/// SPEC.md §10.3 — named policies in one place, never role strings scattered through
/// endpoint files.
///
/// A policy answers "may this role reach this route". It does **not** answer "may this
/// user perform this action on this complaint right now" — that is the guard table's job
/// (§12.3), and the two are not interchangeable.
/// </summary>
public static class Policies
{
    public const string Citizen = nameof(Citizen);
    public const string Staff = nameof(Staff);
    public const string DeptAdmin = nameof(DeptAdmin);
    public const string StaffOrAdmin = nameof(StaffOrAdmin);

    public static AuthorizationBuilder AddObhijogPolicies(this AuthorizationBuilder builder) =>
        builder
            .AddPolicy(Citizen, p => p.RequireRole(nameof(UserRole.Citizen)))
            .AddPolicy(Staff, p => p.RequireRole(nameof(UserRole.Staff)))
            .AddPolicy(DeptAdmin, p => p.RequireRole(nameof(UserRole.DeptAdmin)))
            .AddPolicy(StaffOrAdmin, p => p.RequireRole(
                nameof(UserRole.Staff),
                nameof(UserRole.DeptAdmin)));
}
