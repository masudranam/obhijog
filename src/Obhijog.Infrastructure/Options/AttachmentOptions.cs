using System.ComponentModel.DataAnnotations;

namespace Obhijog.Infrastructure.Options;

/// <summary>SPEC.md §19, F6.</summary>
public class AttachmentOptions
{
    public const string SectionName = "Attachments";

    /// <summary>5 MB. Enforced before a byte reaches the blob store.</summary>
    [Range(1, 104_857_600)]
    public long MaxSizeBytes { get; set; } = 5_242_880;

    /// <summary>
    /// An allow-list, never a deny-list: anything not named here is rejected. Bound from a
    /// comma-separated value so a single environment variable can carry it —
    /// <c>Attachments__AllowedContentTypes=image/jpeg,image/png</c> — which an array-shaped
    /// key cannot do without index suffixes.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string AllowedContentTypes { get; set; } = "image/jpeg,image/png,image/webp";

    [Range(1, 50)]
    public int MaxPerComplaint { get; set; } = 5;

    public IReadOnlySet<string> AllowedContentTypeSet =>
        AllowedContentTypes
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
