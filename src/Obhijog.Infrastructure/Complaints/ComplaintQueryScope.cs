using Obhijog.Domain.Complaints;
using Obhijog.Domain.Users;
using Obhijog.Infrastructure.Auth;

namespace Obhijog.Infrastructure.Complaints;

/// <summary>
/// The one enforcement point for complaint visibility. SPEC.md §9.3.
///
/// **Every** complaint query starts here — list, detail, export, dashboard, sweep. Not
/// "most". A hand-rolled <c>Where(c => c.CitizenId == user.Id)</c> anywhere else is a
/// review finding even when it is correct, because the next one will not be and there is
/// no way to test a rule that lives in fifteen places.
///
/// Out of scope is <b>absence</b>, not refusal: a caller who cannot see a row gets a
/// filtered query that returns nothing, and the service turns that into
/// <c>NotFoundException</c> → <c>404</c>. Never <c>403</c> (§9.2).
/// </summary>
public static class ComplaintQueryScope
{
    public static IQueryable<Complaint> For(IQueryable<Complaint> source, ICurrentUser user)
    {
        if (!user.IsAuthenticated)
        {
            // Not "everything": an unauthenticated caller has exactly one legitimate read,
            // the by-reference projection of §9.5, and that one does not come through here.
            return source.Where(_ => false);
        }

        return user.Role switch
        {
            UserRole.Citizen => source.Where(c => c.CitizenId == user.Id),

            // A Staff or DeptAdmin with no department would otherwise match every complaint
            // whose DepartmentId is null — of which there are none today, but the query
            // must not depend on that.
            UserRole.Staff or UserRole.DeptAdmin => user.DepartmentId is { } departmentId
                ? source.Where(c => c.DepartmentId == departmentId)
                : source.Where(_ => false),

            _ => source.Where(_ => false),
        };
    }

    /// <summary>
    /// A <c>departmentId</c> query parameter never widens scope (§9.3, §13.3). It is
    /// honoured only when it names the caller's own department, and ignored otherwise —
    /// ignored rather than rejected, so a stale bookmark degrades to the caller's own data
    /// instead of erroring.
    /// </summary>
    public static IQueryable<Complaint> WithRequestedDepartment(
        IQueryable<Complaint> source,
        ICurrentUser user,
        Guid? requestedDepartmentId) =>
        requestedDepartmentId is { } requested && requested == user.DepartmentId
            ? source.Where(c => c.DepartmentId == requested)
            : source;
}
