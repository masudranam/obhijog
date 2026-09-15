using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Obhijog.Domain.Complaints;
using Obhijog.Domain.Departments;
using Obhijog.Domain.Notifications;
using Obhijog.Domain.Sla;
using Obhijog.Domain.Users;
using Obhijog.Infrastructure.Auth;
using Obhijog.Infrastructure.Complaints;
using Obhijog.Infrastructure.Identity;
using Obhijog.Infrastructure.Notifications;
using Obhijog.Infrastructure.Options;
using Obhijog.Infrastructure.Persistence;
using Obhijog.Infrastructure.Sla;
using Xunit;

namespace Obhijog.Tests;

/// <summary>
/// SPEC.md §18's sweep-idempotency suite — the one the project's Definition of Done for M7
/// singles out: <b>a second sweep changes nothing</b>.
///
/// <b>Against a real PostgreSQL, never the in-memory provider.</b> §18 is explicit, and the
/// reason is this file: the in-memory provider has no transactions, no <c>xmin</c> and no
/// unique-index enforcement, so it would pass every case here while the three defences of
/// §11.3 were silently absent. A suite that cannot fail is worse than no suite.
///
/// Every test builds its own department, category and users with unique keys, so it asserts
/// on <b>its own complaint's rows</b> rather than on global counts — the sweep is
/// system-wide by nature and any test that counted every escalation in the database would
/// be flaky the moment a second test ran.
/// </summary>
[Collection(SlaSweeperTests.Serialised)]
public class SlaSweeperTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    /// <summary>
    /// The sweep is system-wide, so two of these running at once would each see the other's
    /// complaints. Only the whole-database assertions actually need this, but a sweep that
    /// escalated another test's fixture mid-assertion would be a confusing failure to chase.
    /// </summary>
    public const string Serialised = "sla-sweep";

    private static readonly DateTimeOffset Noon = new(2026, 4, 2, 12, 0, 0, TimeSpan.Zero);

    // -----------------------------------------------------------------------------------
    // The ladder — §11.2
    // -----------------------------------------------------------------------------------

    /// <summary>80% elapsed and still open: warned, but not breached and not escalated.</summary>
    [RequiresPostgresFact]
    public async Task WarnsAtEightyPercentWithoutBreachingOrEscalating()
    {
        await using var db = postgres.CreateContext();
        var world = await World.CreateAsync(db);
        var complaint = await world.ComplaintAsync(db, elapsedPercent: 90, assigned: true);

        await world.Sweeper(db).SweepAsync();

        var swept = await Reload(db, complaint.Id);

        Assert.Equal(Noon, swept.SlaWarnedAt);
        Assert.Null(swept.SlaBreachedAt);
        Assert.Equal(0, swept.EscalationLevel);

        // A warning is not a rung of the escalation ladder — it is the notice that one is
        // coming — so it writes no EscalationEvent.
        Assert.Empty(await EscalationsFor(db, complaint.Id));

        // Assigned, so §11.2 sends the warning to the one person holding it and to nobody
        // else — the department's admins hear about it at the breach, not before.
        var notice = Assert.Single(
            await NotificationsFor(db, complaint.Id, NotificationType.SlaWarning));
        Assert.Equal(world.StaffId, notice.RecipientId);
    }

    /// <summary>
    /// 79% is not 80%. Off-by-one at the threshold is the mutation that survives every test
    /// which only ever checks the far side of it.
    /// </summary>
    [RequiresPostgresFact]
    public async Task DoesNotWarnBeforeTheThreshold()
    {
        await using var db = postgres.CreateContext();
        var world = await World.CreateAsync(db);
        var complaint = await world.ComplaintAsync(db, elapsedPercent: 79);

        await world.Sweeper(db).SweepAsync();

        var swept = await Reload(db, complaint.Id);

        Assert.Null(swept.SlaWarnedAt);
        Assert.Null(swept.SlaBreachedAt);
        Assert.Empty(await NotificationsFor(db, complaint.Id));
    }

    /// <summary>
    /// <b>The rungs are pinned at their exact thresholds, not somewhere near them.</b>
    ///
    /// Fixtures at 79 and 90 leave nine points of slack: a warning rung that had drifted to
    /// 89 would satisfy both. These cases sit on the boundary itself, so 80 means 80 and
    /// 150 means 150 — and "at or past" means <c>&gt;=</c>, not <c>&gt;</c>.
    ///
    /// The percentages come from <c>SlaOptions</c>, so this pins the configured default
    /// rather than a constant hidden in the sweeper. Changing a default then becomes a
    /// visible, deliberate edit to this table.
    /// </summary>
    [RequiresPostgresTheory]
    [InlineData(79.9, false, false, 0)]
    [InlineData(80, true, false, 0)]
    [InlineData(99.9, true, false, 0)]
    [InlineData(100, true, true, 1)]
    [InlineData(149.9, true, true, 1)]
    [InlineData(150, true, true, 2)]
    public async Task EachRungFiresAtItsExactThreshold(
        double elapsedPercent,
        bool expectWarned,
        bool expectBreached,
        int expectedLevel)
    {
        await using var db = postgres.CreateContext();

        // A long window, so a tenth of a percent is still minutes rather than milliseconds
        // and the assertion is about the threshold rather than about clock resolution.
        var world = await World.CreateAsync(db, slaHours: 1000);
        var complaint = await world.ComplaintAsync(db, elapsedPercent, assigned: true);

        await world.Sweeper(db).SweepAsync();

        var swept = await Reload(db, complaint.Id);

        Assert.Equal(expectWarned, swept.SlaWarnedAt is not null);
        Assert.Equal(expectBreached, swept.SlaBreachedAt is not null);
        Assert.Equal((short)expectedLevel, swept.EscalationLevel);
    }

    /// <summary>
    /// 100% elapsed: breached, level 1, one <c>EscalationEvent</c>, and — §11.2 —
    /// the assignee <b>and</b> every active Dept Admin notified.
    /// </summary>
    [RequiresPostgresFact]
    public async Task BreachesAtOneHundredPercentAndNotifiesTheAssigneeAndEveryAdmin()
    {
        await using var db = postgres.CreateContext();
        var world = await World.CreateAsync(db);
        var complaint = await world.ComplaintAsync(db, elapsedPercent: 120, assigned: true);

        await world.Sweeper(db).SweepAsync();

        var swept = await Reload(db, complaint.Id);

        Assert.Equal(Noon, swept.SlaBreachedAt);
        Assert.Equal(1, swept.EscalationLevel);

        var escalation = Assert.Single(await EscalationsFor(db, complaint.Id));
        Assert.Equal(1, escalation.Level);
        Assert.Equal(Noon, escalation.RaisedAt);
        Assert.Equal(world.StaffId, escalation.NotifiedUserId);
        Assert.Equal(0, escalation.ReopenCount);

        var breachNotices = await NotificationsFor(db, complaint.Id, NotificationType.SlaBreached);

        // One per recipient: the assignee plus both of the department's active admins. The
        // inactive admin is not a recipient.
        Assert.Equal(3, breachNotices.Count);
        Assert.Contains(breachNotices, n => n.RecipientId == world.StaffId);
        Assert.Contains(breachNotices, n => n.RecipientId == world.AdminId);
        Assert.Contains(breachNotices, n => n.RecipientId == world.SecondAdminId);
        Assert.DoesNotContain(breachNotices, n => n.RecipientId == world.InactiveAdminId);
    }

    /// <summary>
    /// An unassigned complaint has nobody holding it, so §11.2 sends the warning to every
    /// active Dept Admin instead of to a null assignee.
    /// </summary>
    [RequiresPostgresFact]
    public async Task AnUnassignedWarningGoesToTheDepartmentAdmins()
    {
        await using var db = postgres.CreateContext();
        var world = await World.CreateAsync(db);
        var complaint = await world.ComplaintAsync(db, elapsedPercent: 90, assigned: false);

        await world.Sweeper(db).SweepAsync();

        var notices = await NotificationsFor(db, complaint.Id, NotificationType.SlaWarning);

        Assert.Equal(2, notices.Count);
        Assert.Contains(notices, n => n.RecipientId == world.AdminId);
        Assert.Contains(notices, n => n.RecipientId == world.SecondAdminId);
    }

    /// <summary>150% elapsed: level 2, a second <c>EscalationEvent</c>, admins notified.</summary>
    [RequiresPostgresFact]
    public async Task EscalatesToLevelTwoAtOneHundredAndFiftyPercent()
    {
        await using var db = postgres.CreateContext();
        var world = await World.CreateAsync(db);
        var complaint = await world.ComplaintAsync(db, elapsedPercent: 170, assigned: true);

        await world.Sweeper(db).SweepAsync();

        var swept = await Reload(db, complaint.Id);
        Assert.Equal(2, swept.EscalationLevel);

        var escalations = await EscalationsFor(db, complaint.Id);
        Assert.Equal([(short)1, (short)2], escalations.Select(e => e.Level).Order().ToArray());

        // Level 2 goes to the department's admins as a group, so no single notified user is
        // recorded — naming one would misreport who was told.
        Assert.Null(escalations.Single(e => e.Level == 2).NotifiedUserId);

        var notices = await NotificationsFor(
            db, complaint.Id, NotificationType.SlaEscalatedLevel2);
        Assert.Equal(2, notices.Count);
    }

    /// <summary>
    /// A complaint that crossed 80% <b>and</b> 100% between two passes gets both markers in
    /// one pass.
    ///
    /// Warn and breach are independent — each has its own marker and its own predicate — so
    /// swapping <i>those two</i> changes nothing observable, and this test does not claim
    /// otherwise. The ordering that genuinely matters is breach before level 2: the breach
    /// phase assigns <c>EscalationLevel = 1</c> outright, so running it after level 2 would
    /// overwrite a 2 with a 1. <see cref="EscalatesToLevelTwoAtOneHundredAndFiftyPercent"/>
    /// pins that, by asserting the complaint ends at 2.
    /// </summary>
    [RequiresPostgresFact]
    public async Task BothMarkersLandInOnePassWhenAComplaintCrossedBoth()
    {
        await using var db = postgres.CreateContext();
        var world = await World.CreateAsync(db);
        var complaint = await world.ComplaintAsync(db, elapsedPercent: 130, assigned: true);

        var result = await world.Sweeper(db).SweepAsync();

        var swept = await Reload(db, complaint.Id);

        Assert.NotNull(swept.SlaWarnedAt);
        Assert.NotNull(swept.SlaBreachedAt);
        Assert.Equal(1, swept.EscalationLevel);

        // One complaint, two rungs: it is counted once as examined and once in each phase.
        Assert.Equal(1, result.Warned);
        Assert.Equal(1, result.Breached);
        Assert.Equal(1, result.Examined);
    }

    // -----------------------------------------------------------------------------------
    // Idempotency — §11.3, the load-bearing property
    // -----------------------------------------------------------------------------------

    /// <summary>
    /// <b>The single most important test in the project.</b> M7's Definition of Done, F11,
    /// and the reason the three defences of §11.3 exist.
    ///
    /// Two consecutive sweeps over the same overdue complaint must produce exactly one
    /// <c>EscalationEvent</c> per level and exactly one notification per recipient per
    /// level.
    ///
    /// The final-state assertions below are necessary and <b>not sufficient</b>, which is
    /// worth stating plainly because it is the trap this suite fell into once: defence 3
    /// rolls a duplicate insert back, so a sweep that wrongly <i>re-selected</i> the
    /// complaint still leaves the rows looking perfect. Deleting defence 1 was therefore
    /// invisible here until <see cref="ASecondPassDoesNotEvenAttemptTheWorkItAlreadyDid"/>
    /// was added beside it. Read the two together.
    /// </summary>
    [RequiresPostgresFact]
    public async Task TwoSweepsProduceOneEscalationPerLevelAndOneNotificationPerRecipient()
    {
        await using var db = postgres.CreateContext();
        var world = await World.CreateAsync(db);
        var complaint = await world.ComplaintAsync(db, elapsedPercent: 170, assigned: true);

        await world.Sweeper(db).SweepAsync();

        var afterFirst = await SnapshotAsync(db, complaint.Id);

        var second = await world.Sweeper(db).SweepAsync();

        var afterSecond = await SnapshotAsync(db, complaint.Id);

        Assert.Equal(afterFirst, afterSecond);

        // And the second pass says so in its counters, not merely by leaving the rows alone.
        Assert.Equal(0, second.Warned);
        Assert.Equal(0, second.Breached);
        Assert.Equal(0, second.EscalatedLevel2);

        // Spelled out, because "equal snapshots" would also be satisfied by a first pass
        // that did nothing at all.
        Assert.Equal(2, afterFirst.Escalations);
        Assert.Equal(1, afterFirst.Level1Escalations);
        Assert.Equal(1, afterFirst.Level2Escalations);
        Assert.Equal(1, afterFirst.WarningNotices);
        Assert.Equal(3, afterFirst.BreachNotices);
        Assert.Equal(2, afterFirst.Level2Notices);
    }

    /// <summary>
    /// <b>Defence 1 of §11.3, pinned directly.</b> CLAUDE.md non-negotiable 4: the three
    /// mechanisms are not redundant and none may be removed because the others cover it.
    ///
    /// The other two defences are what make this test necessary. Take away the
    /// not-yet-done <c>WHERE</c> clause and the second pass re-selects a complaint it has
    /// already escalated; the unique index then refuses the duplicate insert and the
    /// per-complaint transaction rolls it back — so the rows, the counters and the
    /// snapshots all still look exactly right. The only visible trace is the skip the
    /// sweeper logs when the index turns it away.
    ///
    /// So this asserts on <b>what the pass attempted</b>, not on what survived it: a second
    /// sweep must not touch this complaint at all. Delete <c>sla_warned_at IS NULL</c>,
    /// <c>SlaBreachedAt == null</c> or <c>escalation_level &lt; 2</c> and this goes red
    /// while every state-based test in the file stays green.
    ///
    /// Scoped to this complaint's own id rather than to "no warnings at all", because the
    /// sweep is system-wide and a sibling test deliberately leaves a complaint in the state
    /// that makes the index refuse it on every subsequent pass.
    /// </summary>
    [RequiresPostgresFact]
    public async Task ASecondPassDoesNotEvenAttemptTheWorkItAlreadyDid()
    {
        await using var db = postgres.CreateContext();
        var world = await World.CreateAsync(db);

        // 170%, so the first pass climbs all three rungs and every not-yet-done clause has
        // something to exclude on the second.
        var complaint = await world.ComplaintAsync(db, elapsedPercent: 170, assigned: true);

        await world.Sweeper(db).SweepAsync();

        var log = new RecordingLogger<SlaSweeper>();
        await world.Sweeper(db, logger: log).SweepAsync();

        var touched = log.Entries
            .Where(e => e.Message.Contains(complaint.Id.ToString()))
            .Select(e => e.Message)
            .ToList();

        Assert.Empty(touched);
    }

    /// <summary>
    /// The same property for the rung that writes no <c>EscalationEvent</c>. A warning has
    /// no unique index behind it, so removing <c>sla_warned_at IS NULL</c> would not be
    /// refused by anything — it would quietly send the assignee a fresh warning every
    /// minute until the complaint breached.
    /// </summary>
    [RequiresPostgresFact]
    public async Task AWarnedComplaintIsNotWarnedAgainOnEveryPass()
    {
        await using var db = postgres.CreateContext();
        var world = await World.CreateAsync(db);
        var complaint = await world.ComplaintAsync(db, elapsedPercent: 90, assigned: true);

        await world.Sweeper(db).SweepAsync();

        for (var pass = 0; pass < 4; pass++)
        {
            await world.Sweeper(db).SweepAsync();
        }

        Assert.Single(await NotificationsFor(db, complaint.Id, NotificationType.SlaWarning));
    }

    /// <summary>
    /// A ten-pass hammering. Idempotency that holds for two passes and fails at the third
    /// is not idempotency, and a counter incremented rather than a marker set would show up
    /// here and nowhere else.
    /// </summary>
    [RequiresPostgresFact]
    public async Task TenSweepsAreIndistinguishableFromOne()
    {
        await using var db = postgres.CreateContext();
        var world = await World.CreateAsync(db);
        var complaint = await world.ComplaintAsync(db, elapsedPercent: 200, assigned: true);

        await world.Sweeper(db).SweepAsync();
        var afterFirst = await SnapshotAsync(db, complaint.Id);

        for (var pass = 0; pass < 9; pass++)
        {
            await world.Sweeper(db).SweepAsync();
        }

        Assert.Equal(afterFirst, await SnapshotAsync(db, complaint.Id));
    }

    /// <summary>§11.3: a complaint already at level 2 is never selected again.</summary>
    [RequiresPostgresFact]
    public async Task AComplaintAlreadyAtLevelTwoIsNeverSelectedAgain()
    {
        await using var db = postgres.CreateContext();
        var world = await World.CreateAsync(db);
        var complaint = await world.ComplaintAsync(db, elapsedPercent: 400, assigned: true);

        await world.Sweeper(db).SweepAsync();
        Assert.Equal(2, (await Reload(db, complaint.Id)).EscalationLevel);

        // Push the clock much further out. There is no third rung, so nothing more happens
        // however overdue it gets.
        world.Clock.Advance(TimeSpan.FromDays(30));

        await world.Sweeper(db).SweepAsync();

        // Asserted on this complaint's own rows, never on the pass counters: the sweep is
        // system-wide, so a counter here would also be counting every other test's fixture.
        Assert.Equal(2, (await EscalationsFor(db, complaint.Id)).Count);
        Assert.Equal(2, (await Reload(db, complaint.Id)).EscalationLevel);
    }

    /// <summary>
    /// The database's own backstop — defence 3. Even with the marker forced back to null,
    /// so that defence 1 cannot see the work is done, the unique index on
    /// <c>(ComplaintId, ReopenCount, Level)</c> refuses the duplicate. This is the mechanism
    /// that survives two overlapping sweeps, and it is the one a reviewer will try to delete.
    /// </summary>
    [RequiresPostgresFact]
    public async Task TheUniqueIndexRefusesASecondEscalationAtTheSameLevel()
    {
        await using var db = postgres.CreateContext();
        var world = await World.CreateAsync(db);
        var complaint = await world.ComplaintAsync(db, elapsedPercent: 120, assigned: true);

        await world.Sweeper(db).SweepAsync();
        Assert.Single(await EscalationsFor(db, complaint.Id));

        db.EscalationEvents.Add(new EscalationEvent
        {
            Id = Guid.CreateVersion7(),
            ComplaintId = complaint.Id,
            ReopenCount = 0,
            Level = 1,
            RaisedAt = Noon,
            Reason = "A duplicate the index must refuse.",
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        Assert.Single(await EscalationsFor(db, complaint.Id));
    }

    /// <summary>
    /// And the sweep survives that refusal rather than dying on it: with the marker cleared
    /// behind its back, the rung is retried, the index rejects it, the complaint is skipped
    /// — and the rest of the pass still runs. This is what "two overlapping sweeps" looks
    /// like from inside the losing one.
    /// </summary>
    [RequiresPostgresFact]
    public async Task ASweepSurvivesTheIndexRefusingItsEscalation()
    {
        await using var db = postgres.CreateContext();
        var world = await World.CreateAsync(db);
        var loser = await world.ComplaintAsync(db, elapsedPercent: 120, assigned: true);

        await world.Sweeper(db).SweepAsync();

        // Simulate the overlap: the other sweep's escalation row is already committed, but
        // this one's view of the marker predates it.
        await db.Complaints
            .Where(c => c.Id == loser.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.SlaBreachedAt, (DateTimeOffset?)null));
        db.ChangeTracker.Clear();

        var bystander = await world.ComplaintAsync(db, elapsedPercent: 130, assigned: true);

        var result = await world.Sweeper(db).SweepAsync();

        // The loser is skipped, not duplicated...
        Assert.Single(await EscalationsFor(db, loser.Id));

        // ...and the complaint behind it in the same phase is still processed.
        Assert.Equal(1, (await Reload(db, bystander.Id)).EscalationLevel);
        Assert.Equal(1, result.Breached);
    }

    // -----------------------------------------------------------------------------------
    // What the sweep must never touch — §11.2
    // -----------------------------------------------------------------------------------

    /// <summary>
    /// "Still open" means <c>New</c>, <c>Assigned</c> or <c>InProgress</c>. A resolved
    /// complaint has stopped the clock; a rejected one never started it.
    /// </summary>
    [RequiresPostgresTheory]
    [InlineData(ComplaintStatus.Resolved)]
    [InlineData(ComplaintStatus.Closed)]
    [InlineData(ComplaintStatus.Rejected)]
    public async Task AClosedOutComplaintIsNeverSelected(ComplaintStatus status)
    {
        await using var db = postgres.CreateContext();
        var world = await World.CreateAsync(db);

        // Deeply overdue, so only the status can be what keeps it out of the sweep.
        var complaint = await world.ComplaintAsync(db, elapsedPercent: 500, status: status);

        await world.Sweeper(db).SweepAsync();

        var swept = await Reload(db, complaint.Id);

        Assert.Null(swept.SlaWarnedAt);
        Assert.Null(swept.SlaBreachedAt);
        Assert.Equal(0, swept.EscalationLevel);
        Assert.Empty(await EscalationsFor(db, complaint.Id));
    }

    // -----------------------------------------------------------------------------------
    // Auto-close — §11.5
    // -----------------------------------------------------------------------------------

    /// <summary>
    /// A complaint <c>Resolved</c> longer than <c>Sla:AutoCloseAfterDays</c> is closed as
    /// the <c>System</c> actor, with a history row that has no actor at all.
    /// </summary>
    [RequiresPostgresFact]
    public async Task AutoClosesAResolvedComplaintAfterTheConfiguredDays()
    {
        await using var db = postgres.CreateContext();
        var world = await World.CreateAsync(db);

        var complaint = await world.ComplaintAsync(
            db,
            elapsedPercent: 50,
            status: ComplaintStatus.Resolved,
            resolvedAt: Noon.AddDays(-8));

        var result = await world.Sweeper(db).SweepAsync();

        var swept = await Reload(db, complaint.Id);

        Assert.Equal(ComplaintStatus.Closed, swept.Status);
        Assert.Equal(Noon, swept.ClosedAt);
        Assert.Equal(1, result.AutoClosed);

        var history = await db.ComplaintStatusHistories
            .AsNoTracking()
            .Where(h => h.ComplaintId == complaint.Id)
            .ToListAsync();

        var closure = Assert.Single(history, h => h.ToStatus == ComplaintStatus.Closed);
        Assert.True(closure.IsSystem);
        Assert.Null(closure.ChangedById);
        Assert.Equal(ComplaintAction.Close, closure.Action);
        Assert.Equal(ComplaintStatus.Resolved, closure.FromStatus);
        Assert.Contains("Auto-closed after 7 days", closure.Note);
    }

    /// <summary>Six days is not seven. The boundary is the whole feature.</summary>
    [RequiresPostgresFact]
    public async Task DoesNotAutoCloseBeforeTheConfiguredDays()
    {
        await using var db = postgres.CreateContext();
        var world = await World.CreateAsync(db);

        var complaint = await world.ComplaintAsync(
            db,
            elapsedPercent: 50,
            status: ComplaintStatus.Resolved,
            resolvedAt: Noon.AddDays(-6));

        var result = await world.Sweeper(db).SweepAsync();

        Assert.Equal(0, result.AutoClosed);
        Assert.Equal(ComplaintStatus.Resolved, (await Reload(db, complaint.Id)).Status);
    }

    /// <summary>
    /// Auto-close is idempotent for the same reason the rungs are: once closed, the
    /// complaint no longer matches the phase's <c>Status = Resolved</c> predicate.
    /// </summary>
    [RequiresPostgresFact]
    public async Task AutoCloseDoesNotRunTwice()
    {
        await using var db = postgres.CreateContext();
        var world = await World.CreateAsync(db);

        var complaint = await world.ComplaintAsync(
            db,
            elapsedPercent: 50,
            status: ComplaintStatus.Resolved,
            resolvedAt: Noon.AddDays(-8));

        await world.Sweeper(db).SweepAsync();
        var second = await world.Sweeper(db).SweepAsync();

        Assert.Equal(0, second.AutoClosed);

        var closures = await db.ComplaintStatusHistories
            .AsNoTracking()
            .CountAsync(h => h.ComplaintId == complaint.Id && h.IsSystem);

        Assert.Equal(1, closures);
    }

    /// <summary>
    /// <b>The auto-close still asks the guard table.</b> CLAUDE.md non-negotiable 3.
    ///
    /// <c>AutoCloseAsync</c> only ever selects <c>Resolved</c> complaints, so in normal
    /// operation the guard-table consultation inside <c>CloseAsSystemAsync</c> never says
    /// no — which means deleting it would go unnoticed. This calls that method directly on
    /// a complaint the table forbids closing, so the check is load-bearing rather than
    /// decorative.
    ///
    /// <c>New → close</c> is an absent pair: §12.3 has exactly one <c>close</c> row and it
    /// starts at <c>Resolved</c>. The method must refuse and leave the complaint alone.
    /// </summary>
    [RequiresPostgresTheory]
    [InlineData(ComplaintStatus.New)]
    [InlineData(ComplaintStatus.Assigned)]
    [InlineData(ComplaintStatus.InProgress)]
    [InlineData(ComplaintStatus.Rejected)]
    public async Task TheSystemCannotCloseAComplaintTheGuardTableDoesNotAllow(
        ComplaintStatus status)
    {
        await using var db = postgres.CreateContext();
        var world = await World.CreateAsync(db);
        var complaint = await world.ComplaintAsync(db, elapsedPercent: 50, status: status);

        var closed = await world.SystemCloseAsync(db, complaint.Id);

        Assert.False(closed);

        var untouched = await Reload(db, complaint.Id);
        Assert.Equal(status, untouched.Status);
        Assert.Null(untouched.ClosedAt);

        Assert.Empty(await db.ComplaintStatusHistories
            .AsNoTracking()
            .Where(h => h.ComplaintId == complaint.Id && h.IsSystem)
            .ToListAsync());
    }

    /// <summary>
    /// And it says yes to the one row that permits it — row 11, the only rule in §12.3
    /// carrying <c>AllowSystem</c>. Asserted next to the refusals above so neither half can
    /// be satisfied by a method that simply always returns the same answer.
    /// </summary>
    [RequiresPostgresFact]
    public async Task TheSystemMayCloseAResolvedComplaint()
    {
        await using var db = postgres.CreateContext();
        var world = await World.CreateAsync(db);

        var complaint = await world.ComplaintAsync(
            db,
            elapsedPercent: 50,
            status: ComplaintStatus.Resolved,
            resolvedAt: Noon.AddDays(-8));

        Assert.True(await world.SystemCloseAsync(db, complaint.Id));
        Assert.Equal(ComplaintStatus.Closed, (await Reload(db, complaint.Id)).Status);
    }

    // -----------------------------------------------------------------------------------
    // Reopening — §11.4
    // -----------------------------------------------------------------------------------

    /// <summary>
    /// §11.4: a reopened complaint gets a fresh window and may legitimately climb the
    /// ladder again, while its earlier <c>EscalationEvent</c> rows are kept. That is exactly
    /// why <c>ReopenCount</c> is part of the unique key — without it, the second breach
    /// would collide with the first and the reopened complaint could never escalate.
    /// </summary>
    [RequiresPostgresFact]
    public async Task AReopenedComplaintClimbsTheLadderAgainAndKeepsItsHistory()
    {
        await using var db = postgres.CreateContext();
        var world = await World.CreateAsync(db);
        var complaint = await world.ComplaintAsync(db, elapsedPercent: 120);

        await world.Sweeper(db).SweepAsync();
        Assert.Single(await EscalationsFor(db, complaint.Id));

        // Reopened through the real write path — the reopen clock reset of §11.4 belongs to
        // ComplaintTransitionService, and reproducing it here would test a copy.
        await world.ResolveAndReopenAsync(db, complaint.Id);

        var reopened = await Reload(db, complaint.Id);
        Assert.Equal(1, reopened.ReopenCount);
        Assert.Null(reopened.SlaBreachedAt);
        Assert.Equal(0, reopened.EscalationLevel);

        // History is never rewritten: the first window's escalation is still there.
        Assert.Single(await EscalationsFor(db, complaint.Id));

        // Run the new window out and sweep again. Eleven hours is past the fresh SlaDueAt
        // (Noon + 10h) but short of the level-2 rung, which §11.4 still measures from the
        // original CreatedAt — so this is a clean second breach and nothing more.
        world.Clock.Advance(TimeSpan.FromHours(11));
        await world.Sweeper(db).SweepAsync();

        var escalations = await EscalationsFor(db, complaint.Id);

        // Two level-1 rows for one complaint. Without ReopenCount in the unique key these
        // two would collide and a reopened complaint could never escalate again — which is
        // exactly what §11.4 says the key is for.
        Assert.Equal(2, escalations.Count);
        Assert.Equal([0, 1], escalations.Select(e => e.ReopenCount).Order().ToArray());
        Assert.All(escalations, e => Assert.Equal(1, e.Level));
    }

    // -----------------------------------------------------------------------------------
    // Recategorizing — §11.4
    // -----------------------------------------------------------------------------------

    /// <summary>
    /// §11.4: <c>recategorize</c> recomputes <c>SlaDueAt</c> from the <b>original</b> creation
    /// time, not from now, and <b>does not</b> clear the breach markers.
    ///
    /// Both halves matter, and the second is the one with teeth: rerouting a complaint must
    /// not be a way to erase a missed deadline. Recomputing from <c>now</c> would hand every
    /// overdue complaint a fresh window for the price of one category change.
    /// </summary>
    [RequiresPostgresFact]
    public async Task RecategorizingRecomputesFromCreationAndKeepsTheBreach()
    {
        await using var db = postgres.CreateContext();
        var world = await World.CreateAsync(db);
        var complaint = await world.ComplaintAsync(db, elapsedPercent: 120);

        await world.Sweeper(db).SweepAsync();
        Assert.NotNull((await Reload(db, complaint.Id)).SlaBreachedAt);

        var createdAt = (await Reload(db, complaint.Id)).CreatedAt;

        await world.AsAdmin(db).TransitionAsync(
            complaint.Id,
            new TransitionRequest
            {
                Action = ComplaintAction.Recategorize,
                CategoryId = world.LongCategoryId,
            });

        var moved = await Reload(db, complaint.Id);

        // From CreatedAt, not from Noon. The complaint is as old as it always was.
        Assert.Equal(createdAt.AddHours(World.LongCategorySlaHours), moved.SlaDueAt);
        Assert.Equal(createdAt, moved.CreatedAt);

        // And the breach it already earned is still on the record.
        Assert.NotNull(moved.SlaBreachedAt);
        Assert.NotNull(moved.SlaWarnedAt);
        Assert.Equal(1, moved.EscalationLevel);
        Assert.Single(await EscalationsFor(db, complaint.Id));
    }

    // -----------------------------------------------------------------------------------
    // Delivery and batching — F12, §11.6
    // -----------------------------------------------------------------------------------

    /// <summary>
    /// F12: a delivery failure increments <c>Attempts</c> and records <c>LastError</c>
    /// <b>without failing the sweep</b>. The escalation is already committed — a dead
    /// channel must cost a retry, never a rung.
    /// </summary>
    [RequiresPostgresFact]
    public async Task ADeliveryFailureIsRecordedAndTheSweepStillSucceeds()
    {
        await using var db = postgres.CreateContext();
        var world = await World.CreateAsync(db);
        var complaint = await world.ComplaintAsync(db, elapsedPercent: 120, assigned: true);

        var result = await world.Sweeper(db, sender: new FailingSender()).SweepAsync();

        Assert.Equal(1, result.Breached);
        Assert.Equal(1, (await Reload(db, complaint.Id)).EscalationLevel);

        var notices = await NotificationsFor(db, complaint.Id, NotificationType.SlaBreached);

        Assert.NotEmpty(notices);
        Assert.All(notices, n =>
        {
            Assert.Null(n.SentAt);
            Assert.Equal(1, n.Attempts);
            Assert.Equal(FailingSender.Reason, n.LastError);
        });
    }

    /// <summary>A sender that throws is no more allowed to take the sweep down than one that reports.</summary>
    [RequiresPostgresFact]
    public async Task AThrowingSenderDoesNotFailTheSweep()
    {
        await using var db = postgres.CreateContext();
        var world = await World.CreateAsync(db);
        var complaint = await world.ComplaintAsync(db, elapsedPercent: 120, assigned: true);

        var result = await world.Sweeper(db, sender: new ThrowingSender()).SweepAsync();

        Assert.Equal(1, result.Breached);

        var notices = await NotificationsFor(db, complaint.Id, NotificationType.SlaBreached);
        Assert.All(notices, n => Assert.NotNull(n.LastError));
    }

    /// <summary>A successful delivery stamps <c>SentAt</c> and leaves no error behind.</summary>
    [RequiresPostgresFact]
    public async Task ASuccessfulDeliveryStampsSentAt()
    {
        await using var db = postgres.CreateContext();
        var world = await World.CreateAsync(db);
        var complaint = await world.ComplaintAsync(db, elapsedPercent: 120, assigned: true);

        await world.Sweeper(db).SweepAsync();

        var notices = await NotificationsFor(db, complaint.Id, NotificationType.SlaBreached);

        Assert.All(notices, n =>
        {
            Assert.Equal(Noon, n.SentAt);
            Assert.Equal(1, n.Attempts);
            Assert.Null(n.LastError);
        });
    }

    /// <summary>
    /// §11.6: hitting the batch cap is logged, so a backlog is visible rather than silent.
    /// With the cap at one and two complaints overdue, the first pass caps and says so.
    /// </summary>
    [RequiresPostgresFact]
    public async Task HittingTheBatchCapIsLogged()
    {
        await using var db = postgres.CreateContext();
        var world = await World.CreateAsync(db);
        await world.ComplaintAsync(db, elapsedPercent: 120, assigned: true);
        await world.ComplaintAsync(db, elapsedPercent: 130, assigned: true);

        var log = new RecordingLogger<SlaSweeper>();

        var result = await world.Sweeper(db, batchSize: 1, logger: log).SweepAsync();

        Assert.Equal(1, result.Breached);
        Assert.Contains(
            log.Entries,
            e => e.Level == LogLevel.Warning && e.Message.Contains("hit the batch cap"));
    }

    /// <summary>
    /// §11.6: one structured line per pass, at Information. It is the only observability
    /// this feature has — a sweep that logged nothing would be invisible in production.
    /// </summary>
    [RequiresPostgresFact]
    public async Task EveryPassLogsExactlyOneSummaryLine()
    {
        await using var db = postgres.CreateContext();
        var world = await World.CreateAsync(db);
        await world.ComplaintAsync(db, elapsedPercent: 120, assigned: true);

        var log = new RecordingLogger<SlaSweeper>();

        await world.Sweeper(db, logger: log).SweepAsync();

        Assert.Single(
            log.Entries,
            e => e.Level == LogLevel.Information && e.Message.Contains("SLA sweep complete"));
    }

    // -----------------------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------------------

    private static async Task<Complaint> Reload(ObhijogDbContext db, Guid id)
    {
        db.ChangeTracker.Clear();

        return await db.Complaints.AsNoTracking().SingleAsync(c => c.Id == id);
    }

    private static async Task<List<EscalationEvent>> EscalationsFor(ObhijogDbContext db, Guid id) =>
        await db.EscalationEvents.AsNoTracking().Where(e => e.ComplaintId == id).ToListAsync();

    private static async Task<List<Notification>> NotificationsFor(
        ObhijogDbContext db,
        Guid id,
        NotificationType? type = null) =>
        await db.Notifications
            .AsNoTracking()
            .Where(n => n.ComplaintId == id && (type == null || n.Type == type))
            .ToListAsync();

    /// <summary>
    /// Everything a second sweep must leave untouched, in one comparable value. A record,
    /// so the assertion is a single equality that names every field it covers.
    /// </summary>
    private static async Task<Snapshot> SnapshotAsync(ObhijogDbContext db, Guid id)
    {
        var complaint = await Reload(db, id);
        var escalations = await EscalationsFor(db, id);
        var notices = await NotificationsFor(db, id);

        return new Snapshot(
            complaint.SlaWarnedAt,
            complaint.SlaBreachedAt,
            complaint.EscalationLevel,
            escalations.Count,
            escalations.Count(e => e.Level == 1),
            escalations.Count(e => e.Level == 2),
            notices.Count(n => n.Type == NotificationType.SlaWarning),
            notices.Count(n => n.Type == NotificationType.SlaBreached),
            notices.Count(n => n.Type == NotificationType.SlaEscalatedLevel2));
    }

    private record Snapshot(
        DateTimeOffset? WarnedAt,
        DateTimeOffset? BreachedAt,
        short Level,
        int Escalations,
        int Level1Escalations,
        int Level2Escalations,
        int WarningNotices,
        int BreachNotices,
        int Level2Notices);

    /// <summary>
    /// One department's worth of fixture: a category with a known SLA window, two active
    /// Dept Admins and one inactive one, a staff member and a citizen. Unique keys
    /// throughout, so tests never collide in a shared database.
    /// </summary>
    private sealed class World
    {
        public required TestClock Clock { get; init; }

        public required Guid DepartmentId { get; init; }

        public required Guid CategoryId { get; init; }

        public required int SlaHours { get; init; }

        /// <summary>A far longer window, for the recategorize recompute of §11.4.</summary>
        public required Guid LongCategoryId { get; init; }

        public const int LongCategorySlaHours = 48;

        public required Guid AdminId { get; init; }

        public required Guid SecondAdminId { get; init; }

        /// <summary>Present so the sweeper has something it must <b>not</b> notify.</summary>
        public required Guid InactiveAdminId { get; init; }

        public required Guid StaffId { get; init; }

        public required Guid CitizenId { get; init; }

        public static async Task<World> CreateAsync(ObhijogDbContext db, int slaHours = 10)
        {
            var suffix = Guid.NewGuid().ToString("N")[..11].ToUpperInvariant();

            var department = new Department
            {
                Id = Guid.CreateVersion7(),
                Name = $"Sweep Department {suffix}",
                Code = $"S{suffix}",
            };

            var category = new ComplaintCategory
            {
                Id = Guid.CreateVersion7(),
                Name = $"Sweep category {suffix}",
                DepartmentId = department.Id,
                SlaHours = slaHours,
                DefaultPriority = ComplaintPriority.Normal,
                IsActive = true,
            };

            var longCategory = new ComplaintCategory
            {
                Id = Guid.CreateVersion7(),
                Name = $"Sweep slow category {suffix}",
                DepartmentId = department.Id,
                SlaHours = LongCategorySlaHours,
                DefaultPriority = ComplaintPriority.Low,
                IsActive = true,
            };

            db.Departments.Add(department);
            db.ComplaintCategories.AddRange(category, longCategory);

            var admin = User(UserRole.DeptAdmin, department.Id);
            var secondAdmin = User(UserRole.DeptAdmin, department.Id);
            var inactiveAdmin = User(UserRole.DeptAdmin, department.Id);
            inactiveAdmin.IsActive = false;
            var staff = User(UserRole.Staff, department.Id);
            var citizen = User(UserRole.Citizen, null);

            db.Users.AddRange(admin, secondAdmin, inactiveAdmin, staff, citizen);

            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            return new World
            {
                Clock = new TestClock(Noon),
                DepartmentId = department.Id,
                CategoryId = category.Id,
                LongCategoryId = longCategory.Id,
                SlaHours = slaHours,
                AdminId = admin.Id,
                SecondAdminId = secondAdmin.Id,
                InactiveAdminId = inactiveAdmin.Id,
                StaffId = staff.Id,
                CitizenId = citizen.Id,
            };
        }

        /// <summary>
        /// A complaint placed at an exact point in its SLA window, computed backwards from
        /// <see cref="Noon"/> — the same trick the M7 seeder uses, and the only way to put a
        /// complaint at 170% without waiting.
        /// </summary>
        public async Task<Complaint> ComplaintAsync(
            ObhijogDbContext db,
            double elapsedPercent,
            bool assigned = false,
            ComplaintStatus status = ComplaintStatus.New,
            DateTimeOffset? resolvedAt = null)
        {
            var window = TimeSpan.FromHours(SlaHours);
            var createdAt = Noon - window * (elapsedPercent / 100d);

            var complaint = new Complaint
            {
                Id = Guid.CreateVersion7(),
                ReferenceNumber = $"MC-TEST-{Guid.NewGuid():N}"[..20],
                CitizenId = CitizenId,
                CategoryId = CategoryId,
                DepartmentId = DepartmentId,
                Title = "Swept complaint",
                Description = "A complaint that exists to be swept.",
                Status = status,
                Priority = ComplaintPriority.Normal,
                Latitude = 23.75m,
                Longitude = 90.39m,
                AssignedStaffId = assigned ? StaffId : null,
                CreatedAt = createdAt,
                SlaDueAt = SlaPolicy.DueAt(createdAt, SlaHours),
                ResolvedAt = resolvedAt,

                // The check constraints of §8.4 require the text the status implies; these
                // are fixture rows, not transitions, so they carry it directly.
                ResolutionNote = status is ComplaintStatus.Resolved or ComplaintStatus.Closed
                    ? "Resolved for the fixture."
                    : null,
                RejectionReason = status == ComplaintStatus.Rejected
                    ? "Rejected for the fixture."
                    : null,
                ClosedAt = status == ComplaintStatus.Closed ? createdAt : null,
            };

            db.Complaints.Add(complaint);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            return complaint;
        }

        /// <summary>
        /// Walks the complaint through <c>resolve</c> and <c>reopen</c> on the real write
        /// path, so §11.4's clock reset is the production one rather than a copy.
        /// </summary>
        public async Task ResolveAndReopenAsync(ObhijogDbContext db, Guid complaintId)
        {
            // Every step is a real row of §12.3, walked in order from New. There is no
            // shortcut that sets a status directly — that is the point of non-negotiable 3,
            // and it applies to test helpers too.
            await Transitions(db, AdminId, UserRole.DeptAdmin).TransitionAsync(
                complaintId,
                new TransitionRequest
                {
                    Action = ComplaintAction.Assign,
                    AssigneeId = StaffId,
                });

            await Transitions(db, StaffId, UserRole.Staff).TransitionAsync(
                complaintId,
                new TransitionRequest { Action = ComplaintAction.Start });

            await Transitions(db, StaffId, UserRole.Staff).TransitionAsync(
                complaintId,
                new TransitionRequest
                {
                    Action = ComplaintAction.Resolve,
                    Note = "Done, for now.",
                });

            await Transitions(db, CitizenId, UserRole.Citizen).TransitionAsync(
                complaintId,
                new TransitionRequest
                {
                    Action = ComplaintAction.Reopen,
                    Note = "It came back.",
                });

            db.ChangeTracker.Clear();
        }

        public SlaSweeper Sweeper(
            ObhijogDbContext db,
            INotificationSender? sender = null,
            int batchSize = 500,
            ILogger<SlaSweeper>? logger = null) =>
            new(
                db,
                Transitions(db, Guid.Empty, UserRole.Citizen, authenticated: false),
                sender ?? new RecordingSender(),
                Options.Create(new SlaOptions
                {
                    WarningThresholdPercent = 80,
                    EscalationLevel2Percent = 150,
                    AutoCloseAfterDays = 7,
                    SweepBatchSize = batchSize,
                }),
                Clock,
                logger ?? NullLogger<SlaSweeper>.Instance);

        public ComplaintTransitionService AsAdmin(ObhijogDbContext db) =>
            Transitions(db, AdminId, UserRole.DeptAdmin);

        /// <summary>
        /// The sweeper's own auto-close path, reached the way the sweeper reaches it: with
        /// no caller at all. <c>CloseAsSystemAsync</c> is <c>internal</c>, and the test
        /// project already sees internals of Infrastructure.
        /// </summary>
        public Task<bool> SystemCloseAsync(ObhijogDbContext db, Guid complaintId) =>
            Transitions(db, Guid.Empty, UserRole.Citizen, authenticated: false)
                .CloseAsSystemAsync(complaintId, "Auto-closed by the test.", Clock.GetUtcNow());

        private ComplaintTransitionService Transitions(
            ObhijogDbContext db,
            Guid userId,
            UserRole role,
            bool authenticated = true)
        {
            var user = new StubCurrentUser(userId, role, DepartmentId, authenticated);

            return new ComplaintTransitionService(
                db,
                new ComplaintService(db, user, Clock),
                user,
                Clock);
        }

        private static User User(UserRole role, Guid? departmentId)
        {
            var email = $"sweep-{Guid.CreateVersion7():N}@example.test";

            return new User
            {
                Id = Guid.CreateVersion7(),
                FullName = $"Sweep {role}",
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
    /// The sweeper has no caller. Outside a request <c>HttpContextCurrentUser</c> reports
    /// exactly this — unauthenticated, no id — and <c>CloseAsSystemAsync</c> must work
    /// anyway, which is the point of <c>ComplaintQueryScope.ForSystem</c>.
    /// </summary>
    private sealed class StubCurrentUser(
        Guid id,
        UserRole role,
        Guid? departmentId,
        bool authenticated) : ICurrentUser
    {
        public bool IsAuthenticated => authenticated;

        public Guid Id => id;

        public string Email => "stub@example.test";

        public UserRole Role => role;

        public Guid? DepartmentId => role == UserRole.Citizen ? null : departmentId;
    }

    private sealed class RecordingSender : INotificationSender
    {
        public Task<NotificationDelivery> SendAsync(
            Notification notification,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(NotificationDelivery.Success);
    }

    private sealed class FailingSender : INotificationSender
    {
        public const string Reason = "The channel is down.";

        public Task<NotificationDelivery> SendAsync(
            Notification notification,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(NotificationDelivery.Failed(Reason));
    }

    private sealed class ThrowingSender : INotificationSender
    {
        public Task<NotificationDelivery> SendAsync(
            Notification notification,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The channel exploded.");
    }
}

/// <summary>
/// Captures what was logged, so §11.6's two visible behaviours — the batch-cap warning and
/// the one summary line per pass — can be asserted rather than hoped for.
/// </summary>
public sealed class RecordingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, formatter(state, exception)));

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
