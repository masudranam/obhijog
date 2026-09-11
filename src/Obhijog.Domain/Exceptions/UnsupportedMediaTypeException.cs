namespace Obhijog.Domain.Exceptions;

/// <summary>
/// The upload's content type is not on the allow-list. Maps to <c>415</c>.
///
/// Separate from <see cref="ValidationException"/> because the status differs and F6 names
/// it: a caller who sends a PDF has not filled a field in wrongly, they have sent a kind of
/// thing this endpoint does not accept.
/// </summary>
public class UnsupportedMediaTypeException(string message) : Exception(message)
{
}
