using Obhijog.Domain.Users;

namespace Obhijog.Infrastructure.Auth;

/// <summary>
/// The caller, read once per request from the claims. SPEC.md §10.3.
///
/// This is what <c>ComplaintQueryScope</c> consumes (M4). Services must never re-parse
/// claims — one reader means one place for the scoping rule to be wrong.
/// </summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }

    Guid Id { get; }

    string Email { get; }

    UserRole Role { get; }

    /// <summary>Null for a Citizen — the <c>dept</c> claim is absent, not empty (§10.2).</summary>
    Guid? DepartmentId { get; }
}
