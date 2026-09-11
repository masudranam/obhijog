using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Obhijog.Domain.Exceptions;
using Obhijog.Infrastructure.Auth;

namespace Obhijog.Api.Errors;

/// <summary>
/// The single exception-to-status mapper. SPEC.md §16.4 and §13.1.
///
/// One handler owns the whole mapping, so no endpoint needs a <c>try/catch</c> that
/// returns <c>BadRequest</c> — and, more importantly, so the §9.2 invariant (out of scope
/// is <c>404</c>, never <c>403</c>) is decided in one readable place rather than per route.
/// </summary>
public class ProblemDetailsExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<ProblemDetailsExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var (status, title) = Map(exception);

        if (status is null)
        {
            // Not ours. Returning false lets the pipeline's developer page or the default
            // 500 handle it — swallowing an unknown exception into a tidy ProblemDetails
            // would hide real faults.
            return false;
        }

        if (status >= StatusCodes.Status500InternalServerError)
        {
            logger.LogError(exception, "Unhandled exception mapped to {Status}", status);
        }
        else
        {
            logger.LogInformation(
                "Request failed with {Status}: {Message}",
                status,
                exception.Message);
        }

        context.Response.StatusCode = status.Value;

        var problemDetails = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = exception.Message,
            Type = $"https://httpstatuses.io/{status}",
        };

        // §13.1: a validation failure carries `errors`, keyed by field. Only that case —
        // attaching an empty dictionary to every other error would invite clients to read
        // it as "no field was at fault" rather than "this is not a field-level failure".
        if (exception is ValidationException { Errors.Count: > 0 } validation)
        {
            problemDetails.Extensions["errors"] = validation.Errors;
        }

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            Exception = exception,
            ProblemDetails = problemDetails,
        });
    }

    private static (int? Status, string Title) Map(Exception exception) => exception switch
    {
        NotFoundException => (StatusCodes.Status404NotFound, "Not found"),
        ForbiddenException => (StatusCodes.Status403Forbidden, "Forbidden"),
        InvalidTransitionException => (StatusCodes.Status409Conflict, "Invalid transition"),
        DbUpdateConcurrencyException => (StatusCodes.Status409Conflict, "Concurrency conflict"),
        ValidationException => (StatusCodes.Status400BadRequest, "Validation failed"),

        // F6. Both are validation in spirit but carry their own status, so they cannot be
        // folded into ValidationException without losing the code the client checks.
        UnsupportedMediaTypeException =>
            (StatusCodes.Status415UnsupportedMediaType, "Unsupported media type"),
        PayloadTooLargeException => (StatusCodes.Status413PayloadTooLarge, "File too large"),

        // §10.1: reuse is a 401 to the caller. The family revocation has already happened
        // inside TokenService; the response deliberately does not say so.
        RefreshTokenReuseException => (StatusCodes.Status401Unauthorized, "Invalid refresh token"),

        _ => (null, string.Empty),
    };
}
