using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Obhijog.Domain.Auth;
using Obhijog.Domain.Users;
using Obhijog.Infrastructure.Identity;
using Obhijog.Infrastructure.Persistence;

namespace Obhijog.Infrastructure.Auth;

/// <summary>
/// Issues access tokens and manages the rotating refresh-token family. SPEC.md §10.2.
/// </summary>
public class TokenService(
    ObhijogDbContext db,
    IOptions<Options.JwtOptions> options,
    TimeProvider timeProvider)
{
    /// <summary>256 bits, per §10.2.</summary>
    private const int RefreshTokenBytes = 32;

    private readonly Options.JwtOptions _jwt = options.Value;

    /// <summary>
    /// The department claim. Absent for a Citizen rather than empty — a claim that exists
    /// with no value is a scoping bug waiting to be written (§10.2).
    /// </summary>
    public const string DepartmentClaim = "dept";

    /// <summary>
    /// The role claim. §10.2 names it "role", so it is emitted short like every other
    /// claim rather than as ClaimTypes.Role, which expands to a Microsoft schema URI and
    /// would leave one claim in the token spelled unlike the other four.
    /// </summary>
    public const string RoleClaim = "role";

    public async Task<TokenPair> IssueAsync(User user, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var (accessToken, accessExpires) = CreateAccessToken(user, now);
        var (refreshToken, refreshExpires) = await CreateRefreshTokenAsync(user.Id, now, cancellationToken);

        return new TokenPair(accessToken, accessExpires, refreshToken, refreshExpires);
    }

    /// <summary>
    /// Rotates a refresh token: the presented one is revoked and a new pair issued.
    ///
    /// Presenting an already-revoked token throws <see cref="RefreshTokenReuseException"/>
    /// and revokes every token the user holds. An unknown or expired token is simply
    /// invalid and returns null — it carries no evidence of compromise.
    /// </summary>
    public async Task<TokenPair?> RotateAsync(
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var hash = Hash(refreshToken);

        var stored = await db.RefreshTokens.SingleOrDefaultAsync(
            t => t.TokenHash == hash,
            cancellationToken);

        if (stored is null)
        {
            return null;
        }

        if (stored.RevokedAt is not null)
        {
            await RevokeFamilyAsync(stored.UserId, now, cancellationToken);
            throw new RefreshTokenReuseException(
                "Refresh token has already been used. The token family has been revoked.");
        }

        if (stored.ExpiresAt <= now)
        {
            return null;
        }

        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == stored.UserId, cancellationToken);

        // A deactivated account must not be able to refresh its way past a lockout (§10.1).
        if (user is null || !user.IsActive)
        {
            await RevokeFamilyAsync(stored.UserId, now, cancellationToken);
            return null;
        }

        var (accessToken, accessExpires) = CreateAccessToken(user, now);
        var (newRefresh, refreshExpires) = await CreateRefreshTokenAsync(
            user.Id,
            now,
            cancellationToken,
            saveChanges: false);

        stored.RevokedAt = now;
        stored.ReplacedByHash = Hash(newRefresh);

        await db.SaveChangesAsync(cancellationToken);

        return new TokenPair(accessToken, accessExpires, newRefresh, refreshExpires);
    }

    /// <summary>
    /// Revokes a single token. Logout is deliberately not a family revocation — signing out
    /// of one device should not sign you out of the others.
    /// </summary>
    public async Task RevokeAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        var hash = Hash(refreshToken);

        var stored = await db.RefreshTokens.SingleOrDefaultAsync(
            t => t.TokenHash == hash,
            cancellationToken);

        if (stored is null || stored.RevokedAt is not null)
        {
            return;
        }

        stored.RevokedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Revokes every token the user still holds. Called on reuse detection and when a
    /// refresh arrives for a deactivated account.
    /// </summary>
    public async Task RevokeFamilyAsync(
        Guid userId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(t => t.RevokedAt, now),
                cancellationToken);
    }

    private (string Token, DateTimeOffset ExpiresAt) CreateAccessToken(User user, DateTimeOffset now)
    {
        var expires = now.AddMinutes(_jwt.AccessTokenMinutes);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.CreateVersion7().ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email ?? string.Empty),
            new(JwtRegisteredClaimNames.Name, user.FullName),
            new(RoleClaim, user.Role.ToString()),
        };

        // Absent for a Citizen (§10.2). ComplaintQueryScope reads its absence as "no
        // department", so emitting an empty string here would quietly widen scope.
        if (user.Role != UserRole.Citizen && user.DepartmentId is { } departmentId)
        {
            claims.Add(new Claim(DepartmentClaim, departmentId.ToString()));
        }

        var key = new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(_jwt.SigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _jwt.Issuer,
            audience: _jwt.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expires.UtcDateTime,
            signingCredentials: credentials);

        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }

    private async Task<(string Token, DateTimeOffset ExpiresAt)> CreateRefreshTokenAsync(
        Guid userId,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        bool saveChanges = true)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(RefreshTokenBytes));
        var expires = now.AddDays(_jwt.RefreshTokenDays);

        db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            TokenHash = Hash(token),
            CreatedAt = now,
            ExpiresAt = expires,
        });

        if (saveChanges)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return (token, expires);
    }

    /// <summary>
    /// SHA-256, hex-encoded. Not a password hash and deliberately not a slow one: this is a
    /// 256-bit random value, so there is no dictionary to attack and nothing for a work
    /// factor to buy. The hash exists so a leaked table yields no usable token.
    /// </summary>
    private static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
}
