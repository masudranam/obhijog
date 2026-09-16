using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Obhijog.Domain.Complaints;
using Obhijog.Domain.Notifications;
using Obhijog.Infrastructure.Messaging;
using Obhijog.Infrastructure.Persistence;
using Xunit;

namespace Obhijog.Tests;

/// <summary>
/// SPEC.md §18's seventh suite, and §14 F16's "a test asserts it".
///
/// <b>Why a seventh suite exists.</b> Two of M10's Definition-of-Done clauses are stated as
/// tests rather than as behaviour — "a redelivered message is provably harmless" and
/// "<c>InProcess</c> still works" — which is the same reason token rotation earned the fifth
/// and dashboard &amp; export the sixth. Both failures are invisible from outside: a duplicate
/// notification looks like a notification, and a transport flag that silently falls back to
/// in-process delivery looks like a working system with an empty queue.
///
/// They do not belong in <see cref="SlaSweeperTests"/>. That suite is about the ladder — what
/// the sweep detects and when. This one is about what happens to a breach *after* detection,
/// on either side of a queue, and it is the handler rather than the sweeper under test in
/// most of it.
///
/// <b>Nothing here talks to Azure.</b> There is no Service Bus namespace behind this project,
/// so the transport is doubled and the handler is called directly — which is exactly why the
/// handler lives in Infrastructure rather than in the Function. What is real is the database:
/// §11.3 defence 4 is a PostgreSQL partial unique index, and the in-memory provider does not
/// enforce unique indexes at all, so it would pass every test in this file while the property
/// they exist to prove was absent.
/// </summary>
// Same collection as the sweep suite, so the two serialise against each other rather than
// racing over one shared database (issue #50).
[Collection(SlaSweeperTests.Serialised)]
public class SlaTransportTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    /// <summary>
    /// The sweep suite's instant, not a second one. The shared World's clock is set to it, and
    /// a message stamped from a different "now" would silently test nothing about ordering.
    /// </summary>
    private static readonly DateTimeOffset Noon = SlaSweeperTests.Noon;

    // --- the redelivery property ----------------------------------------------------------

    /// <summary>
    /// <b>The Definition of Done, as one assertion.</b> The same message twice produces one
    /// set of notifications.
    ///
    /// The second call is not a no-op because the handler checked first — it attempts the
    /// insert and PostgreSQL refuses it. That distinction is the whole design: a check-then-act
    /// handler would still duplicate under two concurrent deliveries, which is the case Service
    /// Bus actually produces when a lock expires while the first delivery is still running.
    /// </summary>
    [RequiresPostgresFact]
    public async Task ARedeliveredMessageWritesNothingTheSecondTime()
    {
        await using var db = postgres.CreateContext();
        var world = await SlaSweeperTests.World.CreateAsync(db);
        var complaint = await BreachedAsync(world, db);

        var message = new SlaBreachedMessage(complaint.Id, complaint.ReopenCount, Noon);

        var first = await Handler(world, db).HandleAsync(message);
        var second = await Handler(world, db).HandleAsync(message);

        Assert.True(first > 0, "the first delivery must write the notifications");
        Assert.Equal(0, second);

        Assert.Equal(first, await BreachNotificationsAsync(db, complaint.Id));
    }

    /// <summary>
    /// Ten deliveries, because "twice" is the easy case. Service Bus redelivers on lock
    /// expiry, on an abandoned message, and on a host restart mid-batch; a queue that has been
    /// draining a backlog can deliver the same message several times in a minute.
    /// </summary>
    [RequiresPostgresFact]
    public async Task TenDeliveriesAreIndistinguishableFromOne()
    {
        await using var db = postgres.CreateContext();
        var world = await SlaSweeperTests.World.CreateAsync(db);
        var complaint = await BreachedAsync(world, db);

        var message = new SlaBreachedMessage(complaint.Id, complaint.ReopenCount, Noon);

        var written = 0;

        for (var delivery = 0; delivery < 10; delivery++)
        {
            written += await Handler(world, db).HandleAsync(message);
        }

        Assert.Equal(written, await BreachNotificationsAsync(db, complaint.Id));
        Assert.Equal(
            world.ExpectedRecipientCount,
            await BreachNotificationsAsync(db, complaint.Id));
    }

    /// <summary>
    /// A redelivery must not merely write nothing — it must not <b>throw</b>. A Service Bus
    /// trigger that raises abandons the message, which redelivers it, which raises again: a
    /// poison loop built entirely out of a message that was handled correctly the first time,
    /// ending in a dead-letter queue full of successes.
    /// </summary>
    [RequiresPostgresFact]
    public async Task ARedeliveredMessageDoesNotThrow()
    {
        await using var db = postgres.CreateContext();
        var world = await SlaSweeperTests.World.CreateAsync(db);
        var complaint = await BreachedAsync(world, db);

        var message = new SlaBreachedMessage(complaint.Id, complaint.ReopenCount, Noon);

        await Handler(world, db).HandleAsync(message);

        var exception = await Record.ExceptionAsync(() => Handler(world, db).HandleAsync(message));

        Assert.Null(exception);
    }

    /// <summary>
    /// §11.4: a reopened complaint legitimately re-runs the ladder, so the *new* cycle's
    /// message must be accepted even though the old cycle's rows are still there.
    ///
    /// This is what <c>ReopenCount</c> is doing in the unique index. Without it the first
    /// breach would suppress every later one forever, and the test above would pass while the
    /// feature was broken in the more damaging direction.
    /// </summary>
    [RequiresPostgresFact]
    public async Task ALaterReopenCycleIsNotSuppressedByTheEarlierOne()
    {
        await using var db = postgres.CreateContext();
        var world = await SlaSweeperTests.World.CreateAsync(db);
        var complaint = await BreachedAsync(world, db);

        await Handler(world, db).HandleAsync(
            new SlaBreachedMessage(complaint.Id, 0, Noon));

        // The complaint comes back and breaches again a cycle later.
        await SetReopenCountAsync(db, complaint.Id, 1);

        var second = await Handler(world, db).HandleAsync(
            new SlaBreachedMessage(complaint.Id, 1, Noon.AddDays(1)));

        Assert.True(second > 0, "a new reopen cycle must be able to notify again");
        Assert.Equal(
            world.ExpectedRecipientCount * 2,
            await BreachNotificationsAsync(db, complaint.Id));
    }

    /// <summary>
    /// The mirror of the case above: a message from a *finished* cycle, arriving late. The
    /// complaint has already been reopened, so its clock was reset (§11.4) and telling anyone
    /// it is overdue would be wrong. The handler drops it rather than writing a stale notice.
    /// </summary>
    [RequiresPostgresFact]
    public async Task AMessageFromAFinishedReopenCycleIsDropped()
    {
        await using var db = postgres.CreateContext();
        var world = await SlaSweeperTests.World.CreateAsync(db);
        var complaint = await BreachedAsync(world, db);

        await SetReopenCountAsync(db, complaint.Id, 2);

        var written = await Handler(world, db).HandleAsync(
            new SlaBreachedMessage(complaint.Id, 0, Noon));

        Assert.Equal(0, written);
        Assert.Equal(0, await BreachNotificationsAsync(db, complaint.Id));
    }

    /// <summary>A complaint deleted between publish and delivery completes, and does not throw.</summary>
    [RequiresPostgresFact]
    public async Task AMessageForAnUnknownComplaintIsCompleted()
    {
        await using var db = postgres.CreateContext();
        var world = await SlaSweeperTests.World.CreateAsync(db);

        var written = await Handler(world, db).HandleAsync(
            new SlaBreachedMessage(Guid.CreateVersion7(), 0, Noon));

        Assert.Equal(0, written);
    }

    /// <summary>
    /// The handler re-derives recipients instead of trusting the message, so a reassignment
    /// between publish and delivery notifies whoever holds the complaint now. A message can
    /// sit in a queue for hours; the subject line it was published with may name someone who
    /// has since handed the work over.
    /// </summary>
    [RequiresPostgresFact]
    public async Task RecipientsAreResolvedAtDeliveryNotAtPublication()
    {
        await using var db = postgres.CreateContext();
        var world = await SlaSweeperTests.World.CreateAsync(db);
        var complaint = await BreachedAsync(world, db);

        await ReassignAsync(db, complaint.Id, world.OtherStaffId);

        await Handler(world, db).HandleAsync(
            new SlaBreachedMessage(complaint.Id, complaint.ReopenCount, Noon));

        var recipients = await db.Notifications
            .AsNoTracking()
            .Where(n => n.ComplaintId == complaint.Id && n.Type == NotificationType.SlaBreached)
            .Select(n => n.RecipientId)
            .ToListAsync();

        Assert.Contains(world.OtherStaffId, recipients);
        Assert.DoesNotContain(world.StaffId, recipients);
    }

    /// <summary>
    /// Delivery outcomes are recorded, because the handler routes through the same
    /// <c>NotificationDispatcher</c> the sweeper uses rather than looping over the sender
    /// itself. An earlier draft did the latter and silently left <c>Attempts</c> at zero and
    /// <c>SentAt</c> null on every row the Function wrote.
    /// </summary>
    [RequiresPostgresFact]
    public async Task TheHandlerStampsDeliveryTheSameWayTheSweeperDoes()
    {
        await using var db = postgres.CreateContext();
        var world = await SlaSweeperTests.World.CreateAsync(db);
        var complaint = await BreachedAsync(world, db);

        await Handler(world, db).HandleAsync(
            new SlaBreachedMessage(complaint.Id, complaint.ReopenCount, Noon));

        var notifications = await db.Notifications
            .AsNoTracking()
            .Where(n => n.ComplaintId == complaint.Id && n.Type == NotificationType.SlaBreached)
            .ToListAsync();

        Assert.NotEmpty(notifications);
        Assert.All(notifications, n => Assert.Equal(1, n.Attempts));
        Assert.All(notifications, n => Assert.NotNull(n.SentAt));
        Assert.All(notifications, n => Assert.Null(n.LastError));
    }

    // --- the transport switch ----------------------------------------------------------------

    /// <summary>
    /// <c>InProcess</c> is the default and must keep working unchanged — the second half of
    /// the M10 Definition of Done, and the clause most likely to be quietly broken by the
    /// first half.
    /// </summary>
    [RequiresPostgresFact]
    public async Task InProcessStillWritesItsOwnNotificationsAndPublishesNothing()
    {
        await using var db = postgres.CreateContext();
        var world = await SlaSweeperTests.World.CreateAsync(db);
        var complaint = await OverdueAsync(world, db);

        var publisher = new RecordingPublisher { WritesInline = true };

        await world.Sweeper(db, publisher: publisher).SweepAsync();

        Assert.Empty(publisher.Published);
        Assert.Equal(
            world.ExpectedRecipientCount,
            await BreachNotificationsAsync(db, complaint.Id));
    }

    /// <summary>
    /// <c>ServiceBus</c> moves delivery and nothing else. The marker and the escalation row
    /// are still written by the sweeper, inside its own transaction — "detection stays where
    /// it is" (F16) is an assertion about these three facts together, not a slogan.
    /// </summary>
    [RequiresPostgresFact]
    public async Task ServiceBusPublishesTheBreachAndWritesNoNotificationInline()
    {
        await using var db = postgres.CreateContext();
        var world = await SlaSweeperTests.World.CreateAsync(db);
        var complaint = await OverdueAsync(world, db);

        var publisher = new RecordingPublisher { WritesInline = false };

        await world.Sweeper(db, publisher: publisher).SweepAsync();

        // Delivery moved.
        Assert.Equal(0, await BreachNotificationsAsync(db, complaint.Id));

        var published = Assert.Single(publisher.Published, m => m.ComplaintId == complaint.Id);
        Assert.Equal(complaint.ReopenCount, published.ReopenCount);

        // Detection did not.
        var swept = await db.Complaints.AsNoTracking().SingleAsync(c => c.Id == complaint.Id);
        Assert.NotNull(swept.SlaBreachedAt);
        Assert.Equal(1, swept.EscalationLevel);

        Assert.Equal(
            1,
            await db.EscalationEvents.CountAsync(e => e.ComplaintId == complaint.Id && e.Level == 1));
    }

    /// <summary>
    /// The end-to-end shape, with the queue doubled: the sweeper publishes, the handler
    /// consumes, and the notifications that <c>InProcess</c> would have written inline exist
    /// after both halves have run. The two transports converge on the same database state,
    /// which is the actual claim F16 makes.
    /// </summary>
    [RequiresPostgresFact]
    public async Task TheTwoTransportsProduceTheSameNotificationsInTheEnd()
    {
        await using var db = postgres.CreateContext();
        var world = await SlaSweeperTests.World.CreateAsync(db);
        var complaint = await OverdueAsync(world, db);

        var publisher = new RecordingPublisher { WritesInline = false };
        await world.Sweeper(db, publisher: publisher).SweepAsync();

        foreach (var message in publisher.Published)
        {
            await Handler(world, db).HandleAsync(message);
        }

        Assert.Equal(
            world.ExpectedRecipientCount,
            await BreachNotificationsAsync(db, complaint.Id));
    }

    /// <summary>
    /// A sweep whose publish fails must still leave the breach recorded, <b>and must finish
    /// the pass</b>.
    ///
    /// The second half is the part worth testing. The publish loop runs after the breach
    /// phase's per-complaint transactions, outside their try — so an exception escaping it
    /// reaches <c>SweepAsync</c>'s catch and silently costs the pass its level-2 and
    /// auto-close phases. A notification failure would take out two unrelated rungs. The real
    /// <c>ServiceBusSlaEventPublisher</c> swallows and logs, so the sweeper's own guard is
    /// what this pins, and <see cref="ThrowingPublisher"/> is the implementation that ignores
    /// the contract.
    /// </summary>
    [RequiresPostgresFact]
    public async Task AFailedPublishLeavesTheBreachRecordedAndDoesNotAbortThePass()
    {
        await using var db = postgres.CreateContext();
        var world = await SlaSweeperTests.World.CreateAsync(db);
        var complaint = await OverdueAsync(world, db);

        // Auto-close runs after the breach phase, so it only happens if the throwing publisher
        // did not take the pass down with it.
        var stale = await world.ComplaintAsync(
            db,
            elapsedPercent: 50,
            status: ComplaintStatus.Resolved,
            resolvedAt: SlaSweeperTests.Noon.AddDays(-8));

        await world.Sweeper(db, publisher: new ThrowingPublisher()).SweepAsync();

        var swept = await db.Complaints.AsNoTracking().SingleAsync(c => c.Id == complaint.Id);

        Assert.NotNull(swept.SlaBreachedAt);
        Assert.Equal(1, swept.EscalationLevel);

        var closed = await db.Complaints.AsNoTracking().SingleAsync(c => c.Id == stale.Id);

        Assert.Equal(ComplaintStatus.Closed, closed.Status);
    }

    /// <summary>
    /// <b>A breach whose transaction rolls back must publish nothing.</b>
    ///
    /// This test exists because the code was wrong. The first version enqueued the message
    /// inside <c>act</c>, which runs before <c>SaveChanges</c> and <c>Commit</c>, so a
    /// complaint whose transaction failed was skipped by the sweeper *and published anyway*.
    /// The handler would then write breach notifications for a complaint that is not
    /// breached — and defence 4 cannot catch that one, because nothing competing was written.
    /// It is the exact phantom F16 and <see cref="ISlaEventPublisher.PublishBreachAsync"/>
    /// claim the post-commit ordering makes impossible, and the claim was false.
    ///
    /// The rollback is forced with a sender that has nothing to do with it: a
    /// <c>DbUpdateException</c> raised on save. What matters is only that the transaction
    /// fails after <c>act</c> has run, which is the shape of an xmin conflict too.
    /// </summary>
    [RequiresPostgresFact]
    public async Task ABreachWhoseTransactionRollsBackIsNeverPublished()
    {
        await using var db = postgres.CreateContext();
        var world = await SlaSweeperTests.World.CreateAsync(db);
        var complaint = await OverdueAsync(world, db);

        var publisher = new RecordingPublisher { WritesInline = false };

        // Force *this* complaint's breach transaction to fail, using §11.3 defence 3 rather
        // than a contrivance: an EscalationEvent already occupying (ComplaintId, ReopenCount,
        // Level 1) is exactly what an overlapping sweep leaves behind, and the sweeper's own
        // insert then loses to the unique index and rolls the transaction back.
        //
        // An earlier draft forced the failure with a duplicate-key Complaint left pending on
        // the change tracker. That was wrong and intermittently useless: a pending entity
        // attaches to whichever transaction saves first, so another suite's complaint in the
        // shared database would absorb the failure, clear the tracker, and let this
        // complaint's breach succeed — the test then passed while the bug was present.
        db.EscalationEvents.Add(new EscalationEvent
        {
            Id = Guid.CreateVersion7(),
            ComplaintId = complaint.Id,
            ReopenCount = complaint.ReopenCount,
            Level = 1,
            RaisedAt = Noon,
            Reason = "Planted so the sweeper's own escalation insert loses to defence 3.",
        });

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await world.Sweeper(db, publisher: publisher).SweepAsync();

        Assert.DoesNotContain(publisher.Published, m => m.ComplaintId == complaint.Id);

        // And the breach really did not happen, so there was nothing to announce.
        var swept = await db.Complaints.AsNoTracking().SingleAsync(c => c.Id == complaint.Id);
        Assert.Null(swept.SlaBreachedAt);
    }

    /// <summary>
    /// Defence 4's filter, pinned from both sides.
    ///
    /// The index is partial over the three SLA types, and nothing else in the suite would
    /// notice if someone "simplified" it to a blanket unique index — that change breaks no
    /// other test, and then produces a 500 on the second reassignment back to a staff member
    /// who already holds one. `ComplaintAssigned` legitimately recurs for the same recipient
    /// inside one reopen cycle; the SLA rungs do not.
    /// </summary>
    [RequiresPostgresFact]
    public async Task TheUniqueIndexConstrainsSlaNotificationsAndNothingElse()
    {
        await using var db = postgres.CreateContext();
        var world = await SlaSweeperTests.World.CreateAsync(db);
        var complaint = await BreachedAsync(world, db);

        // A → B → A: two ComplaintAssigned rows for one recipient in one cycle, both legal.
        db.Notifications.Add(Row(complaint.Id, world.StaffId, NotificationType.ComplaintAssigned));
        await db.SaveChangesAsync();
        db.Notifications.Add(Row(complaint.Id, world.StaffId, NotificationType.ComplaintAssigned));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.Equal(
            2,
            await db.Notifications.CountAsync(n =>
                n.ComplaintId == complaint.Id
                && n.RecipientId == world.StaffId
                && n.Type == NotificationType.ComplaintAssigned));

        // The same shape on an SLA type is refused.
        db.Notifications.Add(Row(complaint.Id, world.StaffId, NotificationType.SlaBreached));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        db.Notifications.Add(Row(complaint.Id, world.StaffId, NotificationType.SlaBreached));

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        Assert.Equal("23505", (exception.InnerException as Npgsql.PostgresException)?.SqlState);
        db.ChangeTracker.Clear();
    }

    private static Notification Row(Guid complaintId, Guid recipientId, NotificationType type) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            RecipientId = recipientId,
            ComplaintId = complaintId,
            ReopenCount = 0,
            Type = type,
            Subject = "s",
            Body = "b",
            CreatedAt = Noon,
        };

    // --- doubles --------------------------------------------------------------------------------

    /// <summary>The queue, recorded rather than sent. There is no namespace to send to.</summary>
    private sealed class RecordingPublisher : ISlaEventPublisher
    {
        public List<SlaBreachedMessage> Published { get; } = [];

        public bool WritesInline { get; init; }

        public bool WritesNotificationsInline => WritesInline;

        public Task PublishBreachAsync(
            SlaBreachedMessage message,
            CancellationToken cancellationToken = default)
        {
            Published.Add(message);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// A namespace that is down. <c>ServiceBusSlaEventPublisher</c> swallows and logs rather
    /// than throwing, so this double throws to prove the sweeper survives the harsher case too.
    /// </summary>
    private sealed class ThrowingPublisher : ISlaEventPublisher
    {
        public bool WritesNotificationsInline => false;

        public Task PublishBreachAsync(
            SlaBreachedMessage message,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The Service Bus namespace is unreachable.");
    }

    // --- fixture helpers ------------------------------------------------------------------
    // Thin wrappers over the shared SlaSweeperTests.World, kept here because they exist only
    // for this suite. Anything the sweep suite also needs lives on World itself.

    private static SlaBreachNotificationHandler Handler(
        SlaSweeperTests.World world,
        ObhijogDbContext db) =>
        new(db, world.Dispatcher(db), NullLogger<SlaBreachNotificationHandler>.Instance);

    /// <summary>
    /// Past due, assigned, and <b>not yet marked</b>: what the breach rung selects. Only the
    /// tests that actually run a sweep use this, and each of them sweeps it in the same test,
    /// so it does not survive as unfinished work.
    /// </summary>
    private static Task<Complaint> OverdueAsync(SlaSweeperTests.World world, ObhijogDbContext db) =>
        world.ComplaintAsync(db, elapsedPercent: 120, assigned: true);

    /// <summary>
    /// A complaint in the state a published message actually describes: the sweeper has
    /// already written <c>SlaWarnedAt</c>, <c>SlaBreachedAt</c> and level 1 and committed
    /// them, and only then published. The handler never sees an unmarked complaint.
    ///
    /// <b>This started as a bug in this fixture, and the bug was real rather than cosmetic.</b>
    /// The handler tests used an unmarked overdue complaint, which left one sitting in the
    /// shared database that the sweep suite's next pass duly warned and breached — turning
    /// <c>BothMarkersLandInOnePassWhenAComplaintCrossedBoth</c> red on its pass counters. That
    /// is issue #50 exactly: suites share one database, so a fixture is not private to the
    /// suite that wrote it. Marking the complaint fixes the interference and is the more
    /// faithful fixture, which is the only reason it is the right fix rather than a dodge —
    /// a complaint the sweeper has finished with is inert to every later pass.
    /// </summary>
    private static async Task<Complaint> BreachedAsync(
        SlaSweeperTests.World world,
        ObhijogDbContext db)
    {
        var complaint = await world.ComplaintAsync(db, elapsedPercent: 120, assigned: true);

        await db.Complaints
            .Where(c => c.Id == complaint.Id)
            .ExecuteUpdateAsync(set => set
                .SetProperty(c => c.SlaWarnedAt, SlaSweeperTests.Noon)
                .SetProperty(c => c.SlaBreachedAt, SlaSweeperTests.Noon)
                .SetProperty(c => c.EscalationLevel, (short)1));

        db.ChangeTracker.Clear();

        return complaint;
    }

    private static async Task<int> BreachNotificationsAsync(ObhijogDbContext db, Guid complaintId) =>
        await db.Notifications
            .AsNoTracking()
            .CountAsync(n => n.ComplaintId == complaintId && n.Type == NotificationType.SlaBreached);

    /// <summary>
    /// Moves the complaint to a later reopen cycle without running the ladder, which is what
    /// the §11.4 reopen path does to it. Written directly because these tests are about the
    /// handler's reaction to the cycle number, not about how the number got there — the
    /// transition path itself is already covered in the sweep suite.
    /// </summary>
    private static async Task SetReopenCountAsync(ObhijogDbContext db, Guid complaintId, int count)
    {
        await db.Complaints
            .Where(c => c.Id == complaintId)
            .ExecuteUpdateAsync(set => set.SetProperty(c => c.ReopenCount, count));

        db.ChangeTracker.Clear();
    }

    private static async Task ReassignAsync(ObhijogDbContext db, Guid complaintId, Guid staffId)
    {
        await db.Complaints
            .Where(c => c.Id == complaintId)
            .ExecuteUpdateAsync(set => set.SetProperty(c => c.AssignedStaffId, staffId));

        db.ChangeTracker.Clear();
    }
}
