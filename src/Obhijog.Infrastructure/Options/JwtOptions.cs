using System.ComponentModel.DataAnnotations;

namespace Obhijog.Infrastructure.Options;

/// <summary>SPEC.md §10.2 and §19.</summary>
public class JwtOptions
{
    public const string SectionName = "Jwt";

    /// <summary>
    /// HS256 needs at least 256 bits of key. Shorter is a weak signature rather than a
    /// configuration preference, so startup fails rather than warning (§10.2).
    /// </summary>
    public const int MinimumSigningKeyBytes = 32;

    [Required]
    public string Issuer { get; set; } = "obhijog";

    [Required]
    public string Audience { get; set; } = "obhijog";

    /// <summary>Required, with no default. A built-in fallback that reaches production is
    /// the same bug as a hard-coded secret.</summary>
    [Required(AllowEmptyStrings = false)]
    public string SigningKey { get; set; } = string.Empty;

    [Range(1, 1440)]
    public int AccessTokenMinutes { get; set; } = 15;

    [Range(1, 365)]
    public int RefreshTokenDays { get; set; } = 14;
}
