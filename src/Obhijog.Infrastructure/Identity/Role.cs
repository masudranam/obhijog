using Microsoft.AspNetCore.Identity;

namespace Obhijog.Infrastructure.Identity;

/// <summary>
/// The Identity role table. <c>User.Role</c> (SPEC.md §8.1) is the queryable copy of the
/// same fact; this is what ASP.NET Identity and the role claim are built on.
/// </summary>
public class Role : IdentityRole<Guid>
{
}
