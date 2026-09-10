using System.ComponentModel.DataAnnotations;
using Obhijog.Domain.Complaints;

namespace Obhijog.Infrastructure.Complaints;

/// <summary>
/// SPEC.md §13. Records rather than entities: no EF entity is ever returned from an
/// endpoint, and every read is a projection straight to one of these.
/// </summary>
public record CreateComplaintRequest
{
    [Required]
    [MaxLength(140)]
    public required string Title { get; init; }

    [Required]
    [MaxLength(4000)]
    public required string Description { get; init; }

    [Required]
    public Guid CategoryId { get; init; }

    [Range(-90, 90)]
    public decimal Latitude { get; init; }

    [Range(-180, 180)]
    public decimal Longitude { get; init; }

    [MaxLength(250)]
    public string? AddressText { get; init; }
}

/// <summary>One row of the list. Deliberately narrower than the detail projection.</summary>
public record ComplaintListItem(
    Guid Id,
    string ReferenceNumber,
    string Title,
    string CategoryName,
    string DepartmentName,
    ComplaintStatus Status,
    ComplaintPriority Priority,
    DateTimeOffset CreatedAt,
    DateTimeOffset SlaDueAt,
    DateTimeOffset? SlaBreachedAt,
    DateTimeOffset? SlaWarnedAt,
    short EscalationLevel,
    DateTimeOffset? ResolvedAt);

/// <summary>
/// The full read. Carries the SLA fields so the client renders badges from them and never
/// re-derives the 80/100/150 thresholds (§15, CLAUDE.md non-negotiable 6).
///
/// <c>availableActions</c> is absent until M6 builds the guard table. An empty array here
/// would be a lie the UI would render buttons from; the field arrives with the thing that
/// can populate it honestly.
/// </summary>
public record ComplaintDetail(
    Guid Id,
    string ReferenceNumber,
    string Title,
    string Description,
    Guid CategoryId,
    string CategoryName,
    Guid DepartmentId,
    string DepartmentName,
    ComplaintStatus Status,
    ComplaintPriority Priority,
    decimal Latitude,
    decimal Longitude,
    string? AddressText,
    Guid? AssignedStaffId,
    string? AssignedStaffName,
    DateTimeOffset CreatedAt,
    DateTimeOffset SlaDueAt,
    DateTimeOffset? SlaWarnedAt,
    DateTimeOffset? SlaBreachedAt,
    short EscalationLevel,
    DateTimeOffset? ResolvedAt,
    DateTimeOffset? ClosedAt,
    string? RejectionReason,
    string? ResolutionNote,
    int ReopenCount,
    IReadOnlyList<ComplaintHistoryEntry> History);

public record ComplaintHistoryEntry(
    Guid Id,
    ComplaintStatus FromStatus,
    ComplaintStatus ToStatus,
    ComplaintAction Action,
    string? ChangedByName,
    DateTimeOffset ChangedAt,
    string? Note,
    bool IsSystem);

/// <summary>
/// The redacted public projection of §9.5, and the whole security boundary for the one
/// unauthenticated read. A reference number is guessable, so what is *absent* here is the
/// point: no citizen or staff identity, no contact details, no comments, no attachments,
/// no coordinates, no internal notes, no rejection or resolution text.
/// </summary>
public record PublicComplaint(
    string ReferenceNumber,
    string CategoryName,
    string DepartmentName,
    ComplaintStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset SlaDueAt,
    bool IsSlaBreached,
    DateTimeOffset? ResolvedAt,
    IReadOnlyList<PublicHistoryEntry> History);

/// <summary>Only when it moved and what it moved to. No actor, no note.</summary>
public record PublicHistoryEntry(DateTimeOffset ChangedAt, ComplaintStatus ToStatus);

/// <summary>SPEC.md §13.1 — no response envelope; a page is exactly these four fields.</summary>
public record Page<T>(IReadOnlyList<T> Items, int PageNumber, int PageSize, int Total);

/// <summary>
/// The §13.3 query surface. Bound from the query string; every field is optional and the
/// defaults are the ones §13.3 names.
/// </summary>
public record ComplaintListQuery
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    public ComplaintStatus[]? Status { get; init; }

    public Guid? CategoryId { get; init; }

    /// <summary>Ignored unless it equals the caller's own department (§9.3).</summary>
    public Guid? DepartmentId { get; init; }

    public bool? AssignedToMe { get; init; }

    public bool? Unassigned { get; init; }

    public SlaState? SlaState { get; init; }

    public ComplaintPriority[]? Priority { get; init; }

    public string? Q { get; init; }

    public DateTimeOffset? From { get; init; }

    public DateTimeOffset? To { get; init; }

    /// <summary><c>createdAt</c>, <c>slaDueAt</c> or <c>priority</c>, <c>-</c> for descending.</summary>
    public string? Sort { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = DefaultPageSize;
}

/// <summary>
/// §13.3. Not symmetric, and stated as an enum so the asymmetry lives in one place: a
/// breached complaint is not also "warning", and "ok" means neither marker is set.
/// </summary>
public enum SlaState
{
    Ok,
    Warning,
    Breached,
}
