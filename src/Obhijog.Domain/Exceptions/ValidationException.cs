namespace Obhijog.Domain.Exceptions;

/// <summary>
/// The request failed validation. Maps to <c>400</c> with the <c>errors</c> dictionary
/// SPEC.md §13.1 promises on every validation failure.
///
/// The dictionary is keyed by field name and matches the shape ASP.NET already produces for
/// model-state failures, so a client has one error format to handle rather than two.
/// </summary>
public class ValidationException : Exception
{
    public ValidationException(string message)
        : base(message) => Errors = new Dictionary<string, string[]>();

    /// <summary>A single field's failure — the common case.</summary>
    public ValidationException(string field, string message)
        : base(message) => Errors = new Dictionary<string, string[]> { [field] = [message] };

    public ValidationException(IDictionary<string, string[]> errors)
        : base("One or more validation errors occurred.") => Errors = errors;

    public IDictionary<string, string[]> Errors { get; }
}
