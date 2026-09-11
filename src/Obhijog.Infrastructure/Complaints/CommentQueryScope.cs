using Obhijog.Domain.Complaints;
using Obhijog.Domain.Users;
using Obhijog.Infrastructure.Auth;

namespace Obhijog.Infrastructure.Complaints;

/// <summary>
/// The §9.4 internal-comment rule, as a seam. Mirrors <see cref="ComplaintQueryScope"/> for the
/// same reason: the filter has to exist in exactly one place, and it has to be applied **in the
/// query** so an internal note is never loaded into memory on a Citizen's behalf at all.
///
/// It is an <c>IQueryable</c> transform rather than a predicate on a loaded list so that it can
/// be exercised over plain objects in a test while being the same code EF translates to SQL.
/// </summary>
public static class CommentQueryScope
{
    /// <summary>
    /// Narrows a comment query to what this caller may read. Visibility of the complaint itself
    /// is <see cref="ComplaintQueryScope"/>'s job; this only removes internal notes.
    /// </summary>
    public static IQueryable<ComplaintComment> For(
        IQueryable<ComplaintComment> source,
        ICurrentUser user) =>
        user.Role == UserRole.Citizen
            ? source.Where(c => !c.IsInternal)
            : source;
}
