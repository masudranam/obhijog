namespace Obhijog.Domain.Exceptions;

/// <summary>
/// The caller can see the resource but may not perform this action. Maps to 403. Never used for a resource outside the caller's scope; that is NotFoundException (§9.2).
/// </summary>
public class ForbiddenException(string message) : Exception(message)
{
}
