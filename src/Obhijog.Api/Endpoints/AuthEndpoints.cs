using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Obhijog.Domain.Users;
using Obhijog.Infrastructure.Auth;
using Obhijog.Infrastructure.Identity;

namespace Obhijog.Api.Endpoints;

/// <summary>SPEC.md §10.1 and §13.2.</summary>
public static class AuthEndpoints
{
    public record RegisterRequest(
        [property: Required, EmailAddress, MaxLength(256)] string Email,
        [property: Required, MinLength(10), MaxLength(128)] string Password,
        [property: Required, MaxLength(120)] string FullName,
        [property: MaxLength(24)] string? Phone);

    public record LoginRequest(
        [property: Required, EmailAddress] string Email,
        [property: Required] string Password);

    public record RefreshRequest([property: Required] string RefreshToken);

    public record TokenResponse(
        string AccessToken,
        DateTimeOffset AccessTokenExpiresAt,
        string RefreshToken,
        DateTimeOffset RefreshTokenExpiresAt);

    public record MeResponse(
        Guid Id,
        string Email,
        string FullName,
        string Role,
        Guid? DepartmentId);

    public static RouteGroupBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/auth").WithTags("auth");

        group.MapPost("/register", RegisterAsync).AllowAnonymous();
        group.MapPost("/login", LoginAsync).AllowAnonymous();
        group.MapPost("/refresh", RefreshAsync).AllowAnonymous();
        group.MapPost("/logout", LogoutAsync).RequireAuthorization();
        group.MapGet("/me", MeAsync).RequireAuthorization();

        return group;
    }

    /// <summary>
    /// Citizen self-registration only. Any role or department in the payload is not merely
    /// ignored — the request type has no field for one, so privilege escalation is not
    /// expressible rather than filtered out (§10.1).
    /// </summary>
    private static async Task<IResult> RegisterAsync(
        RegisterRequest request,
        UserManager<User> userManager,
        TokenService tokens,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var user = new User
        {
            Id = Guid.CreateVersion7(),
            UserName = request.Email,
            Email = request.Email,
            FullName = request.FullName,
            Phone = request.Phone,
            Role = UserRole.Citizen,
            DepartmentId = null,
            IsActive = true,
            CreatedAt = timeProvider.GetUtcNow(),
        };

        var created = await userManager.CreateAsync(user, request.Password);
        if (!created.Succeeded)
        {
            return Results.ValidationProblem(
                created.Errors
                    .GroupBy(e => e.Code)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray()));
        }

        var roled = await userManager.AddToRoleAsync(user, nameof(UserRole.Citizen));
        if (!roled.Succeeded)
        {
            return Results.ValidationProblem(
                roled.Errors
                    .GroupBy(e => e.Code)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray()));
        }

        return Results.Ok(ToResponse(await tokens.IssueAsync(user, cancellationToken)));
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        UserManager<User> userManager,
        TokenService tokens,
        CancellationToken cancellationToken)
    {
        var user = await userManager.FindByEmailAsync(request.Email);

        // One response for "no such user", "wrong password" and "deactivated". Telling them
        // apart would turn login into an account-enumeration oracle.
        if (user is null
            || !user.IsActive
            || !await userManager.CheckPasswordAsync(user, request.Password))
        {
            return Results.Problem(
                title: "Invalid credentials",
                detail: "The email or password is incorrect.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        return Results.Ok(ToResponse(await tokens.IssueAsync(user, cancellationToken)));
    }

    /// <summary>
    /// Rotation. Reuse of a revoked token throws <see cref="RefreshTokenReuseException"/>,
    /// which the ProblemDetails handler maps to <c>401</c> — the family revocation has
    /// already happened by then and the response does not disclose it (§10.1).
    /// </summary>
    private static async Task<IResult> RefreshAsync(
        RefreshRequest request,
        TokenService tokens,
        CancellationToken cancellationToken)
    {
        var pair = await tokens.RotateAsync(request.RefreshToken, cancellationToken);

        return pair is null
            ? Results.Problem(
                title: "Invalid refresh token",
                detail: "The refresh token is unknown, expired or no longer valid.",
                statusCode: StatusCodes.Status401Unauthorized)
            : Results.Ok(ToResponse(pair));
    }

    /// <summary>
    /// Revokes the presented token only. Deliberately not a family revocation — signing out
    /// on one device should not sign you out everywhere.
    /// </summary>
    private static async Task<IResult> LogoutAsync(
        RefreshRequest request,
        TokenService tokens,
        CancellationToken cancellationToken)
    {
        await tokens.RevokeAsync(request.RefreshToken, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> MeAsync(
        ICurrentUser currentUser,
        UserManager<User> userManager,
        CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(currentUser.Id.ToString());

        if (user is null || !user.IsActive)
        {
            return Results.Problem(
                title: "Unauthorized",
                detail: "The account no longer exists or is inactive.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        return Results.Ok(new MeResponse(
            user.Id,
            user.Email ?? string.Empty,
            user.FullName,
            user.Role.ToString(),
            user.DepartmentId));
    }

    private static TokenResponse ToResponse(TokenPair pair) => new(
        pair.AccessToken,
        pair.AccessTokenExpiresAt,
        pair.RefreshToken,
        pair.RefreshTokenExpiresAt);
}
