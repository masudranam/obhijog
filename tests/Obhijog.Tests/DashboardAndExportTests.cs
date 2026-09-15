using Microsoft.EntityFrameworkCore;
using Obhijog.Domain.Complaints;
using Obhijog.Domain.Departments;
using Obhijog.Domain.Sla;
using Obhijog.Domain.Users;
using Obhijog.Infrastructure.Complaints;
using Obhijog.Infrastructure.Dashboard;
using Obhijog.Infrastructure.Export;
using Obhijog.Infrastructure.Identity;
using Obhijog.Infrastructure.Persistence;
using Xunit;

namespace Obhijog.Tests;

/// <summary>
/// M8's two acceptance criteria that cannot be checked by reading the code. F13, F14.
///
/// <b>This is a sixth suite, and SPEC.md §18 asks for the reason.</b> Two of M8's criteria
/// are stated as tests rather than as behaviour:
///
/// <list type="bullet">
/// <item>"Every figure matches a hand count over the seed data." A dashboard of ten
/// aggregates is exactly the kind of code that is wrong in one cell and plausible
/// everywhere — <c>escalatedLevel1</c> counting closed complaints, <c>dueNext24h</c>
/// including everything already overdue — and none of it is visible from outside.</item>
/// <item>"CSV injection neutralised … a test covers a title beginning with <c>=</c>." That
/// one is a security control. Its failure mode is a spreadsheet executing a citizen's
/// complaint title on a clerk's machine, and nothing else in the repository would notice
/// if the escape were dropped.</item>
/// </list>
///
/// The CSV cases run without a database. The rest need a real PostgreSQL, per §18.
/// </summary>
[Collection(DashboardAndExportTests.Serialised)]
public class DashboardAndExportTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    public const string Serialised = "dashboard-export";

    private static readonly DateTimeOffset Noon = new(2026, 5, 4, 12, 0, 0, TimeSpan.Zero);

    // -----------------------------------------------------------------------------------
    // F14 — CSV injection. Pure, so no database.
    // -----------------------------------------------------------------------------------

    /// <summary>
    /// <b>The criterion M8 names explicitly.</b> A leading <c>=</c>, <c>+</c>, <c>-</c> or
    /// <c>@</c> is what Excel, LibreOffice and Google Sheets all read as "this cell is a
    /// formula, evaluate it on open". A complaint title is free text a citizen types, so
    /// without this the export hands attacker-controlled code to whatever the department
    /// opens the file with.
    /// </summary>
    [Theory]
    [InlineData("=HYPERLINK(\"http://evil.test\",\"Click\")", "\"'=HYPERLINK(\"\"http://evil.test\"\",\"\"Click\"\")\"")]
    [InlineData("=1+1", "\"'=1+1\"")]
    [InlineData("+44 7700 900000", "\"'+44 7700 900000\"")]
    [InlineData("-5 degrees", "\"'-5 degrees\"")]
    [InlineData("@channel", "\"'@channel\"")]
    public void AFormulaIsNeutralised(string input, string expected)
    {
        Assert.Equal(expected, Csv.Field(input));
    }

    /// <summary>
    /// Leading whitespace does not smuggle one past. A spreadsheet trims before deciding
    /// whether a cell is a formula, so a check on the raw first character would pass this
    /// test input and still execute in Excel.
    /// </summary>
    [Theory]
    [InlineData("   =cmd|'/c calc'!A1")]
    [InlineData("\t=1+1")]
    [InlineData("\n@SUM(1,2)")]
    public void LeadingWhitespaceDoesNotHideAFormula(string input)
    {
        var field = Csv.Field(input);

        Assert.StartsWith("\"'", field, StringComparison.Ordinal);
    }

    /// <summary>Ordinary text is left alone beyond the quoting every field gets.</summary>
    [Theory]
    [InlineData("Broken streetlight", "\"Broken streetlight\"")]
    [InlineData("Water leak — main", "\"Water leak — main\"")]
    [InlineData("", "\"\"")]
    [InlineData(null, "\"\"")]
    public void OrdinaryTextIsOnlyQuoted(string? input, string expected)
    {
        Assert.Equal(expected, Csv.Field(input));
    }

    /// <summary>RFC 4180: an embedded quote is doubled, not escaped with a backslash.</summary>
    [Fact]
    public void AnEmbeddedQuoteIsDoubled()
    {
        Assert.Equal("\"He said \"\"fix it\"\" twice\"", Csv.Field("He said \"fix it\" twice"));
    }

    /// <summary>
    /// A multi-line description becomes one line. A description is free text and newlines
    /// in it are the common case, not the exotic one — left in, they turn one complaint into
    /// several rows in tools that parse quoted newlines badly.
    /// </summary>
    [Fact]
    public void NewlinesAreFlattenedIntoOneField()
    {
        var field = Csv.Field("Line one\r\nLine two\n\nLine three");

        Assert.Equal("\"Line one Line two Line three\"", field);
        Assert.DoesNotContain('\n', field);
        Assert.DoesNotContain('\r', field);
    }

    /// <summary>Every field is quoted, so a comma in the data can never become a separator.</summary>
    [Fact]
    public void ACommaInAFieldDoesNotBecomeASeparator()
    {
        Assert.Equal("\"Dhaka, Bangladesh\",\"ok\"", Csv.Row("Dhaka, Bangladesh", "ok"));
    }

    /// <summary>The filename carries no caller-supplied text into a response header.</summary>
    [Fact]
    public void TheExportFileNameIsDated()
    {
        Assert.Equal("complaints-2026-05-04.csv", ComplaintExportService.FileName(Noon));
    }

    // -----------------------------------------------------------------------------------
    // F13 — the dashboard, against a hand count
    // -----------------------------------------------------------------------------------

    /// <summary>
    /// <b>M8's Definition of Done: every figure matches a hand count.</b>
    ///
    /// The fixture is nine complaints chosen so that no two figures have the same answer by
    /// accident — see <see cref="World.NineComplaintsAsync"/>, where each one is commented
    /// with the buckets it belongs to. Every expectation below was counted by hand from that
    /// list, not read off a first run.
    /// </summary>
    [RequiresPostgresFact]
    public async Task EveryDashboardFigureMatchesAHandCount()
    {
        await using var db = postgres.CreateContext();
        var world = await World.CreateAsync(db);
        await world.NineComplaintsAsync(db);

        var summary = await world.Dashboard(db, world.AdminId, UserRole.DeptAdmin).GetAsync();

        //  New(1), New(2), warned(3), breached L1(4), breached L2(5)
        Assert.Equal(5, summary.TotalOpen);

        Assert.Equal(3, summary.ByStatus[ComplaintStatus.New]);
        Assert.Equal(1, summary.ByStatus[ComplaintStatus.Assigned]);
        Assert.Equal(1, summary.ByStatus[ComplaintStatus.InProgress]);
        Assert.Equal(2, summary.ByStatus[ComplaintStatus.Resolved]);
        Assert.Equal(1, summary.ByStatus[ComplaintStatus.Closed]);
        Assert.Equal(1, summary.ByStatus[ComplaintStatus.Rejected]);

        // All six present, always — a status with no complaints is a zero, not a gap.
        Assert.Equal(6, summary.ByStatus.Count);
        Assert.Equal(9, summary.ByStatus.Values.Sum());

        Assert.Equal(1, summary.WarningOpen);
        Assert.Equal(2, summary.BreachedOpen);

        // Due at Noon+9h, Noon+9h and Noon+1h. The two already overdue are *not* here.
        Assert.Equal(3, summary.DueNext24h);

        Assert.Equal(1, summary.EscalatedLevel1);
        Assert.Equal(1, summary.EscalatedLevel2);

        // Resolved 5h and 20h ago inside the window; the 40-day-old one is outside it.
        Assert.Equal(2, summary.ResolvedLast30Days);
        Assert.Equal(12.5, summary.AvgResolutionHours);

        // One of the two met its deadline.
        Assert.Equal(50.0, summary.SlaCompliancePct);
    }

    /// <summary>
    /// F13: "A Citizen sees the same shape computed over their own complaints only."
    ///
    /// The same nine complaints, read as the citizen who filed three of them. Nothing in
    /// <c>DashboardService</c> branches on role — this is <c>ComplaintQueryScope.For</c>
    /// doing its one job (§9.3).
    /// </summary>
    [RequiresPostgresFact]
    public async Task ACitizenSeesTheSameShapeOverTheirOwnComplaintsOnly()
    {
        await using var db = postgres.CreateContext();
        var world = await World.CreateAsync(db);
        await world.NineComplaintsAsync(db);

        var summary = await world.Dashboard(db, world.OtherCitizenId, UserRole.Citizen).GetAsync();

        // The other citizen filed exactly one: the level-2 breach.
        Assert.Equal(1, summary.TotalOpen);
        Assert.Equal(1, summary.BreachedOpen);
        Assert.Equal(1, summary.EscalatedLevel2);
        Assert.Equal(0, summary.EscalatedLevel1);
        Assert.Equal(0, summary.ResolvedLast30Days);

        // Still the full shape: six keys, not the subset that happened to be non-zero.
        Assert.Equal(6, summary.ByStatus.Count);
        Assert.Equal(1, summary.ByStatus.Values.Sum());
    }

    /// <summary>
    /// An empty scope is a dashboard of zeros, not a 500 and not a null. <c>GroupBy</c>
    /// yields no row at all for an empty set, which is the trap this pins.
    /// </summary>
    [RequiresPostgresFact]
    public async Task ACallerWithNoComplaintsGetsZeros()
    {
        await using var db = postgres.CreateContext();
        var world = await World.CreateAsync(db);
        await world.NineComplaintsAsync(db);

        var stranger = Guid.CreateVersion7();
        var summary = await world.Dashboard(db, stranger, UserRole.Citizen).GetAsync();

        Assert.Equal(0, summary.TotalOpen);
        Assert.Equal(6, summary.ByStatus.Count);
        Assert.All(summary.ByStatus.Values, count => Assert.Equal(0, count));

        // Null, not zero: "nothing was resolved" is not "everything missed its SLA".
        Assert.Null(summary.AvgResolutionHours);
        Assert.Null(summary.SlaCompliancePct);
    }

    /// <summary>
    /// §11.1: the SLA is measured to <c>ResolvedAt</c>, not <c>ClosedAt</c>. A complaint
    /// resolved inside its window and closed later still counts as met — so the compliance
    /// figure must not be computed over open-status complaints or skip closed ones.
    /// </summary>
    [RequiresPostgresFact]
    public async Task ComplianceCountsAClosedComplaintByWhenItWasResolved()
    {
        await using var db = postgres.CreateContext();
        var world = await World.CreateAsync(db);

        // Closed last week, but resolved five hours into a ten-hour window.
        await world.ComplaintAsync(
            db,
            status: ComplaintStatus.Closed,
            createdAt: Noon.AddDays(-6),
            resolvedAt: Noon.AddDays(-6).AddHours(5),
            closedAt: Noon.AddDays(-1));

        var summary = await world.Dashboard(db, world.AdminId, UserRole.DeptAdmin).GetAsync();

        Assert.Equal(1, summary.ResolvedLast30Days);
        Assert.Equal(5, summary.AvgResolutionHours);
        Assert.Equal(100.0, summary.SlaCompliancePct);
    }

    // -----------------------------------------------------------------------------------
    // F14 — the export shares the list's scope and filters
    // -----------------------------------------------------------------------------------

    /// <summary>
    /// F14: "applies the <b>same</b> filter and scope code as the list endpoint."
    ///
    /// Asserted as an equality rather than by inspection: whatever the list returns for a
    /// query, the export contains those references and no others. A divergence in either
    /// direction fails — the export cannot leak a row the list withholds, and cannot drop
    /// one the list shows.
    /// </summary>
    [RequiresPostgresTheory]
    [InlineData(null)]
    [InlineData("New")]
    [InlineData("Resolved")]
    public async Task TheExportReturnsExactlyWhatTheListReturns(string? status)
    {
        await using var db = postgres.CreateContext();
        var world = await World.CreateAsync(db);
        await world.NineComplaintsAsync(db);

        var query = new ComplaintListQuery
        {
            Status = status is null ? null : [Enum.Parse<ComplaintStatus>(status)],
            PageSize = ComplaintListQuery.MaxPageSize,
        };

        var listed = await world.Complaints(db, world.AdminId, UserRole.DeptAdmin)
            .ListAsync(query);

        var csv = await world.Export(db, world.AdminId, UserRole.DeptAdmin).WriteAsync(query);

        var exported = References(csv);

        Assert.Equal(
            listed.Items.Select(i => i.ReferenceNumber).OrderBy(r => r).ToArray(),
            exported.OrderBy(r => r).ToArray());

        Assert.NotEmpty(exported);
    }

    /// <summary>
    /// The scope half of the same claim: whoever the caller is, the export has no query of
    /// its own and therefore no "export everything" path (§9.3).
    ///
    /// The Citizen case is defence in depth rather than a reachable flow — §13.2 puts
    /// <c>Policies.DeptAdmin</c> on the route, so a Citizen gets a <c>403</c> before the
    /// service is called. It is asserted anyway because the policy and the scope are two
    /// different guards, and the day the route widens, the scope has to already be right.
    /// </summary>
    [RequiresPostgresFact]
    public async Task AnExportIsScopedToTheCallerLikeEveryOtherRead()
    {
        await using var db = postgres.CreateContext();
        var world = await World.CreateAsync(db);
        await world.NineComplaintsAsync(db);

        var admin = await world.Export(db, world.AdminId, UserRole.DeptAdmin)
            .WriteAsync(new ComplaintListQuery());

        var citizen = await world.Export(db, world.OtherCitizenId, UserRole.Citizen)
            .WriteAsync(new ComplaintListQuery());

        Assert.Equal(9, References(admin).Length);
        Assert.Single(References(citizen));

        // And a Dept Admin of *another* department gets nothing at all, rather than an
        // error — out of scope is absence, never refusal (§9.2).
        var outsider = await world
            .Export(db, Guid.CreateVersion7(), UserRole.DeptAdmin, Guid.CreateVersion7())
            .WriteAsync(new ComplaintListQuery());

        Assert.Empty(References(outsider));
    }

    /// <summary>
    /// A header row even when there are no complaints. An empty file and a file of zero
    /// complaints look identical otherwise, and one of those is a bug report.
    /// </summary>
    [RequiresPostgresFact]
    public async Task AnEmptyExportStillCarriesItsHeader()
    {
        await using var db = postgres.CreateContext();
        var world = await World.CreateAsync(db);

        var csv = await world
            .Export(db, Guid.CreateVersion7(), UserRole.DeptAdmin, Guid.CreateVersion7())
            .WriteAsync(new ComplaintListQuery());

        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Single(lines);
        Assert.StartsWith("\"reference\",\"title\"", lines[0], StringComparison.Ordinal);
    }

    /// <summary>The F14 column set, in the order F14 names it.</summary>
    [RequiresPostgresFact]
    public async Task TheHeaderIsTheColumnSetSpecNames()
    {
        await using var db = postgres.CreateContext();
        var world = await World.CreateAsync(db);

        var csv = await world.Export(db, world.AdminId, UserRole.DeptAdmin)
            .WriteAsync(new ComplaintListQuery());

        Assert.Equal(
            "\"reference\",\"title\",\"category\",\"department\",\"status\",\"priority\","
            + "\"assignee\",\"created\",\"due\",\"resolved\",\"breached\",\"escalation level\"",
            csv.Split("\r\n")[0]);
    }

    /// <summary>
    /// End to end: a complaint whose title is a formula comes out of the real export
    /// neutralised. The unit cases above prove <c>Csv.Field</c> escapes; this proves the
    /// export actually routes the title through it.
    /// </summary>
    [RequiresPostgresFact]
    public async Task AFormulaTitleIsNeutralisedInTheRealExport()
    {
        await using var db = postgres.CreateContext();
        var world = await World.CreateAsync(db);

        await world.ComplaintAsync(db, title: "=cmd|'/c calc'!A1");

        var csv = await world.Export(db, world.AdminId, UserRole.DeptAdmin)
            .WriteAsync(new ComplaintListQuery());

        Assert.Contains("\"'=cmd|'/c calc'!A1\"", csv, StringComparison.Ordinal);
        Assert.DoesNotContain(",\"=cmd", csv, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------------------

    /// <summary>The reference column of every data row, header skipped.</summary>
    private static string[] References(string csv) =>
        csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Select(line => line.Split(',')[0].Trim('"'))
            .ToArray();

    /// <summary>
    /// One department, its people, and the complaints a test needs. Unique keys throughout,
    /// so a shared database never makes two tests interfere.
    /// </summary>
    private sealed class World
    {
        private const int SlaHours = 10;

        public required TestClock Clock { get; init; }

        public required Guid DepartmentId { get; init; }

        public required Guid CategoryId { get; init; }

        public required Guid AdminId { get; init; }

        public required Guid StaffId { get; init; }

        public required Guid CitizenId { get; init; }

        /// <summary>A second citizen, so "their own complaints only" has something to exclude.</summary>
        public required Guid OtherCitizenId { get; init; }

        public static async Task<World> CreateAsync(ObhijogDbContext db)
        {
            var suffix = Guid.NewGuid().ToString("N")[..11].ToUpperInvariant();

            var department = new Department
            {
                Id = Guid.CreateVersion7(),
                Name = $"Dashboard Department {suffix}",
                Code = $"D{suffix}",
            };

            var category = new ComplaintCategory
            {
                Id = Guid.CreateVersion7(),
                Name = $"Dashboard category {suffix}",
                DepartmentId = department.Id,
                SlaHours = SlaHours,
                DefaultPriority = ComplaintPriority.Normal,
                IsActive = true,
            };

            db.Departments.Add(department);
            db.ComplaintCategories.Add(category);

            var admin = User(UserRole.DeptAdmin, department.Id);
            var staff = User(UserRole.Staff, department.Id);
            var citizen = User(UserRole.Citizen, null);
            var otherCitizen = User(UserRole.Citizen, null);

            db.Users.AddRange(admin, staff, citizen, otherCitizen);

            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            return new World
            {
                Clock = new TestClock(Noon),
                DepartmentId = department.Id,
                CategoryId = category.Id,
                AdminId = admin.Id,
                StaffId = staff.Id,
                CitizenId = citizen.Id,
                OtherCitizenId = otherCitizen.Id,
            };
        }

        /// <summary>
        /// The fixture the hand count is taken from. Nine complaints, each annotated with
        /// the buckets it lands in, so the expectations in the test can be checked against
        /// this list by reading rather than by running.
        /// </summary>
        public async Task NineComplaintsAsync(ObhijogDbContext db)
        {
            // 1, 2 — New, open, untouched by the sweeper. Due in 9h: dueNext24h.
            await ComplaintAsync(db, createdAt: Noon.AddHours(-1));
            await ComplaintAsync(db, createdAt: Noon.AddHours(-1));

            // 3 — Assigned and warned, not breached. warningOpen, and due in 1h.
            await ComplaintAsync(
                db,
                status: ComplaintStatus.Assigned,
                createdAt: Noon.AddHours(-9),
                assigned: true,
                warnedAt: Noon.AddHours(-1));

            // 4 — InProgress, breached, level 1. Overdue, so NOT dueNext24h.
            await ComplaintAsync(
                db,
                status: ComplaintStatus.InProgress,
                createdAt: Noon.AddHours(-11),
                assigned: true,
                warnedAt: Noon.AddHours(-3),
                breachedAt: Noon.AddHours(-1),
                escalationLevel: 1);

            // 5 — New, breached, level 2, filed by the *other* citizen.
            await ComplaintAsync(
                db,
                createdAt: Noon.AddHours(-12),
                citizenId: OtherCitizenId,
                warnedAt: Noon.AddHours(-4),
                breachedAt: Noon.AddHours(-2),
                escalationLevel: 2);

            // 6 — Resolved 5h into a 10h window, three days ago. Met, inside 30 days.
            await ComplaintAsync(
                db,
                status: ComplaintStatus.Resolved,
                createdAt: Noon.AddDays(-3),
                resolvedAt: Noon.AddDays(-3).AddHours(5));

            // 7 — Closed, resolved 20h into a 10h window, five days ago. Missed, inside 30.
            await ComplaintAsync(
                db,
                status: ComplaintStatus.Closed,
                createdAt: Noon.AddDays(-5),
                resolvedAt: Noon.AddDays(-5).AddHours(20),
                closedAt: Noon.AddDays(-4));

            // 8 — Resolved 40 days ago. Counted in byStatus, outside the 30-day window.
            await ComplaintAsync(
                db,
                status: ComplaintStatus.Resolved,
                createdAt: Noon.AddDays(-41),
                resolvedAt: Noon.AddDays(-40));

            // 9 — Rejected. Never open, never resolved.
            await ComplaintAsync(db, status: ComplaintStatus.Rejected, createdAt: Noon.AddDays(-2));
        }

        public async Task<Complaint> ComplaintAsync(
            ObhijogDbContext db,
            ComplaintStatus status = ComplaintStatus.New,
            DateTimeOffset? createdAt = null,
            bool assigned = false,
            Guid? citizenId = null,
            string? title = null,
            DateTimeOffset? warnedAt = null,
            DateTimeOffset? breachedAt = null,
            short escalationLevel = 0,
            DateTimeOffset? resolvedAt = null,
            DateTimeOffset? closedAt = null)
        {
            var created = createdAt ?? Noon.AddHours(-1);

            var complaint = new Complaint
            {
                Id = Guid.CreateVersion7(),
                ReferenceNumber = $"MC-DX-{Guid.NewGuid():N}"[..20],
                CitizenId = citizenId ?? CitizenId,
                CategoryId = CategoryId,
                DepartmentId = DepartmentId,
                Title = title ?? "Dashboard fixture",
                Description = "A complaint that exists to be counted.",
                Status = status,
                Priority = ComplaintPriority.Normal,
                Latitude = 23.75m,
                Longitude = 90.39m,
                AssignedStaffId = assigned ? StaffId : null,
                CreatedAt = created,
                SlaDueAt = SlaPolicy.DueAt(created, SlaHours),
                SlaWarnedAt = warnedAt,
                SlaBreachedAt = breachedAt,
                EscalationLevel = escalationLevel,
                ResolvedAt = resolvedAt,
                ClosedAt = closedAt,

                // The §8.4 check constraints require the text each status implies.
                ResolutionNote = status is ComplaintStatus.Resolved or ComplaintStatus.Closed
                    ? "Resolved for the fixture."
                    : null,
                RejectionReason = status == ComplaintStatus.Rejected
                    ? "Rejected for the fixture."
                    : null,
            };

            db.Complaints.Add(complaint);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            return complaint;
        }

        /// <summary>
        /// <paramref name="departmentId"/> defaults to this world's department. Pass a
        /// different one to act as somebody from elsewhere — a Citizen's is ignored either
        /// way, because §10.2 says a Citizen has no <c>dept</c> claim at all.
        /// </summary>
        public DashboardService Dashboard(
            ObhijogDbContext db,
            Guid userId,
            UserRole role,
            Guid? departmentId = null) =>
            new(db, new TestCurrentUser(userId, role, departmentId ?? DepartmentId), Clock);

        public ComplaintService Complaints(
            ObhijogDbContext db,
            Guid userId,
            UserRole role,
            Guid? departmentId = null) =>
            new(db, new TestCurrentUser(userId, role, departmentId ?? DepartmentId), Clock);

        public ComplaintExportService Export(
            ObhijogDbContext db,
            Guid userId,
            UserRole role,
            Guid? departmentId = null) =>
            new(db, Complaints(db, userId, role, departmentId));

        private static User User(UserRole role, Guid? departmentId)
        {
            var email = $"dash-{Guid.CreateVersion7():N}@example.test";

            return new User
            {
                Id = Guid.CreateVersion7(),
                FullName = $"Dashboard {role}",
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
}
