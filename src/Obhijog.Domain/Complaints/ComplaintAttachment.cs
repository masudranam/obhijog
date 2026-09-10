namespace Obhijog.Domain.Complaints;

/// <summary>SPEC.md §8.7.</summary>
public class ComplaintAttachment
{
    public Guid Id { get; set; }

    public Guid ComplaintId { get; set; }

    public Complaint? Complaint { get; set; }

    /// <summary>
    /// <c>{complaintId}/{attachmentId}{ext}</c> — **never** the user-supplied filename.
    /// Unique, max 200 characters.
    /// </summary>
    public required string BlobName { get; set; }

    /// <summary>Max 200 characters.</summary>
    public required string OriginalFileName { get; set; }

    /// <summary>Max 80 characters.</summary>
    public required string ContentType { get; set; }

    public long SizeBytes { get; set; }

    public Guid UploadedById { get; set; }

    public DateTimeOffset UploadedAt { get; set; }
}
