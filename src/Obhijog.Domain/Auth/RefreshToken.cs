namespace Obhijog.Domain.Auth;

/// <summary>
/// SPEC.md §10.2.
///
/// The token itself is an opaque 256-bit random value and is **never stored** — only its
/// SHA-256 hash. A leaked database therefore yields no usable refresh token.
///
/// Rotation is what makes reuse detectable: each refresh revokes the presented token and
/// records the hash that replaced it, so a second presentation of an already-revoked
/// token is unambiguous theft and revokes the whole family.
/// </summary>
public class RefreshToken
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    /// <summary>SHA-256 of the opaque token, hex-encoded. 64 characters.</summary>
    public required string TokenHash { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>
    /// The hash of the token issued in this one's place. Set on rotation, which is what
    /// lets a reuse walk the chain and revoke every descendant.
    /// </summary>
    public string? ReplacedByHash { get; set; }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;
}
