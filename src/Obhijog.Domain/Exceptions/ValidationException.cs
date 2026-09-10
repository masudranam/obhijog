namespace Obhijog.Domain.Exceptions;

/// <summary>
/// The request failed validation. Maps to 400 with an errors dictionary.
/// </summary>
public class ValidationException(string message) : Exception(message)
{
}
