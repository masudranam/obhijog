namespace Obhijog.Domain.Exceptions;

/// <summary>
/// The resource does not exist, or is outside the caller's scope. Maps to 404. SPEC.md §9.2 makes the second case a 404 rather than a 403 — a 403 would confirm the row exists.
/// </summary>
public class NotFoundException(string message) : Exception(message)
{
}
