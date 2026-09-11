using Microsoft.EntityFrameworkCore;
using Obhijog.Domain.Complaints;
using Obhijog.Domain.Exceptions;
using Obhijog.Domain.Notifications;
using Obhijog.Domain.Sla;
using Obhijog.Infrastructure.Auth;
using Obhijog.Infrastructure.Persistence;

namespace Obhijog.Infrastructure.Complaints;

/// <summary>
/// Complaint reads and submission. SPEC.md §16.2, F4, F5.
///
/// Every query here starts at <see cref="ComplaintQueryScope"/>. Transitions are not this
/// service's job — they belong to <c>ComplaintTransitionService</c> and the guard table in
/// M6, and there is no <c>complaint.Status = …</c> anywhere in this file except the initial
/// assignment at creation.
/// </summary>
public class ComplaintService(
    ObhijogDbContext db,
    ICurrentUser currentUser,
    TimeProvider timeProvider)
{
    /// <summary>
    /// F4. Validates the category, denormalizes its department and priority, allocates a
    /// reference number, computes the SLA deadline, writes the initial history row and
    /// queues the submission notification — all in one transaction.
    /// </summary>
    public async Task<ComplaintDetail> CreateAsync(
        CreateComplaintRequest request,
        CancellationToken cancellationToken = default)
    {
        var category = await db.ComplaintCategories
            .AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == request.CategoryId, cancellationToken);

        // A category that does not exist and one that is switched off are the same answer
        // to the caller: this is not a category you may file against. Neither is a 404 —
        // the category list is public, so nothing is being concealed (§9.2 is about rows
        // the caller may not see, which this is not).
        if (category is null || !category.IsActive)
        {
            throw new ValidationException(
                "categoryId",
                "The category does not exist or is no longer accepting new complaints.");
        }

        // A coordinate is not optional, and `decimal` has no absent value: an omitted pair
        // binds to 0,0 — a real point in the Gulf of Guinea that a crew would be dispatched
        // to. Rejecting the exact origin costs one unreachable location and prevents a
        // whole class of silently-wrong complaints.
        if (request is { Latitude: 0, Longitude: 0 })
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["latitude"] = ["A location is required."],
                ["longitude"] = ["A location is required."],
            });
        }

        var now = timeProvider.GetUtcNow();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var complaint = new Complaint
        {
            Id = Guid.CreateVersion7(),
            ReferenceNumber = await NextReferenceNumberAsync(now, cancellationToken),
            CitizenId = currentUser.Id,
            CategoryId = category.Id,

            // Denormalized at creation so that rerouting is an explicit, audited act rather
            // than a side effect of someone editing the category (§8.4).
            DepartmentId = category.DepartmentId,
            Priority = category.DefaultPriority,

            Title = request.Title.Trim(),
            Description = request.Description.Trim(),
            Status = ComplaintStatus.New,
            Latitude = request.Latitude,
            Longitude = request.Longitude,
            AddressText = string.IsNullOrWhiteSpace(request.AddressText)
                ? null
                : request.AddressText.Trim(),
            CreatedAt = now,

            // Stored, not computed: editing the category's SlaHours later must not move a
            // deadline a citizen has already been shown (§8.3, §11.1).
            SlaDueAt = SlaPolicy.DueAt(now, category.SlaHours),
        };

        db.Complaints.Add(complaint);

        db.ComplaintStatusHistories.Add(new ComplaintStatusHistory
        {
            Id = Guid.CreateVersion7(),
            ComplaintId = complaint.Id,

            // Nothing moved — the complaint came into existence as New. FromStatus mirrors
            // ToStatus for the same reason a recategorize does (§8.5).
            FromStatus = ComplaintStatus.New,
            ToStatus = ComplaintStatus.New,
            Action = ComplaintAction.Submit,
            ChangedById = currentUser.Id,
            ChangedAt = now,
            IsSystem = false,
        });

        db.Notifications.Add(new Notification
        {
            Id = Guid.CreateVersion7(),
            RecipientId = currentUser.Id,
            ComplaintId = complaint.Id,
            Type = NotificationType.ComplaintSubmitted,
            Subject = $"Complaint {complaint.ReferenceNumber} received",
            Body =
                $"We have received your complaint \"{complaint.Title}\" and routed it to the "
                + "responsible department. You can track it with your reference number.",
            CreatedAt = now,
        });

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return await GetAsync(complaint.Id, cancellationToken);
    }

    /// <summary>
    /// F5 and §13.3. Scoped first, filtered second — in that order, so no filter can widen
    /// what the caller may see.
    /// </summary>
    public async Task<Page<ComplaintListItem>> ListAsync(
        ComplaintListQuery query,
        CancellationToken cancellationToken = default)
    {
        // §13.1: over the max is a 400, **not** a silent clamp. A caller who asked for 500
        // rows and quietly received 100 would page through the rest wrongly and never know
        // — the clamp is the more hostile of the two behaviours, not the friendlier one.
        if (query.PageSize > ComplaintListQuery.MaxPageSize)
        {
            throw new ValidationException(
                "pageSize",
                $"pageSize must be at most {ComplaintListQuery.MaxPageSize}.");
        }

        if (query.Page < 1)
        {
            throw new ValidationException("page", "page is 1-based and must be at least 1.");
        }

        var page = query.Page;

        // An absent or zero pageSize means "use the default", which is a different thing
        // from asking for too many.
        var pageSize = query.PageSize <= 0 ? ComplaintListQuery.DefaultPageSize : query.PageSize;

        var scoped = ComplaintQueryScope.For(db.Complaints.AsNoTracking(), currentUser);
        scoped = ComplaintQueryScope.WithRequestedDepartment(
            scoped,
            currentUser,
            query.DepartmentId);

        var filtered = ApplyFilters(scoped, query);
        var total = await filtered.CountAsync(cancellationToken);

        var rows = await ApplySort(filtered, query.Sort)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new
            {
                c.Id,
                c.ReferenceNumber,
                c.Title,
                CategoryName = c.Category!.Name,
                DepartmentName = c.Department!.Name,
                c.Status,
                c.Priority,
                c.CreatedAt,
                c.SlaDueAt,
                c.SlaBreachedAt,
                c.SlaWarnedAt,
                c.EscalationLevel,
                c.ResolvedAt,
                c.CitizenId,
                c.AssignedStaffId,
                // A subquery rather than a navigation: `User` extends `IdentityUser<Guid>`
                // and lives in Infrastructure (D13), so `Complaint` has no property to
                // traverse. Npgsql folds this into a LATERAL join.
                AssignedStaffName = db.Users
                    .Where(u => u.Id == c.AssignedStaffId)
                    .Select(u => u.FullName)
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        // availableActions is computed after materialization: the guard table is a pure
        // C# dictionary and there is no translating it to SQL. It is the same call the
        // detail read makes, so a list button and a detail button can never disagree.
        var items = rows
            .Select(c => new ComplaintListItem(
                c.Id,
                c.ReferenceNumber,
                c.Title,
                c.CategoryName,
                c.DepartmentName,
                c.Status,
                c.Priority,
                c.CreatedAt,
                c.SlaDueAt,
                c.SlaBreachedAt,
                c.SlaWarnedAt,
                c.EscalationLevel,
                c.ResolvedAt,
                c.AssignedStaffName,
                AvailableActions(c.Status, c.AssignedStaffId, c.CitizenId)))
            .ToList();

        return new Page<ComplaintListItem>(items, page, pageSize, total);
    }

    /// <summary>
    /// F5. A complaint outside the caller's scope is <c>404</c>, never <c>403</c> — the
    /// scope filter simply returns nothing and this method cannot tell the difference
    /// between "not yours" and "does not exist". That is the point (§9.2).
    /// </summary>
    public Task<ComplaintDetail> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default) =>
        ReadAsync(ComplaintQueryScope.For(db.Complaints.AsNoTracking(), currentUser), id, cancellationToken);

    /// <summary>
    /// The same read, for the response to a write this caller has **already been authorised
    /// to make** — the one place the scope is deliberately not re-applied.
    ///
    /// A <c>recategorize</c> can reroute a complaint into another department (§12.4), and
    /// after it commits the admin who performed it is legitimately out of scope. Re-reading
    /// through the seam would then answer <c>404</c> to a request that succeeded, telling
    /// the caller the opposite of the truth about their own action.
    ///
    /// This is not a hole in §9.3: authorization already happened, against the pre-write
    /// state, when <c>ComplaintTransitionService</c> loaded the row through the seam. No
    /// caller reaches this without having just legally mutated this exact complaint.
    /// </summary>
    internal async Task<ComplaintDetail> GetAfterWriteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var detail = await ReadAsync(db.Complaints.AsNoTracking(), id, cancellationToken);

        // …but `availableActions` is still an answer about *this* caller, and the guard
        // table cannot see departments. A recategorize that rerouted the complaint away
        // would otherwise come back offering `assign` on a complaint whose next request is
        // a 404. Out of scope now means nothing is available now.
        var stillVisible = await ComplaintQueryScope
            .For(db.Complaints.AsNoTracking(), currentUser)
            .AnyAsync(c => c.Id == id, cancellationToken);

        return stillVisible ? detail : detail with { AvailableActions = [] };
    }

    private async Task<ComplaintDetail> ReadAsync(
        IQueryable<Complaint> source,
        Guid id,
        CancellationToken cancellationToken)
    {
        var row = await source
            .Where(c => c.Id == id)
            .Select(c => new DetailRow(
                c.CitizenId,
                c.Status,
                c.AssignedStaffId,
                new ComplaintDetail(
                c.Id,
                c.ReferenceNumber,
                c.Title,
                c.Description,
                c.CategoryId,
                c.Category!.Name,
                c.DepartmentId,
                c.Department!.Name,
                c.Status,
                c.Priority,
                c.Latitude,
                c.Longitude,
                c.AddressText,
                c.AssignedStaffId,
                db.Users
                    .Where(u => u.Id == c.AssignedStaffId)
                    .Select(u => u.FullName)
                    .FirstOrDefault(),
                c.CreatedAt,
                c.SlaDueAt,
                c.SlaWarnedAt,
                c.SlaBreachedAt,
                c.EscalationLevel,
                c.ResolvedAt,
                c.ClosedAt,
                c.RejectionReason,
                c.ResolutionNote,
                c.ReopenCount,

                // Filled in below — the guard table does not translate to SQL.
                Array.Empty<ComplaintAction>(),
                c.History
                    .OrderBy(h => h.ChangedAt)
                    .Select(h => new ComplaintHistoryEntry(
                        h.Id,
                        h.FromStatus,
                        h.ToStatus,
                        h.Action,
                        db.Users
                            .Where(u => u.Id == h.ChangedById)
                            .Select(u => u.FullName)
                            .FirstOrDefault(),
                        h.ChangedAt,
                        h.Note,
                        h.IsSystem))
                    .ToList(),

                // Empty until the M7 sweeper writes one; the timeline merges the two lists
                // client-side rather than the API inventing a union type for them.
                c.Escalations
                    .OrderBy(e => e.RaisedAt)
                    .Select(e => new EscalationEntry(e.Id, e.Level, e.RaisedAt))
                    .ToList())))
            .SingleOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            throw new NotFoundException($"Complaint '{id}' was not found.");
        }

        return row.Detail with
        {
            AvailableActions = AvailableActions(row.Status, row.AssignedStaffId, row.CitizenId),
        };
    }

    /// <summary>
    /// What this caller may do, straight from the guard table. Ownership is passed in
    /// because §12.3 cannot express it — the table does not know who the citizen is.
    /// </summary>
    private IReadOnlyList<ComplaintAction> AvailableActions(
        ComplaintStatus status,
        Guid? assignedStaffId,
        Guid citizenId) =>
        ComplaintStateMachine.AvailableActions(
            status,
            currentUser.Role,
            currentUser.Id,
            assignedStaffId,
            isOwner: citizenId == currentUser.Id);

    /// <summary>
    /// Carries the three fields the guard table needs alongside the projection. They are
    /// not on <see cref="ComplaintDetail"/> itself: <c>CitizenId</c> is identity a Staff
    /// caller has no business receiving, and the other two are already there.
    /// </summary>
    private record DetailRow(
        Guid CitizenId,
        ComplaintStatus Status,
        Guid? AssignedStaffId,
        ComplaintDetail Detail);

    /// <summary>The history alone, for the timeline. Same scope, same 404 rule.</summary>
    public async Task<IReadOnlyList<ComplaintHistoryEntry>> GetHistoryAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var exists = await ComplaintQueryScope
            .For(db.Complaints.AsNoTracking(), currentUser)
            .AnyAsync(c => c.Id == id, cancellationToken);

        if (!exists)
        {
            throw new NotFoundException($"Complaint '{id}' was not found.");
        }

        return await db.ComplaintStatusHistories
            .AsNoTracking()
            .Where(h => h.ComplaintId == id)
            .OrderBy(h => h.ChangedAt)
            .Select(h => new ComplaintHistoryEntry(
                h.Id,
                h.FromStatus,
                h.ToStatus,
                h.Action,
                db.Users.Where(u => u.Id == h.ChangedById).Select(u => u.FullName).FirstOrDefault(),
                h.ChangedAt,
                h.Note,
                h.IsSystem))
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// §9.5 — the single unauthenticated read, and the only query in this service that does
    /// not pass through <see cref="ComplaintQueryScope"/>, because there is no caller to
    /// scope to. The redaction *is* the boundary: a reference number is guessable, so this
    /// projection may never grow an identity field.
    /// </summary>
    public async Task<PublicComplaint> GetByReferenceAsync(
        string referenceNumber,
        CancellationToken cancellationToken = default)
    {
        // Truncated before it can reach a ProblemDetails `detail` or a log line: the route
        // value is caller-controlled and unbounded, and this is the one endpoint an
        // unauthenticated stranger can call.
        var normalized = referenceNumber.Trim().ToUpperInvariant();
        var forMessage = normalized.Length > 32 ? normalized[..32] + "…" : normalized;

        var complaint = await db.Complaints
            .AsNoTracking()
            .Where(c => c.ReferenceNumber == normalized)
            .Select(c => new PublicComplaint(
                c.ReferenceNumber,
                c.Category!.Name,
                c.Department!.Name,
                c.Status,
                c.CreatedAt,
                c.SlaDueAt,

                // Historical: a breach survives resolution, so a resolved-late complaint
                // still reports true (§13.3).
                c.SlaBreachedAt != null,
                c.ResolvedAt,
                c.History
                    .OrderBy(h => h.ChangedAt)
                    .Select(h => new PublicHistoryEntry(h.ChangedAt, h.ToStatus))
                    .ToList()))
            .SingleOrDefaultAsync(cancellationToken);

        return complaint
            ?? throw new NotFoundException($"No complaint matches reference '{forMessage}'.");
    }

    // ---------------------------------------------------------------------------------

    private IQueryable<Complaint> ApplyFilters(IQueryable<Complaint> source, ComplaintListQuery query)
    {
        if (query.Status is { Length: > 0 } statuses)
        {
            source = source.Where(c => statuses.Contains(c.Status));
        }

        if (query.Priority is { Length: > 0 } priorities)
        {
            source = source.Where(c => priorities.Contains(c.Priority));
        }

        if (query.CategoryId is { } categoryId)
        {
            source = source.Where(c => c.CategoryId == categoryId);
        }

        // Staff and DeptAdmin only. A Citizen has no assignment, so honouring it for them
        // would silently empty their list rather than mean anything.
        if (query.AssignedToMe == true && currentUser.Role != Domain.Users.UserRole.Citizen)
        {
            source = source.Where(c => c.AssignedStaffId == currentUser.Id);
        }

        if (query.Unassigned == true && currentUser.Role == Domain.Users.UserRole.DeptAdmin)
        {
            source = source.Where(c => c.AssignedStaffId == null);
        }

        if (query.From is { } from)
        {
            source = source.Where(c => c.CreatedAt >= from);
        }

        if (query.To is { } to)
        {
            source = source.Where(c => c.CreatedAt <= to);
        }

        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            // ILIKE, not LOWER(...) LIKE: comparison is case-sensitive in PostgreSQL and
            // lowering both sides cannot use an index (§8.12).
            var pattern = $"%{query.Q.Trim()}%";
            source = source.Where(c =>
                EF.Functions.ILike(c.Title, pattern)
                || EF.Functions.ILike(c.Description, pattern)
                || EF.Functions.ILike(c.ReferenceNumber, pattern)
                || (c.AddressText != null && EF.Functions.ILike(c.AddressText, pattern)));
        }

        return ApplySlaState(source, query.SlaState);
    }

    /// <summary>
    /// §13.3's asymmetry, in one place. <c>breached</c> reads the stored marker and ignores
    /// status entirely; <c>ok</c> and <c>warning</c> are windows on an open complaint.
    /// </summary>
    private IQueryable<Complaint> ApplySlaState(IQueryable<Complaint> source, SlaState? slaState)
    {
        if (slaState is null)
        {
            return source;
        }

        var now = timeProvider.GetUtcNow();

        return slaState switch
        {
            SlaState.Breached => source.Where(c => c.SlaBreachedAt != null),

            SlaState.Warning => source.Where(c =>
                c.ResolvedAt == null
                && c.SlaBreachedAt == null
                && c.SlaWarnedAt != null
                && now < c.SlaDueAt),

            SlaState.Ok => source.Where(c =>
                c.ResolvedAt == null && c.SlaBreachedAt == null && c.SlaWarnedAt == null),

            _ => source,
        };
    }

    /// <summary>
    /// §13.3: <c>createdAt</c>, <c>slaDueAt</c> or <c>priority</c>, <c>-</c> prefix for
    /// descending, default <c>-createdAt</c>. An unrecognised value falls back to the
    /// default rather than erroring — a bad sort is a cosmetic problem, not a failed request.
    /// </summary>
    private static IQueryable<Complaint> ApplySort(IQueryable<Complaint> source, string? sort)
    {
        var descending = sort?.StartsWith('-') == true;
        var field = sort?.TrimStart('-').ToLowerInvariant();

        return field switch
        {
            "sladueat" => descending
                ? source.OrderByDescending(c => c.SlaDueAt)
                : source.OrderBy(c => c.SlaDueAt),

            "priority" => descending
                ? source.OrderByDescending(c => c.Priority).ThenByDescending(c => c.CreatedAt)
                : source.OrderBy(c => c.Priority).ThenByDescending(c => c.CreatedAt),

            "createdat" => descending
                ? source.OrderByDescending(c => c.CreatedAt)
                : source.OrderBy(c => c.CreatedAt),

            _ => source.OrderByDescending(c => c.CreatedAt),
        };
    }

    /// <summary>
    /// §8.10. <c>nextval</c> in the same transaction as the insert, and deliberately not a
    /// read-modify-write over a counter table: the sequence is global and never resets, so
    /// two complaints cannot collide even across a year boundary.
    ///
    /// <c>nextval</c> is exempt from rollback by design. An abandoned insert burns a number
    /// and leaves a gap — expected, not a defect. The reference is an identifier, not a count.
    /// </summary>
    private async Task<string> NextReferenceNumberAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // The sequence name is a compile-time constant, never caller input.
        var next = await db.Database
            .SqlQueryRaw<long>(
                $"SELECT nextval('{ObhijogDbContext.ComplaintReferenceSequence}') AS \"Value\"")
            .SingleAsync(cancellationToken);

        return $"MC-{now:yyyy}-{next:D6}";
    }
}
