using Microsoft.EntityFrameworkCore;
using Obhijog.Domain.Complaints;
using Obhijog.Domain.Exceptions;
using Obhijog.Domain.Users;
using Obhijog.Infrastructure.Auth;
using Obhijog.Infrastructure.Persistence;

namespace Obhijog.Infrastructure.Complaints;

/// <summary>
/// Comments on a complaint. SPEC.md §9.4, F9.
///
/// The internal-comment rule is enforced **in the query** and nowhere else: a Citizen's
/// read never selects an internal row, so there is no DTO mapper to forget and no UI
/// condition to get wrong. A filter applied after the rows are loaded would still have put
/// them on the wire.
/// </summary>
public class CommentService(
    ObhijogDbContext db,
    ICurrentUser currentUser,
    TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<CommentDto>> ListAsync(
        Guid complaintId,
        CancellationToken cancellationToken = default)
    {
        await EnsureVisibleAsync(complaintId, cancellationToken);

        // §9.4. In the query — not in the projection, not in the component.
        var query = CommentQueryScope.For(
            db.ComplaintComments.AsNoTracking().Where(c => c.ComplaintId == complaintId),
            currentUser);

        return await query
            .OrderBy(c => c.CreatedAt)
            .Select(c => new CommentDto(
                c.Id,
                c.Body,
                c.IsInternal,
                db.Users.Where(u => u.Id == c.AuthorId).Select(u => u.FullName).FirstOrDefault()
                    ?? "Unknown",
                c.CreatedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<CommentDto> AddAsync(
        Guid complaintId,
        CreateCommentRequest request,
        CancellationToken cancellationToken = default)
    {
        await EnsureVisibleAsync(complaintId, cancellationToken);

        if (string.IsNullOrWhiteSpace(request.Body))
        {
            throw new ValidationException("body", "A comment cannot be empty.");
        }

        if (request.Body.Length > 2000)
        {
            throw new ValidationException("body", "A comment is at most 2000 characters.");
        }

        // §9.4: a Citizen posting isInternal is 403, not a silently-public comment. Quietly
        // downgrading the flag would be worse than refusing — the author would believe they
        // had written a private note.
        if (request.IsInternal && currentUser.Role == UserRole.Citizen)
        {
            throw new ForbiddenException("Only staff may write an internal comment.");
        }

        var comment = new ComplaintComment
        {
            Id = Guid.CreateVersion7(),
            ComplaintId = complaintId,
            AuthorId = currentUser.Id,
            Body = request.Body.Trim(),
            IsInternal = request.IsInternal,
            CreatedAt = timeProvider.GetUtcNow(),
        };

        db.ComplaintComments.Add(comment);
        await db.SaveChangesAsync(cancellationToken);

        return new CommentDto(
            comment.Id,
            comment.Body,
            comment.IsInternal,
            currentUser.Email,
            comment.CreatedAt);
    }

    /// <summary>
    /// A comment's visibility is its complaint's. Out of scope is <c>404</c> (§9.2).
    /// </summary>
    private async Task EnsureVisibleAsync(Guid complaintId, CancellationToken cancellationToken)
    {
        var visible = await ComplaintQueryScope
            .For(db.Complaints.AsNoTracking(), currentUser)
            .AnyAsync(c => c.Id == complaintId, cancellationToken);

        if (!visible)
        {
            throw new NotFoundException($"Complaint '{complaintId}' was not found.");
        }
    }
}

public record CommentDto(
    Guid Id,
    string Body,
    bool IsInternal,
    string AuthorName,
    DateTimeOffset CreatedAt);

public record CreateCommentRequest
{
    public string Body { get; init; } = string.Empty;

    public bool IsInternal { get; init; }
}
