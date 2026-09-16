using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Obhijog.Api.Auth;
using Obhijog.Api.Endpoints;
using Obhijog.Api.Errors;
using Obhijog.Domain.Complaints;
using Obhijog.Domain.Departments;
using Obhijog.Domain.Exceptions;
using Obhijog.Domain.Sla;
using Obhijog.Domain.Users;
using Obhijog.Infrastructure.Attachments;
using Obhijog.Infrastructure.Auth;
using Obhijog.Infrastructure.Complaints;
using Obhijog.Infrastructure.Identity;
using Obhijog.Infrastructure.Options;
using Obhijog.Infrastructure.Persistence;
using Obhijog.Infrastructure.Reference;
using Xunit;

namespace Obhijog.Tests;

/// <summary>
/// SPEC.md §18's role-scoping suite, started at the milestone that makes its first cases
/// writable.
///
/// M3 supplies the claims reader that every scoping decision is built on: `sub`, `role`
/// and `dept` are read **once** per request into <see cref="ICurrentUser"/>, and
/// <c>ComplaintQueryScope</c> consumes it (§10.3, §9.3). Get this wrong and every
/// downstream scoping rule inherits the error, so it is worth its own tests before any
/// query depends on it.
///
/// The complaint-scoping cases §18 names — a Citizen cannot read another citizen's
/// complaint (404, not 403), Staff cannot read another department's, internal comments
/// absent from a Citizen's query — arrive in M4 with <c>ComplaintQueryScope</c> itself.
/// </summary>
public class ComplaintScopeTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    /// <summary>
    /// §10.2: the <c>dept</c> claim is <b>absent</b> for a Citizen, not empty.
    ///
    /// This is the case that matters most. A Citizen whose <c>DepartmentId</c> came back
    /// as <c>Guid.Empty</c> instead of null would compare equal to nothing and silently
    /// widen scope the moment a department filter is written against it.
    /// </summary>
    [Fact]
    public void CitizenHasNoDepartment()
    {
        var currentUser = For(UserRole.Citizen, departmentId: null);

        Assert.Null(currentUser.DepartmentId);
        Assert.Equal(UserRole.Citizen, currentUser.Role);
        Assert.True(currentUser.IsAuthenticated);
    }

    [Theory]
    [InlineData(UserRole.Staff)]
    [InlineData(UserRole.DeptAdmin)]
    public void StaffAndAdminCarryTheirDepartment(UserRole role)
    {
        var department = Guid.CreateVersion7();

        var currentUser = For(role, department);

        Assert.Equal(department, currentUser.DepartmentId);
        Assert.Equal(role, currentUser.Role);
    }

    [Fact]
    public void AnonymousCallerIsNotAuthenticatedAndCarriesNoIdentity()
    {
        var currentUser = new HttpContextCurrentUser(
            Accessor(new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) }));

        Assert.False(currentUser.IsAuthenticated);
        Assert.Equal(Guid.Empty, currentUser.Id);
        Assert.Null(currentUser.DepartmentId);
    }

    /// <summary>
    /// A malformed <c>dept</c> claim must read as "no department", never as a parsed
    /// zero — the same reasoning as <see cref="CitizenHasNoDepartment"/>.
    /// </summary>
    [Fact]
    public void MalformedDepartmentClaimIsNull()
    {
        var identity = new ClaimsIdentity(
            [
                new Claim("sub", Guid.CreateVersion7().ToString()),
                new Claim(TokenService.RoleClaim, nameof(UserRole.Staff)),
                new Claim(TokenService.DepartmentClaim, "not-a-guid"),
            ],
            authenticationType: "Test");

        var currentUser = new HttpContextCurrentUser(
            Accessor(new DefaultHttpContext { User = new ClaimsPrincipal(identity) }));

        Assert.Null(currentUser.DepartmentId);
    }

    /// <summary>
    /// The whole §10.3 policy matrix, evaluated by the real authorization service against
    /// the real <see cref="Policies.AddObhijogPolicies"/> registration.
    ///
    /// This replaces an earlier test that asserted the four policy *names* were distinct.
    /// That was a tautology of the declarations: widening
    /// <c>DeptAdmin</c> to <c>RequireRole(Staff, DeptAdmin)</c> — which is how this
    /// actually breaks — left it green while handing every Staff member a Dept Admin
    /// endpoint. The DoD item "a Staff token on a DeptAdmin endpoint → 403" is this row.
    /// </summary>
    [Theory]
    // Citizen reaches the citizen policy and nothing else.
    [InlineData(UserRole.Citizen, Policies.Citizen, true)]
    [InlineData(UserRole.Citizen, Policies.Staff, false)]
    [InlineData(UserRole.Citizen, Policies.DeptAdmin, false)]
    [InlineData(UserRole.Citizen, Policies.StaffOrAdmin, false)]
    // Staff must NOT reach a Dept Admin route. This is the DoD row.
    [InlineData(UserRole.Staff, Policies.Staff, true)]
    [InlineData(UserRole.Staff, Policies.DeptAdmin, false)]
    [InlineData(UserRole.Staff, Policies.StaffOrAdmin, true)]
    [InlineData(UserRole.Staff, Policies.Citizen, false)]
    // A Dept Admin is not a superset of Staff: §9.1 has no super-admin, and the two
    // roles do different jobs.
    [InlineData(UserRole.DeptAdmin, Policies.DeptAdmin, true)]
    [InlineData(UserRole.DeptAdmin, Policies.StaffOrAdmin, true)]
    [InlineData(UserRole.DeptAdmin, Policies.Staff, false)]
    [InlineData(UserRole.DeptAdmin, Policies.Citizen, false)]
    public async Task PolicyMatrix(UserRole role, string policy, bool expected)
    {
        var authorization = AuthorizationService();

        var result = await authorization.AuthorizeAsync(PrincipalFor(role), policy);

        Assert.Equal(expected, result.Succeeded);
    }

    [Fact]
    public async Task AnAnonymousPrincipalSatisfiesNoPolicy()
    {
        var authorization = AuthorizationService();
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        foreach (var policy in new[]
                 {
                     Policies.Citizen, Policies.Staff, Policies.DeptAdmin, Policies.StaffOrAdmin,
                 })
        {
            var result = await authorization.AuthorizeAsync(anonymous, policy);
            Assert.False(result.Succeeded);
        }
    }

    /// <summary>
    /// The policy is only worth anything if the route carries it. Asserting the matrix
    /// above still leaves "someone deleted <c>.RequireAuthorization(Policies.DeptAdmin)</c>
    /// from the route" invisible, so read it back off the endpoint's own metadata.
    /// </summary>
    [Fact]
    public void TheDepartmentStaffRouteRequiresTheDeptAdminPolicy()
    {
        var endpoint = Assert.Single(
            MappedEndpoints(),
            e => e.RoutePattern.RawText?.EndsWith("/staff", StringComparison.Ordinal) == true);

        var authorize = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();

        Assert.Equal(
            [Policies.DeptAdmin],
            authorize.Select(a => a.Policy).Where(p => p is not null));
    }

    /// <summary>
    /// Nothing under /departments is anonymous. §13.2 marks the list "any", which means
    /// any *authenticated* role, not any caller.
    /// </summary>
    [Fact]
    public void EveryReferenceRouteRequiresAuthentication()
    {
        foreach (var endpoint in MappedEndpoints())
        {
            Assert.Empty(endpoint.Metadata.GetOrderedMetadata<IAllowAnonymous>());
            Assert.NotEmpty(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
        }
    }

    private static IReadOnlyList<RouteEndpoint> MappedEndpoints()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Services.AddAuthorizationBuilder().AddObhijogPolicies();

        // Registered so minimal-API parameter inference reads it as a service rather than
        // inferring a request body on a GET. The handlers are never invoked here — only
        // their metadata is — so the instance is never produced.
        builder.Services.AddScoped<ReferenceService>(_ =>
            throw new NotSupportedException("Endpoint metadata only."));

        var app = builder.Build();
        app.MapGroup("/api/v1").MapReferenceEndpoints();

        return ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToList();
    }

    private static IAuthorizationService AuthorizationService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorizationBuilder().AddObhijogPolicies();

        return services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
    }

    /// <summary>
    /// The role claim is <c>role</c> (§10.2), so the identity has to be told that is its
    /// role claim type — exactly as <see cref="TokenService.CreateValidationParameters"/>
    /// tells the JWT handler.
    /// </summary>
    private static ClaimsPrincipal PrincipalFor(UserRole role) =>
        new(new ClaimsIdentity(
            [
                new Claim("sub", Guid.CreateVersion7().ToString()),
                new Claim(TokenService.RoleClaim, role.ToString()),
            ],
            authenticationType: "Test",
            nameType: TokenService.NameClaim,
            roleType: TokenService.RoleClaim));

    // -----------------------------------------------------------------------------------
    // The scoping seam itself (§9.3, §18's role-scoping cases)
    //
    // ComplaintQueryScope.For is a filter over IQueryable, so LINQ-to-objects exercises it
    // exactly as EF will translate it — no database, no provider. The M4 endpoints add
    // nothing on top: what these tests prove about the seam is true of every read.
    // -----------------------------------------------------------------------------------

    private static readonly Guid Water = Guid.CreateVersion7();
    private static readonly Guid Electrical = Guid.CreateVersion7();
    private static readonly Guid Citizen1 = Guid.CreateVersion7();
    private static readonly Guid Citizen2 = Guid.CreateVersion7();

    private static IQueryable<Complaint> Complaints() => new[]
    {
        Complaint(Citizen1, Water, "citizen1-water"),
        Complaint(Citizen2, Water, "citizen2-water"),
        Complaint(Citizen1, Electrical, "citizen1-electrical"),
    }.AsQueryable();

    /// <summary>
    /// §9.2 and §9.4's first case: a Citizen sees their own complaints and nobody else's.
    /// The filter returns nothing for another citizen's row, which is what turns into a
    /// <c>404</c> — never a <c>403</c>.
    /// </summary>
    [Fact]
    public void ACitizenSeesOnlyTheirOwnComplaints()
    {
        var scoped = ComplaintQueryScope
            .For(Complaints(), For(UserRole.Citizen, departmentId: null, id: Citizen1))
            .Select(c => c.Title)
            .ToList();

        Assert.Equal(["citizen1-water", "citizen1-electrical"], scoped);
    }

    /// <summary>Staff and Dept Admins see their own department, across all citizens.</summary>
    [Theory]
    [InlineData(UserRole.Staff)]
    [InlineData(UserRole.DeptAdmin)]
    public void StaffSeeTheirOwnDepartmentOnly(UserRole role)
    {
        var scoped = ComplaintQueryScope
            .For(Complaints(), For(role, Water, Guid.CreateVersion7()))
            .Select(c => c.Title)
            .ToList();

        Assert.Equal(["citizen1-water", "citizen2-water"], scoped);
    }

    /// <summary>
    /// A Staff or DeptAdmin token with no <c>dept</c> claim must match nothing. Without the
    /// explicit guard the comparison would be <c>DepartmentId == null</c>, which is a query
    /// that happens to return no rows today and would silently start returning them the day
    /// a nullable department appears.
    /// </summary>
    [Fact]
    public void StaffWithNoDepartmentSeeNothing()
    {
        var scoped = ComplaintQueryScope.For(
            Complaints(),
            For(UserRole.Staff, departmentId: null, id: Guid.CreateVersion7()));

        Assert.Empty(scoped);
    }

    [Fact]
    public void AnAnonymousCallerSeesNothing()
    {
        var anonymous = new HttpContextCurrentUser(
            Accessor(new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) }));

        Assert.Empty(ComplaintQueryScope.For(Complaints(), anonymous));
    }

    /// <summary>
    /// §9.3, §13.3: a <c>departmentId</c> query parameter can narrow but never widen. A
    /// Water admin asking for Electrical keeps seeing Water.
    /// </summary>
    [Fact]
    public void ARequestedDepartmentNeverWidensScope()
    {
        var admin = For(UserRole.DeptAdmin, Water, Guid.CreateVersion7());

        var scoped = ComplaintQueryScope.WithRequestedDepartment(
                ComplaintQueryScope.For(Complaints(), admin),
                admin,
                Electrical)
            .Select(c => c.Title)
            .ToList();

        Assert.Equal(["citizen1-water", "citizen2-water"], scoped);
    }

    // ---------------------------------------------------------------------------------
    // §9.4 / F9 — internal comments
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// F9: a Citizen never sees an internal comment. Asserted over the real
    /// <c>CommentQueryScope</c> so a regression in the production filter fails here, and
    /// over <c>IQueryable</c> so it is the same expression EF translates.
    /// </summary>
    [Fact]
    public void ACitizenNeverSeesAnInternalComment()
    {
        var comments = new[]
        {
            Comment("public note", isInternal: false),
            Comment("internal note", isInternal: true),
            Comment("another public note", isInternal: false),
        }.AsQueryable();

        var visible = CommentQueryScope
            .For(comments, For(UserRole.Citizen, departmentId: null))
            .Select(c => c.Body)
            .ToList();

        Assert.Equal(["public note", "another public note"], visible);
    }

    /// <summary>
    /// The other half: the filter must not fire for staff, or the internal note is useless.
    /// </summary>
    [Theory]
    [InlineData(UserRole.Staff)]
    [InlineData(UserRole.DeptAdmin)]
    public void StaffSeeEveryComment(UserRole role)
    {
        var comments = new[]
        {
            Comment("public note", isInternal: false),
            Comment("internal note", isInternal: true),
        }.AsQueryable();

        var visible = CommentQueryScope
            .For(comments, For(role, Guid.CreateVersion7()))
            .Select(c => c.Body)
            .ToList();

        Assert.Equal(["public note", "internal note"], visible);
    }

    // ---------------------------------------------------------------------------------
    // §9.2 — the translation, which is the half CLAUDE.md calls a merge blocker
    //
    // The seam above is thoroughly covered. What was not, until this section, is the step
    // after it: "the scoped query returned nothing" has to become a NotFoundException and
    // a 404. The pr-reviewer on #37 proved the gap by mutation — replacing every
    // `throw new NotFoundException` in the services with `ForbiddenException` left all 85
    // tests green. A 403 tells the caller the complaint exists, which is precisely the
    // disclosure §9.2 exists to prevent, and nothing in the repository would have noticed.
    //
    // These need a real PostgreSQL (§18): what is under test is a service composing the
    // seam with EF and a projection, not the seam on its own.
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Deliberately a month after <c>SlaSweeperTests.Noon</c> (2026-04-02).
    ///
    /// The suites share one database (#50) and <c>SlaSweeperTests</c> asserts against
    /// <c>SlaSweepResult.Examined</c>, which counts every complaint in it. A fixture whose
    /// SLA window overlapped the sweeper's clock would be selected by its <c>WHERE</c>
    /// clause and turn that assertion red from another file — which #50 has already caused
    /// twice. The gap is what keeps this suite invisible to it, so it is not a free choice.
    /// </summary>
    private static readonly DateTimeOffset Noon = new(2026, 5, 4, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Every service method that resolves a complaint by id through the scope — not only
    /// the three #38 named, because the invariant is about the set of them.
    ///
    /// It is a hand-maintained list and it does <b>not</b> enforce itself: a tenth scoped
    /// method added without a row here fails nothing. What it is, is a checklist a reviewer
    /// can diff against the services in one pass. Making it enforceable would mean a
    /// reflection test over every public method taking a <c>complaintId</c>, which is worth
    /// filing and is not worth smuggling into a test PR.
    /// </summary>
    public static readonly string[] ScopedEntryPoints =
    [
        "ComplaintService.GetAsync",
        "ComplaintService.GetHistoryAsync",
        "ComplaintTransitionService.TransitionAsync",
        "ComplaintTransitionService.ChangePriorityAsync",
        "CommentService.ListAsync",
        "CommentService.AddAsync",
        "AttachmentService.ListAsync",
        "AttachmentService.CreateReadUrlAsync",
        "AttachmentService.UploadAsync",
    ];

    /// <summary>The three shapes of "out of scope" the seam distinguishes (§9.3).</summary>
    public static readonly string[] OutOfScopeCallers =
    [
        "another citizen",
        "staff in another department",
        "a dept admin in another department",
    ];

    public static TheoryData<string> EntryPointCases()
    {
        var data = new TheoryData<string>();

        foreach (var entryPoint in ScopedEntryPoints)
        {
            data.Add(entryPoint);
        }

        return data;
    }

    public static TheoryData<string, string> OutOfScopeCases()
    {
        var data = new TheoryData<string, string>();

        foreach (var entryPoint in ScopedEntryPoints)
        {
            foreach (var caller in OutOfScopeCallers)
            {
                data.Add(entryPoint, caller);
            }
        }

        return data;
    }

    /// <summary>
    /// <b>CLAUDE.md non-negotiable 1.</b> A complaint outside the caller's scope throws
    /// <see cref="NotFoundException"/> — which the handler maps to <c>404</c> — and never
    /// <see cref="ForbiddenException"/>.
    ///
    /// <c>Assert.ThrowsAsync</c> is exact, so a service that switches to
    /// <c>ForbiddenException</c> fails here rather than passing on a base-type match.
    /// </summary>
    [RequiresPostgresTheory]
    [MemberData(nameof(OutOfScopeCases))]
    public async Task AnOutOfScopeComplaintIsNotFoundAndNever403(string entryPoint, string caller)
    {
        await using var db = postgres.CreateContext();
        var world = await ScopeWorld.CreateAsync(db);
        var complaint = await world.ComplaintAsync(db);

        await Assert.ThrowsAsync<NotFoundException>(
            () => world.InvokeAsync(entryPoint, db, world.Caller(caller), complaint.Id));
    }

    /// <summary>
    /// The invariant as the caller experiences it: an out-of-scope complaint and one that
    /// was never filed produce the same exception and the same message, once the two ids
    /// are normalised away.
    ///
    /// Today that holds <i>by construction</i> — the scoped query returns nothing in either
    /// case, so there is one code path and it cannot tell them apart. This test exists for
    /// the day somebody adds a second one. "Complaint X belongs to another department" is a
    /// kinder error and an obvious thing to want; it is also a disclosure, because the
    /// handler copies the message into the ProblemDetails <c>detail</c> field. That is the
    /// mutation this was confirmed against, and it is the only realistic way the property
    /// breaks.
    ///
    /// What it does <b>not</b> catch, because there is nothing there to catch: a reworded
    /// message that stays the same for both cases. A service answering "Complaint X is not
    /// yours" to out-of-scope and nonexistent alike reads badly and discloses nothing —
    /// verified, not assumed: that mutation leaves this green.
    /// </summary>
    [RequiresPostgresTheory]
    [MemberData(nameof(EntryPointCases))]
    public async Task AnOutOfScopeComplaintIsIndistinguishableFromOneThatDoesNotExist(
        string entryPoint)
    {
        await using var db = postgres.CreateContext();
        var world = await ScopeWorld.CreateAsync(db);
        var complaint = await world.ComplaintAsync(db);
        var stranger = world.Caller("another citizen");
        var neverExisted = Guid.CreateVersion7();

        var outOfScope = await Assert.ThrowsAsync<NotFoundException>(
            () => world.InvokeAsync(entryPoint, db, stranger, complaint.Id));

        var absent = await Assert.ThrowsAsync<NotFoundException>(
            () => world.InvokeAsync(entryPoint, db, stranger, neverExisted));

        Assert.Equal(
            absent.Message.Replace(neverExisted.ToString(), "{id}", StringComparison.Ordinal),
            outOfScope.Message.Replace(complaint.Id.ToString(), "{id}", StringComparison.Ordinal));
    }

    /// <summary>
    /// The control, and the reason the two theories above mean anything.
    ///
    /// A negative test's failure mode is that it silently stops testing: a world whose
    /// complaint was never written, or a scope that returns nothing for everybody, passes
    /// all thirty-six assertions above while proving nothing at all. So the callers who
    /// <i>are</i> in scope must get past the scope check.
    ///
    /// Past it, not through the whole method — most of these then fail for a reason that
    /// has nothing to do with scoping (the guard table refuses <c>Reopen</c> from
    /// <c>New</c>; the probe store refuses an upload). Anything but a
    /// <see cref="NotFoundException"/> means the scope let them in, which is the claim.
    /// </summary>
    [RequiresPostgresTheory]
    [InlineData("the owning citizen")]
    [InlineData("staff in the owning department")]
    [InlineData("the owning dept admin")]
    public async Task TheCallersWhoAreInScopeGetPastTheScopeCheck(string caller)
    {
        await using var db = postgres.CreateContext();
        var world = await ScopeWorld.CreateAsync(db);
        var complaint = await world.ComplaintAsync(db);

        foreach (var entryPoint in ScopedEntryPoints)
        {
            var exception = await Record.ExceptionAsync(
                () => world.InvokeAsync(entryPoint, db, world.Caller(caller), complaint.Id));

            Assert.False(
                exception is NotFoundException,
                $"{entryPoint} answered 404 to {caller}, who is in scope: {exception?.Message}");
        }
    }

    /// <summary>
    /// The other half of the translation, and the other single point of failure: the one
    /// <c>switch</c> arm that turns each exception into a status. Both halves have to hold
    /// — a service throwing the right exception into a handler that maps it to <c>403</c>
    /// is the same disclosure by a different route.
    /// </summary>
    [Theory]
    [InlineData("out of scope", StatusCodes.Status404NotFound)]
    [InlineData("a forbidden action on a visible complaint", StatusCodes.Status403Forbidden)]
    public async Task TheHandlerMapsScopeTo404AndActionTo403(string kind, int expected)
    {
        Exception thrown = kind == "out of scope"
            ? new NotFoundException("Complaint 'x' was not found.")
            : new ForbiddenException("Only staff may write an internal comment.");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddProblemDetails();

        await using var provider = services.BuildServiceProvider();

        var handler = new ProblemDetailsExceptionHandler(
            provider.GetRequiredService<IProblemDetailsService>(),
            provider.GetRequiredService<ILogger<ProblemDetailsExceptionHandler>>());

        var context = new DefaultHttpContext
        {
            RequestServices = provider,
            Response = { Body = new MemoryStream() },
        };

        await handler.TryHandleAsync(context, thrown, CancellationToken.None);

        Assert.Equal(expected, context.Response.StatusCode);
    }

    /// <summary>
    /// Two departments and six people, so "out of scope" has three distinct shapes and
    /// "in scope" has three matching ones. Unique keys throughout: the suites share one
    /// database (#50).
    /// </summary>
    private sealed class ScopeWorld
    {
        private const int SlaHours = 10;

        public required Guid DepartmentId { get; init; }

        public required Guid OtherDepartmentId { get; init; }

        public required Guid CategoryId { get; init; }

        public required Guid CitizenId { get; init; }

        public required Guid OtherCitizenId { get; init; }

        public required Guid StaffId { get; init; }

        public required Guid AdminId { get; init; }

        public required Guid ElsewhereStaffId { get; init; }

        public required Guid ElsewhereAdminId { get; init; }

        /// <summary>
        /// The attachment written by <see cref="ComplaintAsync"/>. Real, because
        /// <c>CreateReadUrlAsync</c> throws its own <c>404</c> for an attachment that does
        /// not exist — handing it an invented id would make the in-scope control fail for a
        /// reason that has nothing to do with scoping, and that is exactly what it did on
        /// the first run of this suite.
        /// </summary>
        public Guid AttachmentId { get; private set; }

        public static async Task<ScopeWorld> CreateAsync(ObhijogDbContext db)
        {
            var suffix = Guid.NewGuid().ToString("N")[..11].ToUpperInvariant();

            var department = NewDepartment($"Scope Department {suffix}", $"S{suffix}");
            var elsewhere = NewDepartment($"Other Department {suffix}", $"O{suffix}");

            var category = new ComplaintCategory
            {
                Id = Guid.CreateVersion7(),
                Name = $"Scope category {suffix}",
                DepartmentId = department.Id,
                SlaHours = SlaHours,
                DefaultPriority = ComplaintPriority.Normal,
                IsActive = true,
            };

            db.Departments.AddRange(department, elsewhere);
            db.ComplaintCategories.Add(category);

            var citizen = NewUser(UserRole.Citizen, null);
            var otherCitizen = NewUser(UserRole.Citizen, null);
            var staff = NewUser(UserRole.Staff, department.Id);
            var admin = NewUser(UserRole.DeptAdmin, department.Id);
            var elsewhereStaff = NewUser(UserRole.Staff, elsewhere.Id);
            var elsewhereAdmin = NewUser(UserRole.DeptAdmin, elsewhere.Id);

            db.Users.AddRange(citizen, otherCitizen, staff, admin, elsewhereStaff, elsewhereAdmin);

            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            return new ScopeWorld
            {
                DepartmentId = department.Id,
                OtherDepartmentId = elsewhere.Id,
                CategoryId = category.Id,
                CitizenId = citizen.Id,
                OtherCitizenId = otherCitizen.Id,
                StaffId = staff.Id,
                AdminId = admin.Id,
                ElsewhereStaffId = elsewhereStaff.Id,
                ElsewhereAdminId = elsewhereAdmin.Id,
            };
        }

        /// <summary>
        /// One complaint, owned by <see cref="CitizenId"/> and filed against
        /// <see cref="DepartmentId"/>, with one attachment — <c>CreateReadUrlAsync</c>
        /// throws its own <c>404</c> for a missing attachment, which would make the
        /// in-scope control fail for a reason that is not about scoping at all.
        /// </summary>
        public async Task<Complaint> ComplaintAsync(ObhijogDbContext db)
        {
            var created = Noon.AddHours(-1);

            var complaint = new Complaint
            {
                Id = Guid.CreateVersion7(),
                ReferenceNumber = $"MC-SC-{Guid.NewGuid():N}"[..20],
                CitizenId = CitizenId,
                CategoryId = CategoryId,
                DepartmentId = DepartmentId,
                Title = "Scope fixture",
                Description = "A complaint that exists to be refused.",
                Status = ComplaintStatus.New,
                Priority = ComplaintPriority.Normal,
                Latitude = 23.75m,
                Longitude = 90.39m,
                CreatedAt = created,
                SlaDueAt = SlaPolicy.DueAt(created, SlaHours),
            };

            var attachment = new ComplaintAttachment
            {
                Id = Guid.CreateVersion7(),
                ComplaintId = complaint.Id,
                BlobName = $"{complaint.Id}/{Guid.CreateVersion7()}.png",
                OriginalFileName = "photo.png",
                ContentType = "image/png",
                SizeBytes = 1024,
                UploadedById = CitizenId,
                UploadedAt = created,
            };

            db.Complaints.Add(complaint);
            db.ComplaintAttachments.Add(attachment);

            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            AttachmentId = attachment.Id;

            return complaint;
        }

        public ICurrentUser Caller(string description) => description switch
        {
            "the owning citizen" => new TestCurrentUser(CitizenId, UserRole.Citizen),
            "another citizen" => new TestCurrentUser(OtherCitizenId, UserRole.Citizen),
            "staff in the owning department" =>
                new TestCurrentUser(StaffId, UserRole.Staff, DepartmentId),
            "the owning dept admin" =>
                new TestCurrentUser(AdminId, UserRole.DeptAdmin, DepartmentId),
            "staff in another department" =>
                new TestCurrentUser(ElsewhereStaffId, UserRole.Staff, OtherDepartmentId),
            "a dept admin in another department" =>
                new TestCurrentUser(ElsewhereAdminId, UserRole.DeptAdmin, OtherDepartmentId),
            _ => throw new ArgumentOutOfRangeException(nameof(description), description, null),
        };

        /// <summary>
        /// Calls one scoped entry point by name. Every argument beyond the id is chosen to
        /// be irrelevant: the scope check is the first statement in each of these methods,
        /// so an out-of-scope caller never reaches anything the arguments could affect —
        /// and if a refactor ever moves the check later, these tests are how that is found.
        /// </summary>
        public Task InvokeAsync(
            string entryPoint,
            ObhijogDbContext db,
            ICurrentUser user,
            Guid complaintId)
        {
            var clock = new TestClock(Noon);
            var store = new ScopeProbeStore();

            AttachmentService Attachments() => new(
                db,
                store,
                user,
                Microsoft.Extensions.Options.Options.Create(new AttachmentOptions()),
                clock);

            ComplaintTransitionService Transitions() =>
                new(db, new ComplaintService(db, user, clock), user, clock);

            return entryPoint switch
            {
                "ComplaintService.GetAsync" =>
                    new ComplaintService(db, user, clock).GetAsync(complaintId),

                "ComplaintService.GetHistoryAsync" =>
                    new ComplaintService(db, user, clock).GetHistoryAsync(complaintId),

                // Reopen from New has no guard-table row, so an in-scope caller fails with
                // InvalidTransitionException and the complaint is never mutated. The point
                // is only to reach the service; the action is deliberately a dead end.
                "ComplaintTransitionService.TransitionAsync" =>
                    Transitions().TransitionAsync(
                        complaintId,
                        new TransitionRequest { Action = ComplaintAction.Reopen }),

                "ComplaintTransitionService.ChangePriorityAsync" =>
                    Transitions().ChangePriorityAsync(complaintId, ComplaintPriority.High),

                "CommentService.ListAsync" =>
                    new CommentService(db, user, clock).ListAsync(complaintId),

                "CommentService.AddAsync" =>
                    new CommentService(db, user, clock).AddAsync(
                        complaintId,
                        new CreateCommentRequest { Body = "A comment nobody may add." }),

                "AttachmentService.ListAsync" => Attachments().ListAsync(complaintId),

                "AttachmentService.CreateReadUrlAsync" =>
                    Attachments().CreateReadUrlAsync(complaintId, AttachmentId),

                "AttachmentService.UploadAsync" =>
                    Attachments().UploadAsync(
                        complaintId,
                        new AttachmentUpload("photo.png", "image/png", 1024, new MemoryStream([1]))),

                _ => throw new ArgumentOutOfRangeException(nameof(entryPoint), entryPoint, null),
            };
        }

        private static Department NewDepartment(string name, string code) => new()
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            Code = code,
        };

        private static User NewUser(UserRole role, Guid? departmentId)
        {
            var email = $"scope-{Guid.CreateVersion7():N}@example.test";

            return new User
            {
                Id = Guid.CreateVersion7(),
                FullName = $"Scope {role}",
                Email = email,
                NormalizedEmail = email.ToUpperInvariant(),
                UserName = email,
                NormalizedUserName = email.ToUpperInvariant(),
                SecurityStamp = Guid.CreateVersion7().ToString(),
                Role = role,
                DepartmentId = departmentId,
                IsActive = true,
                CreatedAt = Noon,
            };
        }
    }

    /// <summary>
    /// A blob store that refuses to be written to.
    ///
    /// An out-of-scope upload must be refused with nothing having reached storage. Throwing
    /// here is what proves it: move the scope check below the upload and the theory fails
    /// with an <see cref="InvalidOperationException"/> instead of the expected
    /// <c>NotFoundException</c>.
    ///
    /// That is the whole of the claim, and it is narrower than "the scope check is the first
    /// statement in the method". The theory uploads a valid 1024-byte PNG, so it sails
    /// through the content-type, size and empty-file guards either way — the reviewer on #56
    /// moved the check below all three and nothing went red. Deliberately not pinned: an
    /// out-of-scope caller sending a bad content type would get <c>415</c>, but so would a
    /// caller naming a complaint that never existed, so the two stay indistinguishable and
    /// §9.2 is untouched. A test for that ordering would pin a preference, not an invariant.
    ///
    /// <c>CreateReadUrl</c> answers normally, because the read paths call it for attachments
    /// the caller is entitled to and it is not what is under test.
    /// </summary>
    private sealed class ScopeProbeStore : IAttachmentStore
    {
        public Task UploadAsync(
            string blobName,
            Stream content,
            string contentType,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "The blob store was reached. The scope check must come first (§9.2).");

        public Uri CreateReadUrl(string blobName) =>
            new($"https://blobs.example.test/{blobName}");

        public Task DeleteAsync(string blobName, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "The blob store was reached. The scope check must come first (§9.2).");
    }

    private static ComplaintComment Comment(string body, bool isInternal) => new()
    {
        Id = Guid.CreateVersion7(),
        ComplaintId = Guid.CreateVersion7(),
        AuthorId = Guid.CreateVersion7(),
        Body = body,
        IsInternal = isInternal,
    };

    private static Complaint Complaint(Guid citizenId, Guid departmentId, string title) => new()
    {
        Id = Guid.CreateVersion7(),
        ReferenceNumber = $"MC-2026-{Random.Shared.Next(1, 999999):D6}",
        CitizenId = citizenId,
        DepartmentId = departmentId,
        CategoryId = Guid.CreateVersion7(),
        Title = title,
        Description = "irrelevant to scoping",
    };

    private static HttpContextCurrentUser For(UserRole role, Guid? departmentId, Guid id)
    {
        var claims = new List<Claim>
        {
            new("sub", id.ToString()),
            new(TokenService.RoleClaim, role.ToString()),
        };

        if (departmentId is { } department)
        {
            claims.Add(new Claim(TokenService.DepartmentClaim, department.ToString()));
        }

        return new HttpContextCurrentUser(Accessor(new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test")),
        }));
    }

    private static HttpContextCurrentUser For(UserRole role, Guid? departmentId)
    {
        var claims = new List<Claim>
        {
            new("sub", Guid.CreateVersion7().ToString()),
            new("email", $"{role}@example.test"),
            new(TokenService.RoleClaim, role.ToString()),
        };

        if (departmentId is { } id)
        {
            claims.Add(new Claim(TokenService.DepartmentClaim, id.ToString()));
        }

        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test")),
        };

        return new HttpContextCurrentUser(Accessor(context));
    }

    private static IHttpContextAccessor Accessor(HttpContext context) =>
        new HttpContextAccessor { HttpContext = context };
}
