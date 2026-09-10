using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Obhijog.Api.Auth;
using Obhijog.Domain.Departments;
using Obhijog.Domain.Users;
using Obhijog.Infrastructure.Auth;
using Obhijog.Infrastructure.Identity;
using Obhijog.Infrastructure.Options;
using Obhijog.Infrastructure.Persistence;
using Xunit;

namespace Obhijog.Tests;

/// <summary>
/// The refresh-token family, and the claims a token actually carries. SPEC.md §10.2.
///
/// <b>This is a fifth suite beyond the four in SPEC.md §18, and here is the reason §18 asks
/// for:</b> the M3 Definition of Done names "refresh rotation revokes families" as an
/// acceptance criterion, and §18's table was written before any token existed. The
/// behaviour is security-critical, its one failure mode that matters is a stolen token that
/// keeps working, and that failure is invisible from the outside — removing the
/// <c>RevokeFamilyAsync</c> call leaves every other test in the repository green.
///
/// It runs against a real PostgreSQL and skips with a visible message otherwise (§18).
/// </summary>
public class RefreshTokenRotationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    // -----------------------------------------------------------------------------------
    // Rotation and the family
    // -----------------------------------------------------------------------------------

    [RequiresPostgresFact]
    public async Task RotationIssuesANewTokenAndRevokesThePresentedOne()
    {
        await using var db = postgres.CreateContext();
        var (service, user) = await ArrangeAsync(db);

        var first = await service.IssueAsync(user);
        var rotated = await service.RotateAsync(first.RefreshToken);

        Assert.NotNull(rotated);
        Assert.NotEqual(first.RefreshToken, rotated.RefreshToken);

        var tokens = await TokensFor(db, user.Id);
        Assert.Equal(2, tokens.Count);
        Assert.Single(tokens, t => t.RevokedAt is not null);
    }

    /// <summary>
    /// The criterion the M3 DoD names. Presenting an already-rotated token is the signature
    /// of a stolen one: the legitimate holder and the thief now both hold a token from the
    /// same family and there is no way to tell which is which, so <b>both</b> die.
    /// </summary>
    [RequiresPostgresFact]
    public async Task ReusingARevokedTokenRevokesTheWholeFamily()
    {
        await using var db = postgres.CreateContext();
        var (service, user) = await ArrangeAsync(db);

        var first = await service.IssueAsync(user);
        var second = await service.RotateAsync(first.RefreshToken);
        Assert.NotNull(second);

        // The thief replays the token the legitimate client already spent.
        await Assert.ThrowsAsync<RefreshTokenReuseException>(
            () => service.RotateAsync(first.RefreshToken));

        // Every token in the family is now dead, including the one that was valid a moment
        // ago. The legitimate user is logged out — that is the intended cost.
        var tokens = await TokensFor(db, user.Id);
        Assert.NotEmpty(tokens);
        Assert.All(tokens, token => Assert.NotNull(token.RevokedAt));

        // And the successor is not merely revoked in the table: presenting it now trips
        // reuse detection in its own right, so a thief who stole the newer token learns
        // nothing from the difference between "invalid" and "already used".
        db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<RefreshTokenReuseException>(
            () => service.RotateAsync(second.RefreshToken));
    }

    /// <summary>
    /// Reuse detection must not fire on a value that was never issued. A token that is
    /// simply wrong is a typo or a stale session, not evidence of theft, and revoking a
    /// family on it would hand anyone a logout button for any user they can name.
    /// </summary>
    [RequiresPostgresFact]
    public async Task AnUnknownTokenIsInvalidButRevokesNothing()
    {
        await using var db = postgres.CreateContext();
        var (service, user) = await ArrangeAsync(db);

        var issued = await service.IssueAsync(user);

        Assert.Null(await service.RotateAsync("not-a-token-that-was-ever-issued"));

        var tokens = await TokensFor(db, user.Id);
        Assert.All(tokens, token => Assert.Null(token.RevokedAt));

        db.ChangeTracker.Clear();
        Assert.NotNull(await service.RotateAsync(issued.RefreshToken));
    }

    [RequiresPostgresFact]
    public async Task AnExpiredTokenIsInvalid()
    {
        await using var db = postgres.CreateContext();
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var (service, user) = await ArrangeAsync(db, clock);

        var issued = await service.IssueAsync(user);
        clock.Advance(TimeSpan.FromDays(new JwtOptions().RefreshTokenDays + 1));

        Assert.Null(await service.RotateAsync(issued.RefreshToken));
    }

    /// <summary>
    /// Logout is deliberately not a family revocation: signing out on a phone must not sign
    /// you out on a laptop.
    ///
    /// Replaying the logged-out token afterwards is a different matter — a revoked token
    /// presented again is indistinguishable from a stolen one, so it trips reuse detection
    /// and takes the family with it. That is the intended reading of §10.2 and the reason
    /// this test asserts the laptop's token still works only *before* the replay.
    /// </summary>
    [RequiresPostgresFact]
    public async Task LogoutRevokesOnlyThePresentedToken()
    {
        await using var db = postgres.CreateContext();
        var (service, user) = await ArrangeAsync(db);

        var phone = await service.IssueAsync(user);
        var laptop = await service.IssueAsync(user);

        await service.RevokeAsync(phone.RefreshToken);

        // The laptop is untouched by the phone's logout.
        db.ChangeTracker.Clear();
        Assert.NotNull(await service.RotateAsync(laptop.RefreshToken));

        // The phone's own token is dead, and replaying it is treated as reuse.
        db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<RefreshTokenReuseException>(
            () => service.RotateAsync(phone.RefreshToken));
    }

    /// <summary>
    /// §10.1: a deactivated account must not be able to refresh its way past the lockout,
    /// and its outstanding tokens go with it.
    /// </summary>
    [RequiresPostgresFact]
    public async Task ADeactivatedAccountCannotRefreshAndLosesItsFamily()
    {
        await using var db = postgres.CreateContext();
        var (service, user) = await ArrangeAsync(db);

        var issued = await service.IssueAsync(user);

        user.IsActive = false;
        await db.SaveChangesAsync();

        Assert.Null(await service.RotateAsync(issued.RefreshToken));

        var tokens = await TokensFor(db, user.Id);
        Assert.All(tokens, token => Assert.NotNull(token.RevokedAt));
    }

    // -----------------------------------------------------------------------------------
    // What the token carries, checked through the API's own validation parameters
    // -----------------------------------------------------------------------------------

    /// <summary>
    /// The issuing and validating halves have to agree, and the disagreement that matters
    /// is <c>RoleClaimType</c>: spell it differently from
    /// <see cref="TokenService.RoleClaim"/> and every caller authenticates while every role
    /// policy denies — which reads as a permissions bug and is a wiring one. Going through
    /// <see cref="TokenService.CreateValidationParameters"/>, exactly as Program.cs does,
    /// is what makes that visible here.
    /// </summary>
    [RequiresPostgresTheory]
    [InlineData(UserRole.Staff, Policies.Staff, Policies.DeptAdmin)]
    [InlineData(UserRole.DeptAdmin, Policies.DeptAdmin, Policies.Staff)]
    public async Task AnIssuedTokenSatisfiesItsOwnRolePolicyAndNoOther(
        UserRole role,
        string granted,
        string denied)
    {
        await using var db = postgres.CreateContext();
        var (service, user) = await ArrangeAsync(db, role: role);

        var principal = Validate((await service.IssueAsync(user)).AccessToken);
        var authorization = AuthorizationService();

        Assert.True((await authorization.AuthorizeAsync(principal, granted)).Succeeded);
        Assert.False((await authorization.AuthorizeAsync(principal, denied)).Succeeded);
    }

    /// <summary>
    /// §10.2: <c>dept</c> is absent for a Citizen, not empty. The guard in
    /// <c>CreateAccessToken</c> keys on the role and not only on a null department, because
    /// a department arriving on a Citizen is a bug elsewhere that must not become a widened
    /// scope here — so the case is constructed deliberately.
    /// </summary>
    [RequiresPostgresFact]
    public async Task ACitizenNeverCarriesADepartmentClaimEvenHoldingADepartment()
    {
        await using var db = postgres.CreateContext();
        var (service, staff) = await ArrangeAsync(db, role: UserRole.Staff);

        // Detached, so nothing here is written back: the table's check constraint makes
        // this row impossible, which is precisely why the claim guard has to hold it.
        var citizenWithADepartment = new User
        {
            Id = staff.Id,
            FullName = staff.FullName,
            Email = staff.Email,
            Role = UserRole.Citizen,
            DepartmentId = staff.DepartmentId,
        };

        var principal = Validate((await service.IssueAsync(citizenWithADepartment)).AccessToken);

        Assert.Equal(nameof(UserRole.Citizen), principal.FindFirstValue(TokenService.RoleClaim));
        Assert.Null(principal.FindFirst(TokenService.DepartmentClaim));
    }

    [RequiresPostgresFact]
    public async Task AStaffTokenCarriesItsDepartment()
    {
        await using var db = postgres.CreateContext();
        var (service, user) = await ArrangeAsync(db, role: UserRole.Staff);

        var principal = Validate((await service.IssueAsync(user)).AccessToken);

        Assert.Equal(
            user.DepartmentId!.Value.ToString(),
            principal.FindFirstValue(TokenService.DepartmentClaim));
    }

    /// <summary>A token signed with any other key must not validate.</summary>
    [RequiresPostgresFact]
    public async Task ATokenSignedWithAnotherKeyIsRejected()
    {
        await using var db = postgres.CreateContext();
        var (_, user) = await ArrangeAsync(db);

        var foreign = new TokenService(
            db,
            Microsoft.Extensions.Options.Options.Create(Jwt("a-completely-different-key-of-32-bytes+")),
            TimeProvider.System);

        var issued = await foreign.IssueAsync(user);

        Assert.Throws<SecurityTokenSignatureKeyNotFoundException>(
            () => Validate(issued.AccessToken));
    }

    // -----------------------------------------------------------------------------------
    // Arrangement
    // -----------------------------------------------------------------------------------

    private const string SigningKey = "obhijog-test-signing-key-well-over-32-bytes";

    private static JwtOptions Jwt(string signingKey = SigningKey) => new()
    {
        SigningKey = signingKey,
        Issuer = "obhijog-tests",
        Audience = "obhijog-tests",
    };

    private static ClaimsPrincipal Validate(string accessToken) =>
        new JwtSecurityTokenHandler { MapInboundClaims = false }
            .ValidateToken(accessToken, TokenService.CreateValidationParameters(Jwt()), out _);

    private static IAuthorizationService AuthorizationService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorizationBuilder().AddObhijogPolicies();

        return services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
    }

    private static async Task<(TokenService Service, User User)> ArrangeAsync(
        ObhijogDbContext db,
        TimeProvider? clock = null,
        UserRole role = UserRole.Staff)
    {
        var service = new TokenService(
            db,
            Microsoft.Extensions.Options.Options.Create(Jwt()),
            clock ?? TimeProvider.System);

        return (service, await CreateUserAsync(db, role));
    }

    /// <summary>
    /// A throwaway user per test, so the suite never depends on seed data and two runs
    /// against the same database cannot collide.
    /// </summary>
    private static async Task<User> CreateUserAsync(ObhijogDbContext db, UserRole role)
    {
        Guid? departmentId = null;

        if (role != UserRole.Citizen)
        {
            // Random, not Guid.CreateVersion7: a v7 leads with a millisecond timestamp, so
            // its first hex characters are identical across tests in the same run and the
            // unique index on departments.code rejects the second one.
            var suffix = Guid.NewGuid().ToString("N")[..11].ToUpperInvariant();
            var department = new Department
            {
                Id = Guid.CreateVersion7(),
                Name = $"Test Department {suffix}",
                Code = $"T{suffix}",
            };

            db.Departments.Add(department);
            departmentId = department.Id;
        }

        var email = $"token-test-{Guid.CreateVersion7():N}@example.test";
        var user = new User
        {
            Id = Guid.CreateVersion7(),
            FullName = "Token Test User",
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            SecurityStamp = Guid.CreateVersion7().ToString(),
            Role = role,
            DepartmentId = departmentId,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();

        return user;
    }

    private static async Task<List<Domain.Auth.RefreshToken>> TokensFor(
        ObhijogDbContext db,
        Guid userId) =>
        await db.RefreshTokens.AsNoTracking().Where(t => t.UserId == userId).ToListAsync();

    /// <summary>
    /// A clock the test moves. <c>TimeProvider</c> is injected everywhere for exactly this
    /// reason (CLAUDE.md non-negotiable 5); the expiry branch is untestable without it.
    /// </summary>
    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}
