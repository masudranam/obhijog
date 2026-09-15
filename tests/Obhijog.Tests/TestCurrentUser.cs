using Obhijog.Domain.Users;
using Obhijog.Infrastructure.Auth;

namespace Obhijog.Tests;

/// <summary>
/// A caller, without an HTTP request to read one from.
///
/// <c>ICurrentUser</c> is what <c>ComplaintQueryScope</c> consumes (§9.3, §10.3), so every
/// scoping assertion needs a way to be somebody. The production reader,
/// <c>HttpContextCurrentUser</c>, pulls the same four values out of the request's claims;
/// this supplies them directly.
/// </summary>
public sealed class TestCurrentUser(
    Guid id,
    UserRole role,
    Guid? departmentId = null,
    bool authenticated = true) : ICurrentUser
{
    public bool IsAuthenticated => authenticated;

    public Guid Id => id;

    public string Email => $"{role}-{id:N}@example.test";

    public UserRole Role => role;

    /// <summary>
    /// §10.2: the <c>dept</c> claim is <b>absent</b> for a Citizen, not empty. Enforced here
    /// too, so a test cannot accidentally construct a citizen with a department and get a
    /// scoping answer production could never produce.
    /// </summary>
    public Guid? DepartmentId => role == UserRole.Citizen ? null : departmentId;
}
