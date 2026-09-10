using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Obhijog.Api.Auth;
using Obhijog.Api.Endpoints;
using Obhijog.Domain.Users;
using Obhijog.Infrastructure.Auth;
using Obhijog.Infrastructure.Reference;
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
    /// The whole §10.3 policy matrix, evaluated by the real authorization service against
    /// the real <see cref="Policies.AddObhijogPolicies"/> registration.
    ///
    /// This replaces an earlier test that asserted the four policy *names* were distinct.
    /// That was a tautology of the declarations: widening
    /// <c>DeptAdmin</c> to <c>RequireRole(Staff, DeptAdmin)</c> — which is how this
    /// actually breaks — left it green while handing every Staff member a Dept Admin
    /// endpoint. The DoD item "a Staff token on a DeptAdmin endpoint → 403" is this row.
    /// </summary>
    [Theory]
    // Citizen reaches the citizen policy and nothing else.
    [InlineData(UserRole.Citizen, Policies.Citizen, true)]
    [InlineData(UserRole.Citizen, Policies.Staff, false)]
    [InlineData(UserRole.Citizen, Policies.DeptAdmin, false)]
    [InlineData(UserRole.Citizen, Policies.StaffOrAdmin, false)]
    // Staff must NOT reach a Dept Admin route. This is the DoD row.
    [InlineData(UserRole.Staff, Policies.Staff, true)]
    [InlineData(UserRole.Staff, Policies.DeptAdmin, false)]
    [InlineData(UserRole.Staff, Policies.StaffOrAdmin, true)]
    [InlineData(UserRole.Staff, Policies.Citizen, false)]
    // A Dept Admin is not a superset of Staff: §9.1 has no super-admin, and the two
    // roles do different jobs.
    [InlineData(UserRole.DeptAdmin, Policies.DeptAdmin, true)]
    [InlineData(UserRole.DeptAdmin, Policies.StaffOrAdmin, true)]
    [InlineData(UserRole.DeptAdmin, Policies.Staff, false)]
    [InlineData(UserRole.DeptAdmin, Policies.Citizen, false)]
    public async Task PolicyMatrix(UserRole role, string policy, bool expected)
    {
        var authorization = AuthorizationService();

        var result = await authorization.AuthorizeAsync(PrincipalFor(role), policy);

        Assert.Equal(expected, result.Succeeded);
    }

    [Fact]
    public async Task AnAnonymousPrincipalSatisfiesNoPolicy()
    {
        var authorization = AuthorizationService();
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        foreach (var policy in new[]
                 {
                     Policies.Citizen, Policies.Staff, Policies.DeptAdmin, Policies.StaffOrAdmin,
                 })
        {
            var result = await authorization.AuthorizeAsync(anonymous, policy);
            Assert.False(result.Succeeded);
        }
    }

    /// <summary>
    /// The policy is only worth anything if the route carries it. Asserting the matrix
    /// above still leaves "someone deleted <c>.RequireAuthorization(Policies.DeptAdmin)</c>
    /// from the route" invisible, so read it back off the endpoint's own metadata.
    /// </summary>
    [Fact]
    public void TheDepartmentStaffRouteRequiresTheDeptAdminPolicy()
    {
        var endpoint = Assert.Single(
            MappedEndpoints(),
            e => e.RoutePattern.RawText?.EndsWith("/staff", StringComparison.Ordinal) == true);

        var authorize = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();

        Assert.Equal(
            [Policies.DeptAdmin],
            authorize.Select(a => a.Policy).Where(p => p is not null));
    }

    /// <summary>
    /// Nothing under /departments is anonymous. §13.2 marks the list "any", which means
    /// any *authenticated* role, not any caller.
    /// </summary>
    [Fact]
    public void EveryReferenceRouteRequiresAuthentication()
    {
        foreach (var endpoint in MappedEndpoints())
        {
            Assert.Empty(endpoint.Metadata.GetOrderedMetadata<IAllowAnonymous>());
            Assert.NotEmpty(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
        }
    }

    private static IReadOnlyList<RouteEndpoint> MappedEndpoints()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Services.AddAuthorizationBuilder().AddObhijogPolicies();

        // Registered so minimal-API parameter inference reads it as a service rather than
        // inferring a request body on a GET. The handlers are never invoked here — only
        // their metadata is — so the instance is never produced.
        builder.Services.AddScoped<ReferenceService>(_ =>
            throw new NotSupportedException("Endpoint metadata only."));

        var app = builder.Build();
        app.MapGroup("/api/v1").MapReferenceEndpoints();

        return ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToList();
    }

    private static IAuthorizationService AuthorizationService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorizationBuilder().AddObhijogPolicies();

        return services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
    }

    /// <summary>
    /// The role claim is <c>role</c> (§10.2), so the identity has to be told that is its
    /// role claim type — exactly as <see cref="TokenService.CreateValidationParameters"/>
    /// tells the JWT handler.
    /// </summary>
    private static ClaimsPrincipal PrincipalFor(UserRole role) =>
        new(new ClaimsIdentity(
            [
                new Claim("sub", Guid.CreateVersion7().ToString()),
                new Claim(TokenService.RoleClaim, role.ToString()),
            ],
            authenticationType: "Test",
            nameType: TokenService.NameClaim,
            roleType: TokenService.RoleClaim));

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
