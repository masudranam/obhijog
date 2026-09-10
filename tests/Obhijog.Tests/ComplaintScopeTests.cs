using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Obhijog.Api.Auth;
using Obhijog.Domain.Users;
using Obhijog.Infrastructure.Auth;
using Xunit;

namespace Obhijog.Tests;

/// <summary>
/// SPEC.md §18's role-scoping suite, started at the milestone that makes its first cases
/// writable.
///
/// M3 supplies the claims reader that every scoping decision is built on: `sub`, `role`
/// and `dept` are read **once** per request into <see cref="ICurrentUser"/>, and
/// <c>ComplaintQueryScope</c> consumes it (§10.3, §9.3). Get this wrong and every
/// downstream scoping rule inherits the error, so it is worth its own tests before any
/// query depends on it.
///
/// The complaint-scoping cases §18 names — a Citizen cannot read another citizen's
/// complaint (404, not 403), Staff cannot read another department's, internal comments
/// absent from a Citizen's query — arrive in M4 with <c>ComplaintQueryScope</c> itself.
/// </summary>
public class ComplaintScopeTests
{
    /// <summary>
    /// §10.2: the <c>dept</c> claim is <b>absent</b> for a Citizen, not empty.
    ///
    /// This is the case that matters most. A Citizen whose <c>DepartmentId</c> came back
    /// as <c>Guid.Empty</c> instead of null would compare equal to nothing and silently
    /// widen scope the moment a department filter is written against it.
    /// </summary>
    [Fact]
    public void CitizenHasNoDepartment()
    {
        var currentUser = For(UserRole.Citizen, departmentId: null);

        Assert.Null(currentUser.DepartmentId);
        Assert.Equal(UserRole.Citizen, currentUser.Role);
        Assert.True(currentUser.IsAuthenticated);
    }

    [Theory]
    [InlineData(UserRole.Staff)]
    [InlineData(UserRole.DeptAdmin)]
    public void StaffAndAdminCarryTheirDepartment(UserRole role)
    {
        var department = Guid.CreateVersion7();

        var currentUser = For(role, department);

        Assert.Equal(department, currentUser.DepartmentId);
        Assert.Equal(role, currentUser.Role);
    }

    [Fact]
    public void AnonymousCallerIsNotAuthenticatedAndCarriesNoIdentity()
    {
        var currentUser = new HttpContextCurrentUser(
            Accessor(new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) }));

        Assert.False(currentUser.IsAuthenticated);
        Assert.Equal(Guid.Empty, currentUser.Id);
        Assert.Null(currentUser.DepartmentId);
    }

    /// <summary>
    /// A malformed <c>dept</c> claim must read as "no department", never as a parsed
    /// zero — the same reasoning as <see cref="CitizenHasNoDepartment"/>.
    /// </summary>
    [Fact]
    public void MalformedDepartmentClaimIsNull()
    {
        var identity = new ClaimsIdentity(
            [
                new Claim("sub", Guid.CreateVersion7().ToString()),
                new Claim(TokenService.RoleClaim, nameof(UserRole.Staff)),
                new Claim(TokenService.DepartmentClaim, "not-a-guid"),
            ],
            authenticationType: "Test");

        var currentUser = new HttpContextCurrentUser(
            Accessor(new DefaultHttpContext { User = new ClaimsPrincipal(identity) }));

        Assert.Null(currentUser.DepartmentId);
    }

    /// <summary>
    /// The four policies of §10.3 exist and are distinct. A policy answers "may this role
    /// reach this route" — the DoD's "a Staff token on a DeptAdmin endpoint → 403" rests
    /// on <see cref="Policies.DeptAdmin"/> and <see cref="Policies.Staff"/> not being the
    /// same thing.
    /// </summary>
    [Fact]
    public void PolicyNamesAreDistinct()
    {
        string[] policies =
        [
            Policies.Citizen,
            Policies.Staff,
            Policies.DeptAdmin,
            Policies.StaffOrAdmin,
        ];

        Assert.Equal(policies.Length, policies.Distinct(StringComparer.Ordinal).Count());
    }

    private static HttpContextCurrentUser For(UserRole role, Guid? departmentId)
    {
        var claims = new List<Claim>
        {
            new("sub", Guid.CreateVersion7().ToString()),
            new("email", $"{role}@example.test"),
            new(TokenService.RoleClaim, role.ToString()),
        };

        if (departmentId is { } id)
        {
            claims.Add(new Claim(TokenService.DepartmentClaim, id.ToString()));
        }

        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test")),
        };

        return new HttpContextCurrentUser(Accessor(context));
    }

    private static IHttpContextAccessor Accessor(HttpContext context) =>
        new HttpContextAccessor { HttpContext = context };
}
