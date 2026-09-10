using Obhijog.Api.Auth;
using Obhijog.Domain.Complaints;
using Obhijog.Infrastructure.Complaints;

namespace Obhijog.Api.Endpoints;

/// <summary>
/// Complaints. SPEC.md §13.2, F4, F5.
///
/// Bind, call one service, map a status code — nothing else. No EF query, no scope filter,
/// no decision about whether something is a 404 or a 403; the service throws and the one
/// exception handler maps it (§16.1, §16.4).
/// </summary>
public static class ComplaintEndpoints
{
    public static RouteGroupBuilder MapComplaintEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/complaints").WithTags("complaints");

        // Only Citizens submit. Staff and DeptAdmin filing on someone's behalf would break
        // the ownership model the whole of §9 rests on (F4).
        group.MapPost("/", CreateAsync).RequireAuthorization(Policies.Citizen);

        group.MapGet("/", ListAsync).RequireAuthorization();
        group.MapGet("/{id:guid}", GetAsync).RequireAuthorization();
        group.MapGet("/{id:guid}/history", GetHistoryAsync).RequireAuthorization();

        // The one unauthenticated read in the entire API (§9.5). Registered explicitly as
        // anonymous rather than left to default, so it reads as a decision.
        group.MapGet("/by-reference/{reference}", GetByReferenceAsync).AllowAnonymous();

        return group;
    }

    private static async Task<IResult> CreateAsync(
        CreateComplaintRequest request,
        ComplaintService complaints,
        CancellationToken cancellationToken)
    {
        var created = await complaints.CreateAsync(request, cancellationToken);

        return Results.Created($"/api/v1/complaints/{created.Id}", created);
    }

    /// <summary>
    /// §13.3. `[AsParameters]` binds the whole query surface from the query string, which
    /// keeps the filter list in one record rather than a twelve-parameter signature.
    /// </summary>
    private static async Task<IResult> ListAsync(
        [AsParameters] ComplaintListQueryBinding query,
        ComplaintService complaints,
        CancellationToken cancellationToken) =>
        Results.Ok(await complaints.ListAsync(query.ToQuery(), cancellationToken));

    private static async Task<IResult> GetAsync(
        Guid id,
        ComplaintService complaints,
        CancellationToken cancellationToken) =>
        Results.Ok(await complaints.GetAsync(id, cancellationToken));

    private static async Task<IResult> GetHistoryAsync(
        Guid id,
        ComplaintService complaints,
        CancellationToken cancellationToken) =>
        Results.Ok(await complaints.GetHistoryAsync(id, cancellationToken));

    private static async Task<IResult> GetByReferenceAsync(
        string reference,
        ComplaintService complaints,
        CancellationToken cancellationToken) =>
        Results.Ok(await complaints.GetByReferenceAsync(reference, cancellationToken));
}

/// <summary>
/// The query string as minimal APIs can bind it.
///
/// A separate type from <see cref="ComplaintListQuery"/> because binding and meaning are
/// different jobs: repeatable parameters arrive as arrays of strings that may not parse,
/// and an unparseable `status=banana` should narrow nothing rather than 500. The service
/// receives a parsed, already-sane query.
/// </summary>
public record ComplaintListQueryBinding
{
    public string[]? Status { get; init; }

    public Guid? CategoryId { get; init; }

    public Guid? DepartmentId { get; init; }

    public bool? AssignedToMe { get; init; }

    public bool? Unassigned { get; init; }

    public string? SlaState { get; init; }

    public string[]? Priority { get; init; }

    public string? Q { get; init; }

    public DateTimeOffset? From { get; init; }

    public DateTimeOffset? To { get; init; }

    public string? Sort { get; init; }

    public int? Page { get; init; }

    public int? PageSize { get; init; }

    public ComplaintListQuery ToQuery() => new()
    {
        Status = ParseAll<ComplaintStatus>(Status),
        Priority = ParseAll<ComplaintPriority>(Priority),
        CategoryId = CategoryId,
        DepartmentId = DepartmentId,
        AssignedToMe = AssignedToMe,
        Unassigned = Unassigned,
        SlaState = Enum.TryParse<SlaState>(SlaState, ignoreCase: true, out var state)
            ? state
            : null,
        Q = Q,
        From = From,
        To = To,
        Sort = Sort,
        Page = Page ?? 1,
        PageSize = PageSize ?? ComplaintListQuery.DefaultPageSize,
    };

    /// <summary>
    /// Unparseable values are dropped, not rejected. If every value in a repeatable filter
    /// is junk the filter is absent rather than empty — an empty `IN ()` would return
    /// nothing and look like "you have no complaints".
    /// </summary>
    private static T[]? ParseAll<T>(string[]? values) where T : struct, Enum
    {
        if (values is not { Length: > 0 })
        {
            return null;
        }

        var parsed = values
            .Select(value => Enum.TryParse<T>(value, ignoreCase: true, out var result)
                ? result
                : (T?)null)
            .OfType<T>()
            .Distinct()
            .ToArray();

        return parsed.Length > 0 ? parsed : null;
    }
}
