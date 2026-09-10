using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Obhijog.Domain.Users;
using Obhijog.Infrastructure.Auth;

namespace Obhijog.Api.Auth;

/// <summary>
/// Reads the caller out of the request's claims once. SPEC.md §10.3.
///
/// Registered scoped, so every service in a request sees the same instance and nothing
/// re-parses claims — the scoping rule of §9.3 has to live in exactly one place.
/// </summary>
public class HttpContextCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated ?? false;

    public Guid Id => Guid.TryParse(Find(JwtRegisteredClaimNames.Sub, ClaimTypes.NameIdentifier), out var id)
        ? id
        : Guid.Empty;

    public string Email => Find(JwtRegisteredClaimNames.Email, ClaimTypes.Email) ?? string.Empty;

    public UserRole Role => Enum.TryParse<UserRole>(Find(TokenService.RoleClaim, ClaimTypes.Role), out var role)
        ? role
        : UserRole.Citizen;

    public Guid? DepartmentId =>
        Guid.TryParse(Find(TokenService.DepartmentClaim), out var id) ? id : null;

    /// <summary>
    /// The JWT handler rewrites several short claim names to their long ClaimTypes URIs, and
    /// which one survives depends on inbound-claim mapping. Checking both spellings keeps
    /// this working whether or not the default map is cleared.
    /// </summary>
    private string? Find(params string[] claimTypes)
    {
        var principal = Principal;
        if (principal is null)
        {
            return null;
        }

        foreach (var type in claimTypes)
        {
            var value = principal.FindFirstValue(type);
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }
        }

        return null;
    }
}
