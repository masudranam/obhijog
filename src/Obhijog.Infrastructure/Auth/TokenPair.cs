namespace Obhijog.Infrastructure.Auth;

/// <summary>
/// What a successful login, register or refresh returns. The refresh token is the only
/// place the opaque value exists in plaintext — the database holds its hash (§10.2).
/// </summary>
public record TokenPair(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt);
