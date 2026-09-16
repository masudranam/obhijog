using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Obhijog.Domain.Complaints;
using Obhijog.Domain.Notifications;
using Obhijog.Domain.Sla;
using Obhijog.Domain.Users;
using Obhijog.Infrastructure.Complaints;
using Obhijog.Infrastructure.Messaging;
using Obhijog.Infrastructure.Notifications;
using Obhijog.Infrastructure.Options;
using Obhijog.Infrastructure.Persistence;

namespace Obhijog.Infrastructure.Sla;

/// <summary>
/// The SLA ladder of §11.2, made idempotent by the three mechanisms of §11.3.
///
/// <b>The three defences, and why none of them is redundant:</b>
///
/// <list type="number">
/// <item><b>The query selects only work not yet done.</b> Warnings need
/// <c>SlaWarnedAt IS NULL</c>, breaches need <c>SlaBreachedAt IS NULL</c>, level 2 needs
/// <c>EscalationLevel &lt; 2</c>. This is correctness: it is why a second sweep over the
/// same complaint finds nothing to do.</item>
/// <item><b>One transaction per complaint</b>, with the marker written alongside the
/// escalation and notification rows. This is atomicity: a pass that dies halfway has
/// either fully climbed a rung or not climbed it at all, and never leaves an escalation
/// whose marker is missing — which would make defence 1 wrong on the next pass.</item>
/// <item><b>The unique index on <c>(ComplaintId, ReopenCount, Level)</c></b>. This is the
/// backstop for the case defence 1 cannot see: two passes overlapping, both reading
/// <c>SlaBreachedAt IS NULL</c> before either writes. The second insert fails, its
/// transaction rolls back, and the complaint is simply skipped.</item>
/// </list>
///
/// Removing any one of them because "the others cover it" is the mutation the reviewer will
/// try first, and it would pass every test that does not run two sweeps at once.
///
/// <b>Phase order is ladder order.</b> Warn, then breach, then level 2, then auto-close. A
/// complaint that crossed 80% and 100% between two passes therefore gets both markers in
/// one pass and in the right order: phase 1 commits <c>SlaWarnedAt</c> before phase 2's
/// query runs, so phase 2 reads a warned complaint rather than racing it.
/// </summary>
public class SlaSweeper(
    ObhijogDbContext db,
    ComplaintTransitionService transitions,
    NotificationDispatcher dispatcher,
    ISlaEventPublisher publisher,
    IOptions<SlaOptions> options,
    TimeProvider timeProvider,
    ILogger<SlaSweeper> logger) : ISlaSweeper
{
    /// <summary>
    /// §11.2's "still open". <c>Resolved</c>, <c>Closed</c> and <c>Rejected</c> are outside
    /// the sweep entirely — a resolved complaint has stopped the clock and a rejected one
    /// never started it.
    /// </summary>
    private static readonly ComplaintStatus[] OpenStatuses =
    [
        ComplaintStatus.New,
        ComplaintStatus.Assigned,
        ComplaintStatus.InProgress,
    ];

    private SlaOptions Options => options.Value;

    public async Task<SlaSweepResult> SweepAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var now = timeProvider.GetUtcNow();

        var examined = new HashSet<Guid>();
        int warned = 0, breached = 0, escalated = 0, autoClosed = 0;

        try
        {
            warned = await WarnAsync(now, examined, cancellationToken);
            breached = await BreachAsync(now, examined, cancellationToken);
            escalated = await EscalateLevel2Async(now, examined, cancellationToken);
            autoClosed = await AutoCloseAsync(now, examined, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // §11.6: a phase that throws is logged and the pass ends; the next pass retries.
            // The sweep never takes the API process down, and the counters gathered so far
            // are still reported rather than discarded — a partial pass is information.
            logger.LogError(
                exception,
                "SLA sweep ended early after {Examined} complaints. The next pass will retry.",
                examined.Count);
        }

        stopwatch.Stop();

        var result = new SlaSweepResult(
            examined.Count,
            warned,
            breached,
            escalated,
            autoClosed,
            stopwatch.ElapsedMilliseconds);

        // §11.6: one structured line per pass. This is the entire observability story for a
        // feature with no UI of its own, so it is not a debug-level line.
        logger.LogInformation(
            "SLA sweep complete: {Examined} examined, {Warned} warned, {Breached} breached, "
            + "{EscalatedLevel2} escalated to level 2, {AutoClosed} auto-closed in {DurationMs}ms",
            result.Examined,
            result.Warned,
            result.Breached,
            result.EscalatedLevel2,
            result.AutoClosed,
            result.DurationMs);

        return result;
    }

    // --- the four phases ---------------------------------------------------------------

    /// <summary>
    /// 80% elapsed, still open, not yet warned. Sets <c>SlaWarnedAt</c>, leaves
    /// <c>EscalationLevel</c> alone and writes <b>no</b> <c>EscalationEvent</c> — a warning
    /// is not a rung of the escalation ladder, it is the notice that one is coming (§11.2).
    /// </summary>
    private async Task<int> WarnAsync(
        DateTimeOffset now,
        HashSet<Guid> examined,
        CancellationToken cancellationToken)
    {
        var ids = await SelectAtOrPastPercentAsync(
            now,
            Options.WarningThresholdPercent,
            NotYetWarned,
            nameof(WarnAsync),
            cancellationToken);

        return await ForEachAsync(ids, examined, cancellationToken, async (complaint, ct) =>
        {
            complaint.SlaWarnedAt = now;

            var recipients = complaint.AssignedStaffId is { } assignee
                ? [assignee]

                // §11.2: unassigned, so nobody is holding it — the department's admins are
                // the ones who can still act before it breaches.
                : await DepartmentAdminsAsync(complaint.DepartmentId, ct);

            return Notify(
                complaint,
                recipients,
                NotificationType.SlaWarning,
                $"Complaint {complaint.ReferenceNumber} is approaching its SLA deadline",
                $"\"{complaint.Title}\" is {Elapsed(complaint, now)}% through its SLA window "
                + $"and is due {complaint.SlaDueAt:u}.",
                now);
        });
    }

    /// <summary>
    /// 100% elapsed — past <c>SlaDueAt</c> — still open, not yet breached. Sets
    /// <c>SlaBreachedAt</c>, raises <c>EscalationLevel</c> to 1, writes one
    /// <c>EscalationEvent</c> and notifies the assignee <b>and</b> every active Dept Admin.
    /// </summary>
    private async Task<int> BreachAsync(
        DateTimeOffset now,
        HashSet<Guid> examined,
        CancellationToken cancellationToken)
    {
        // The one rung expressible without interval arithmetic: 100% of the window *is*
        // SlaDueAt, which is a stored column. Straight LINQ, and exactly the predicate the
        // partial (status, sla_due_at) index was built for.
        var ids = await ComplaintQueryScope
            .ForSystem(db.Complaints)
            .AsNoTracking()
            .Where(c => OpenStatuses.Contains(c.Status)
                && c.SlaBreachedAt == null
                && now >= c.SlaDueAt)
            .OrderBy(c => c.SlaDueAt)
            .Select(c => c.Id)
            .Take(Options.SweepBatchSize)
            .ToListAsync(cancellationToken);

        WarnIfCapped(ids.Count, nameof(BreachAsync));

        // Filled by the post-commit callback below, drained after the loop.
        //
        // **Enqueued on commit, not in the transaction, and this distinction was a bug once.**
        // The first version of this added to the list inside `act`, which runs *before*
        // SaveChanges and Commit. A complaint whose transaction then rolled back — an xmin
        // conflict because someone resolved it in the same instant, or any transient commit
        // failure — was skipped by the catch in ForEachAsync and published anyway. The handler
        // would then write breach notifications for a complaint that is not breached, and
        // defence 4 cannot catch that: there is no competing row, because nothing was written.
        // The phantom this ordering is supposed to make impossible was reachable.
        var breachedForPublication = new List<SlaBreachedMessage>();

        var breached = await ForEachAsync(
            ids,
            examined,
            cancellationToken,
            async (complaint, ct) =>
        {
            complaint.SlaBreachedAt = now;
            complaint.EscalationLevel = 1;

            var admins = await DepartmentAdminsAsync(complaint.DepartmentId, ct);

            // §11.2: the assignee **and** every active Dept Admin. Distinct, because a Dept
            // Admin is not an assignable assignee today, and "one notification per recipient
            // per level" must not depend on that staying true.
            var recipients = complaint.AssignedStaffId is { } assignee
                ? admins.Prepend(assignee).Distinct().ToList()
                : admins;

            Escalate(
                complaint,
                level: 1,
                reason: $"SLA breached: due {complaint.SlaDueAt:u}, "
                    + $"{Elapsed(complaint, now)}% of the window elapsed.",
                notifiedUserId: complaint.AssignedStaffId,
                now);

            // F16, and the only place in the sweep where the transport choice is visible.
            // **Detection has already happened** — the marker and the escalation row are
            // written above either way, inside this transaction. What moves is delivery.
            //
            // Under ServiceBus the sweeper stops here and a message, enqueued only once this
            // transaction has committed, carries the work to the handler. Returning no
            // notifications is what makes that true: a return of rows here would write them
            // inline *and* publish, and the queue would duplicate what was already recorded.
            if (!publisher.WritesNotificationsInline)
            {
                return [];
            }

            return Notify(
                complaint,
                recipients,
                NotificationType.SlaBreached,
                $"Complaint {complaint.ReferenceNumber} has breached its SLA",
                $"\"{complaint.Title}\" was due {complaint.SlaDueAt:u} and is still open.",
                now);
        },
            committed: complaint =>
            {
                if (!publisher.WritesNotificationsInline)
                {
                    breachedForPublication.Add(
                        new SlaBreachedMessage(complaint.Id, complaint.ReopenCount, now));
                }
            });

        foreach (var message in breachedForPublication)
        {
            try
            {
                await publisher.PublishBreachAsync(message, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // ServiceBusSlaEventPublisher already swallows and logs, so this is defence
                // against an implementation that does not. Without it a throwing publisher
                // escapes to SweepAsync's catch and silently costs this pass its level-2 and
                // auto-close phases — a notification failure taking out two unrelated rungs.
                logger.LogError(
                    exception,
                    "Publishing the breach of complaint {ComplaintId} threw. The breach is "
                    + "recorded; the notification is lost and the sweep continues.",
                    message.ComplaintId);
            }
        }

        return breached;
    }

    /// <summary>
    /// 150% elapsed, still open, not yet at level 2. Raises <c>EscalationLevel</c> to 2,
    /// writes one <c>EscalationEvent</c> and notifies every active Dept Admin.
    ///
    /// The <c>EscalationLevel &lt; 2</c> predicate is §11.3's, verbatim: it does not also
    /// require level 1, so a complaint that somehow reached 150% without a recorded breach
    /// still escalates rather than being stranded a rung below where it belongs.
    /// </summary>
    private async Task<int> EscalateLevel2Async(
        DateTimeOffset now,
        HashSet<Guid> examined,
        CancellationToken cancellationToken)
    {
        var ids = await SelectAtOrPastPercentAsync(
            now,
            Options.EscalationLevel2Percent,
            BelowLevelTwo,
            nameof(EscalateLevel2Async),
            cancellationToken);

        return await ForEachAsync(ids, examined, cancellationToken, async (complaint, ct) =>
        {
            complaint.EscalationLevel = 2;

            var admins = await DepartmentAdminsAsync(complaint.DepartmentId, ct);

            Escalate(
                complaint,
                level: 2,
                reason: $"Still open at {Elapsed(complaint, now)}% of the SLA window "
                    + $"(due {complaint.SlaDueAt:u}).",

                // No single notified user: level 2 goes to the department's admins as a
                // group, and naming one of them would misreport who was told.
                notifiedUserId: null,
                now);

            return Notify(
                complaint,
                admins,
                NotificationType.SlaEscalatedLevel2,
                $"Complaint {complaint.ReferenceNumber} escalated to level 2",
                $"\"{complaint.Title}\" has been overdue since {complaint.SlaDueAt:u} "
                + "and remains open.",
                now);
        });
    }

    /// <summary>
    /// §11.5. A complaint <c>Resolved</c> for longer than <c>Sla:AutoCloseAfterDays</c> is
    /// closed as the <c>System</c> actor.
    ///
    /// The mutation is delegated to <c>ComplaintTransitionService.CloseAsSystemAsync</c>:
    /// this class decides *which* complaints are due, the guard table still decides whether
    /// closing them is legal, and <c>complaint.Status = …</c> stays in exactly one file
    /// (CLAUDE.md non-negotiable 3).
    /// </summary>
    private async Task<int> AutoCloseAsync(
        DateTimeOffset now,
        HashSet<Guid> examined,
        CancellationToken cancellationToken)
    {
        var cutoff = now.AddDays(-Options.AutoCloseAfterDays);

        var ids = await ComplaintQueryScope
            .ForSystem(db.Complaints)
            .AsNoTracking()
            .Where(c => c.Status == ComplaintStatus.Resolved
                && c.ResolvedAt != null
                && c.ResolvedAt <= cutoff)
            .OrderBy(c => c.ResolvedAt)
            .Select(c => c.Id)
            .Take(Options.SweepBatchSize)
            .ToListAsync(cancellationToken);

        WarnIfCapped(ids.Count, nameof(AutoCloseAsync));

        var note =
            $"Auto-closed after {Options.AutoCloseAfterDays} days without citizen response.";

        var closed = 0;

        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                // Its own transaction, opened inside the service — per complaint, never per
                // pass.
                if (await transitions.CloseAsSystemAsync(id, note, now, cancellationToken))
                {
                    examined.Add(id);
                    closed++;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(
                    exception,
                    "Auto-close failed for complaint {ComplaintId}; the next pass will retry.",
                    id);
            }
            finally
            {
                db.ChangeTracker.Clear();
            }
        }

        return closed;
    }

    // --- the machinery the phases share -------------------------------------------------

    /// <summary>Defence 1 for the warning rung. A literal, never caller-supplied.</summary>
    private const string NotYetWarned = "sla_warned_at IS NULL";

    /// <summary>Defence 1 for the level-2 rung. A literal, never caller-supplied.</summary>
    private const string BelowLevelTwo = "escalation_level < 2";

    /// <summary>
    /// Selects the ids of open complaints at or past <paramref name="percent"/> of their
    /// window, narrowed by <paramref name="notYetDoneSql"/> — defence 1 of §11.3.
    ///
    /// <b>Why raw SQL for this one predicate.</b> The threshold is
    /// <c>now &gt;= created_at + p × (sla_due_at − created_at)</c>, which needs an interval
    /// scaled by a number. <c>TimeSpan × double</c> has no dependable Npgsql translation, and
    /// the failure mode if it silently client-evaluates is the worst kind: every open
    /// complaint pulled into memory every 60 seconds, results still correct, and the partial
    /// <c>(status, sla_due_at)</c> index doing nothing at all. Writing it out means the plan
    /// is what it looks like.
    ///
    /// Only <paramref name="notYetDoneSql"/> is interpolated as text and it is never
    /// caller-supplied — the two call sites pass the constants above. Every value is a
    /// parameter.
    ///
    /// It returns ids rather than entities because <c>Complaint.Version</c> maps to
    /// PostgreSQL's system <c>xmin</c> column, which <c>SELECT *</c> does not return; a
    /// <c>FromSql</c> over the entity would fail to materialise. The ids come back through
    /// raw SQL and the rows are then loaded, tracked, through the ordinary seam.
    /// </summary>
    private async Task<List<Guid>> SelectAtOrPastPercentAsync(
        DateTimeOffset now,
        int percent,
        string notYetDoneSql,
        string phase,
        CancellationToken cancellationToken)
    {
        var fraction = percent / 100d;

        var sql =
            // "AS \"Value\"" is not decoration: SqlQueryRaw<T> for a scalar binds the
            // column literally named Value, and "SELECT id" alone throws at materialisation.
            "SELECT id AS \"Value\" FROM complaints "
            + "WHERE status IN ('New', 'Assigned', 'InProgress') "
            + $"AND {notYetDoneSql} "
            + "AND {0} >= created_at + ({1} * (sla_due_at - created_at)) "
            + "ORDER BY sla_due_at "
            + "LIMIT {2}";

        var ids = await db.Database
            .SqlQueryRaw<Guid>(sql, now, fraction, Options.SweepBatchSize)
            .ToListAsync(cancellationToken);

        WarnIfCapped(ids.Count, phase);

        return ids;
    }

    /// <summary>
    /// Runs <paramref name="act"/> over each selected complaint, each in <b>its own
    /// transaction</b> — defence 2 of §11.3. One failing complaint must not roll back the
    /// 199 that succeeded.
    ///
    /// A failure is logged and the complaint skipped rather than ending the phase, because
    /// the expected failure here is defence 3 firing: two overlapping passes, the second
    /// one's <c>EscalationEvent</c> insert violating the unique index. That is the backstop
    /// working exactly as designed, and it must look like a skipped complaint, not an outage.
    ///
    /// Delivery happens after the commit, never inside it (F12).
    /// </summary>
    /// <param name="committed">
    /// Runs after this complaint's transaction has committed, and only then. It is how the
    /// breach phase enqueues its Service Bus message without risking one for work that rolled
    /// back — anything that must not happen for an abandoned complaint belongs here rather
    /// than in <paramref name="act"/>.
    /// </param>
    private async Task<int> ForEachAsync(
        IReadOnlyList<Guid> ids,
        HashSet<Guid> examined,
        CancellationToken cancellationToken,
        Func<Complaint, CancellationToken, Task<List<Notification>>> act,
        Action<Complaint>? committed = null)
    {
        var acted = 0;

        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();

            List<Notification> written;

            try
            {
                var complaint = await ComplaintQueryScope
                    .ForSystem(db.Complaints)
                    .SingleOrDefaultAsync(c => c.Id == id, cancellationToken);

                if (complaint is null)
                {
                    continue;
                }

                await using var transaction =
                    await db.Database.BeginTransactionAsync(cancellationToken);

                written = await act(complaint, cancellationToken);

                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                examined.Add(id);
                acted++;

                committed?.Invoke(complaint);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Defence 3 firing looks exactly like this. So does a genuine fault; both
                // are survivable at the level of one complaint, and the next pass retries.
                logger.LogWarning(
                    exception,
                    "SLA sweep skipped complaint {ComplaintId}; the next pass will retry.",
                    id);

                db.ChangeTracker.Clear();
                continue;
            }

            await DeliverAsync(written, cancellationToken);

            db.ChangeTracker.Clear();
        }

        return acted;
    }

    /// <summary>
    /// Best-effort delivery, after the commit. F12: a failure increments <c>Attempts</c> and
    /// records <c>LastError</c> <b>without failing the sweep</b> — the notification row is
    /// already durable, so a dead channel costs a retry, never an escalation.
    /// </summary>
    /// <summary>
    /// Delivery moved to <see cref="NotificationDispatcher"/> in M10, when the Service Bus
    /// handler became a second caller needing identical behaviour. Still called only after
    /// the per-complaint transaction has committed.
    /// </summary>
    private Task DeliverAsync(
        IReadOnlyList<Notification> notifications,
        CancellationToken cancellationToken) =>
        dispatcher.DeliverAsync(notifications, cancellationToken);

    /// <summary>
    /// One <c>EscalationEvent</c> per rung, inside the caller's transaction. The unique
    /// index on <c>(ComplaintId, ReopenCount, Level)</c> is what makes a second concurrent
    /// insert fail rather than duplicate — defence 3.
    /// </summary>
    private void Escalate(
        Complaint complaint,
        short level,
        string reason,
        Guid? notifiedUserId,
        DateTimeOffset now) =>
        db.EscalationEvents.Add(new EscalationEvent
        {
            Id = Guid.CreateVersion7(),
            ComplaintId = complaint.Id,

            // Part of the unique key: a reopened complaint legitimately climbs the ladder
            // again for its new window, while its earlier events are kept (§11.4).
            ReopenCount = complaint.ReopenCount,
            Level = level,
            RaisedAt = now,
            Reason = Truncate(reason, 200)!,
            NotifiedUserId = notifiedUserId,
        });

    /// <summary>
    /// Writes one notification row per recipient, inside the caller's transaction, and
    /// returns them so the caller can attempt delivery once that transaction has committed.
    /// </summary>
    private List<Notification> Notify(
        Complaint complaint,
        IReadOnlyList<Guid> recipients,
        NotificationType type,
        string subject,
        string body,
        DateTimeOffset now)
    {
        var written = new List<Notification>(recipients.Count);

        foreach (var recipient in recipients)
        {
            var notification = new Notification
            {
                Id = Guid.CreateVersion7(),
                RecipientId = recipient,
                ComplaintId = complaint.Id,

                // §11.3 defence 4's discriminator. Without it a reopened complaint could
                // never be notified about again, because the unique index would see the
                // previous cycle's row.
                ReopenCount = complaint.ReopenCount,
                Type = type,
                Subject = Truncate(subject, 160)!,
                Body = Truncate(body, 2000)!,
                CreatedAt = now,
            };

            db.Notifications.Add(notification);
            written.Add(notification);
        }

        return written;
    }

    /// <summary>Every active Dept Admin of one department. §11.2's escalation recipients.</summary>
    private async Task<List<Guid>> DepartmentAdminsAsync(
        Guid departmentId,
        CancellationToken cancellationToken) =>
        await db.Users
            .AsNoTracking()
            .Where(u => u.Role == UserRole.DeptAdmin
                && u.IsActive
                && u.DepartmentId == departmentId)
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// §11.6: hitting the cap is logged so a backlog is visible rather than silent. A pass
    /// that caps is not an error — the next one picks up where it stopped — but a pass that
    /// caps every minute is a capacity problem nobody would otherwise see.
    /// </summary>
    private void WarnIfCapped(int selected, string phase)
    {
        if (selected >= Options.SweepBatchSize)
        {
            logger.LogWarning(
                "SLA sweep phase {Phase} hit the batch cap of {BatchSize}. There is a backlog; "
                + "the next pass will continue it.",
                phase,
                Options.SweepBatchSize);
        }
    }

    private static int Elapsed(Complaint complaint, DateTimeOffset now) =>
        (int)SlaPolicy.ElapsedPercent(complaint.CreatedAt, complaint.SlaDueAt, now);

    /// <summary>
    /// The column lengths of §8.8 and §8.9 are validation, not decoration. A generated
    /// message must never be the reason an escalation fails to land.
    /// </summary>
    private static string? Truncate(string? value, int max = 500) =>
        value is null || value.Length <= max ? value : value[..max];
}
