using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Obhijog.Domain.Complaints;
using Obhijog.Domain.Exceptions;
using Obhijog.Infrastructure.Auth;
using Obhijog.Infrastructure.Complaints;
using Obhijog.Infrastructure.Options;
using Obhijog.Infrastructure.Persistence;

namespace Obhijog.Infrastructure.Attachments;

/// <summary>
/// Attachment upload, listing and read-URL issuance. SPEC.md §16.2, F6.
///
/// Visibility comes from the complaint: every method starts by asking
/// <see cref="ComplaintQueryScope"/> whether this caller can see the complaint at all, and
/// a complaint they cannot see is <c>404</c> — the attachment inherits the complaint's
/// scope rather than having one of its own (§9.2, §9.3).
/// </summary>
public class AttachmentService(
    ObhijogDbContext db,
    IAttachmentStore store,
    ICurrentUser currentUser,
    IOptions<AttachmentOptions> options,
    TimeProvider timeProvider)
{
    private readonly AttachmentOptions _attachments = options.Value;

    public async Task<AttachmentDto> UploadAsync(
        Guid complaintId,
        AttachmentUpload upload,
        CancellationToken cancellationToken = default)
    {
        await EnsureComplaintVisibleAsync(complaintId, cancellationToken);

        // Type first: it is the cheapest check and the most likely to be wrong.
        if (!_attachments.AllowedContentTypeSet.Contains(upload.ContentType))
        {
            throw new UnsupportedMediaTypeException(
                $"'{Sanitize(upload.ContentType)}' is not an accepted image type. Allowed: "
                + $"{_attachments.AllowedContentTypes}.");
        }

        if (upload.SizeBytes > _attachments.MaxSizeBytes)
        {
            throw new PayloadTooLargeException(
                $"The file is {upload.SizeBytes} bytes; the limit is "
                + $"{_attachments.MaxSizeBytes} bytes.");
        }

        if (upload.SizeBytes <= 0)
        {
            throw new ValidationException("file", "The file is empty.");
        }

        var existing = await db.ComplaintAttachments
            .CountAsync(a => a.ComplaintId == complaintId, cancellationToken);

        // Over the count is a 400, not a 413: the file is fine, the complaint is full (F6).
        if (existing >= _attachments.MaxPerComplaint)
        {
            throw new ValidationException(
                "file",
                $"This complaint already has the maximum of {_attachments.MaxPerComplaint} "
                + "attachments.");
        }

        var attachmentId = Guid.CreateVersion7();
        var blobName = BuildBlobName(complaintId, attachmentId, upload.FileName);

        // The blob goes up first. An orphaned blob after a failed insert costs storage and
        // nothing else; a row pointing at a blob that was never written is a broken image
        // in the gallery forever.
        await store.UploadAsync(blobName, upload.Content, upload.ContentType, cancellationToken);

        var attachment = new ComplaintAttachment
        {
            Id = attachmentId,
            ComplaintId = complaintId,
            BlobName = blobName,
            OriginalFileName = Truncate(Path.GetFileName(upload.FileName), 200),
            ContentType = upload.ContentType,
            SizeBytes = upload.SizeBytes,
            UploadedById = currentUser.Id,
            UploadedAt = timeProvider.GetUtcNow(),
        };

        db.ComplaintAttachments.Add(attachment);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            // Best effort: if this also fails the blob is orphaned, which the M9 lifecycle
            // rule cleans up. Losing the exception would be worse.
            await store.DeleteAsync(blobName, CancellationToken.None);
            throw;
        }

        return ToDto(attachment);
    }

    public async Task<IReadOnlyList<AttachmentDto>> ListAsync(
        Guid complaintId,
        CancellationToken cancellationToken = default)
    {
        await EnsureComplaintVisibleAsync(complaintId, cancellationToken);

        var attachments = await db.ComplaintAttachments
            .AsNoTracking()
            .Where(a => a.ComplaintId == complaintId)
            .OrderBy(a => a.UploadedAt)
            .Select(a => new
            {
                a.Id,
                a.BlobName,
                a.OriginalFileName,
                a.ContentType,
                a.SizeBytes,
                a.UploadedAt,
            })
            .ToListAsync(cancellationToken);

        // Each row carries its own read URL. An <img> tag cannot send a bearer token, so a
        // gallery pointed at the authenticated endpoint would simply fail to load; signing
        // here costs one round trip instead of one per thumbnail, and hands out exactly the
        // URL the 302 endpoint would have redirected the same caller to.
        return attachments
            .Select(a => new AttachmentDto(
                a.Id,
                a.OriginalFileName,
                a.ContentType,
                a.SizeBytes,
                a.UploadedAt,
                store.CreateReadUrl(a.BlobName).ToString()))
            .ToList();
    }

    /// <summary>
    /// The short-lived read URL for one attachment. The endpoint redirects to it rather than
    /// streaming the bytes, so the blob store serves the image and the API does not become a
    /// proxy for every thumbnail on the screen (F6).
    /// </summary>
    public async Task<Uri> CreateReadUrlAsync(
        Guid complaintId,
        Guid attachmentId,
        CancellationToken cancellationToken = default)
    {
        await EnsureComplaintVisibleAsync(complaintId, cancellationToken);

        var blobName = await db.ComplaintAttachments
            .AsNoTracking()
            .Where(a => a.Id == attachmentId && a.ComplaintId == complaintId)
            .Select(a => a.BlobName)
            .SingleOrDefaultAsync(cancellationToken);

        if (blobName is null)
        {
            throw new NotFoundException($"Attachment '{attachmentId}' was not found.");
        }

        return store.CreateReadUrl(blobName);
    }

    /// <summary>
    /// The attachment's visibility is the complaint's visibility. Asking the scope first
    /// means an attachment id belonging to someone else's complaint is <c>404</c> and is
    /// indistinguishable from one that does not exist (§9.2).
    /// </summary>
    private async Task EnsureComplaintVisibleAsync(Guid complaintId, CancellationToken cancellationToken)
    {
        var visible = await ComplaintQueryScope
            .For(db.Complaints.AsNoTracking(), currentUser)
            .AnyAsync(c => c.Id == complaintId, cancellationToken);

        if (!visible)
        {
            throw new NotFoundException($"Complaint '{complaintId}' was not found.");
        }
    }

    /// <summary>
    /// <c>{complaintId}/{attachmentId}{ext}</c>, per §8.7.
    ///
    /// The user's filename is stored for display and **never** used as a path component.
    /// Only the extension is taken from it, and only after it survives an allow-list — a
    /// name like <c>../../etc/passwd.png</c> or one carrying a directory separator cannot
    /// influence where the blob lands, because the name is built from two GUIDs.
    /// </summary>
    private static string BuildBlobName(Guid complaintId, Guid attachmentId, string fileName)
    {
        var extension = Path.GetExtension(fileName);

        var safe = extension.Length is > 1 and <= 10
            && extension.Skip(1).All(char.IsLetterOrDigit)
            ? extension.ToLowerInvariant()
            : string.Empty;

        return $"{complaintId}/{attachmentId}{safe}";
    }

    private AttachmentDto ToDto(ComplaintAttachment attachment) => new(
        attachment.Id,
        attachment.OriginalFileName,
        attachment.ContentType,
        attachment.SizeBytes,
        attachment.UploadedAt,
        store.CreateReadUrl(attachment.BlobName).ToString());

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    /// <summary>Keeps a caller-supplied content type out of a message unbounded.</summary>
    private static string Sanitize(string contentType) => Truncate(contentType, 80);
}

/// <summary>
/// What the endpoint hands the service: the parts of a multipart file it actually needs.
/// A separate type so the service never sees <c>IFormFile</c> and Infrastructure keeps no
/// dependency on the ASP.NET hosting types (§16.3).
/// </summary>
public record AttachmentUpload(
    string FileName,
    string ContentType,
    long SizeBytes,
    Stream Content);

/// <summary>
/// <c>ReadUrl</c> is a short-lived SAS, valid for <c>Storage:ReadSasMinutes</c>. It is on
/// the DTO because a browser cannot attach a bearer token to an <c>&lt;img&gt;</c>, so a
/// gallery has no other way to render a thumbnail from a private container. It grants
/// exactly what the <c>302</c> endpoint would have redirected this same caller to.
/// </summary>
public record AttachmentDto(
    Guid Id,
    string OriginalFileName,
    string ContentType,
    long SizeBytes,
    DateTimeOffset UploadedAt,
    string ReadUrl);
