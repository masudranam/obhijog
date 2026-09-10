namespace Obhijog.Domain.Exceptions;

/// <summary>
/// The requested action is not a legal edge of the guard table for this complaint's current state, role or payload. Maps to 409. SPEC.md §12.
/// </summary>
public class InvalidTransitionException(string message) : Exception(message)
{
}
