using Microsoft.Extensions.Options;
using Obhijog.Domain.Exceptions;
using Obhijog.Infrastructure.Attachments;
using Obhijog.Infrastructure.Options;

namespace Obhijog.Api.Endpoints;

/// <summary>
/// Photo attachments. SPEC.md §13.2, F6.
///
/// Every route hangs off a complaint, because an attachment's visibility is the
/// complaint's: the service asks <c>ComplaintQueryScope</c> first and a complaint the
/// caller cannot see is <c>404</c> (§9.2).
/// </summary>
public static class AttachmentEndpoints
{
    public static RouteGroupBuilder MapAttachmentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/complaints/{id:guid}/attachments").WithTags("attachments");

        group.MapGet("/", ListAsync).RequireAuthorization();
        group.MapGet("/{attachmentId:guid}", GetAsync).RequireAuthorization();

        group.MapPost("/", UploadAsync)
            .RequireAuthorization()
            .DisableAntiforgery();

        return group;
    }

    private static async Task<IResult> ListAsync(
        Guid id,
        AttachmentService attachments,
        CancellationToken cancellationToken) =>
        Results.Ok(await attachments.ListAsync(id, cancellationToken));

    /// <summary>
    /// F6: <c>302</c> to a short-lived read SAS rather than streaming the bytes. The
    /// container is private, so a direct URL without the SAS fails — and the API does not
    /// become a proxy for every thumbnail on the screen.
    /// </summary>
    private static async Task<IResult> GetAsync(
        Guid id,
        Guid attachmentId,
        AttachmentService attachments,
        CancellationToken cancellationToken)
    {
        var url = await attachments.CreateReadUrlAsync(id, attachmentId, cancellationToken);

        return Results.Redirect(url.ToString(), permanent: false);
    }

    /// <summary>
    /// One file per request, `multipart/form-data`.
    ///
    /// The size limit is enforced twice on purpose: the framework's request-size limit stops
    /// a huge body before it is buffered, and the service checks the declared length so the
    /// caller gets a <c>413</c> with a ProblemDetails rather than a connection reset.
    /// </summary>
    private static async Task<IResult> UploadAsync(
        Guid id,
        IFormFile? file,
        AttachmentService attachments,
        IOptions<AttachmentOptions> options,
        CancellationToken cancellationToken)
    {
        if (file is null)
        {
            throw new ValidationException("file", "A file is required.");
        }

        await using var content = file.OpenReadStream();

        var uploaded = await attachments.UploadAsync(
            id,
            new AttachmentUpload(
                file.FileName ?? string.Empty,
                file.ContentType ?? string.Empty,
                file.Length,
                content),
            cancellationToken);

        return Results.Created($"/api/v1/complaints/{id}/attachments/{uploaded.Id}", uploaded);
    }
}
