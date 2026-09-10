namespace Obhijog.Infrastructure.Auth;

/// <summary>
/// Thrown when an already-revoked refresh token is presented. SPEC.md §10.1 makes this a
/// `401` **and** a family revocation: a revoked token in a caller's hands means either
/// the legitimate holder replayed a stale value or someone else stole it, and the two are
/// indistinguishable from here. Revoking the family is the safe reading.
/// </summary>
public class RefreshTokenReuseException(string message) : Exception(message)
{
}
