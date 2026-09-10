using System.ComponentModel.DataAnnotations;

namespace Obhijog.Infrastructure.Options;

/// <summary>SPEC.md §19.</summary>
public class StorageOptions
{
    public const string SectionName = "Storage";

    [Required(AllowEmptyStrings = false)]
    public string ConnectionString { get; set; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    public string Container { get; set; } = "complaint-attachments";

    [Range(1, 1440)]
    public int ReadSasMinutes { get; set; } = 15;
}
