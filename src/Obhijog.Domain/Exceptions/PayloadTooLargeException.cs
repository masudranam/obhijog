namespace Obhijog.Domain.Exceptions;

/// <summary>The upload exceeds <c>Attachments:MaxSizeBytes</c>. Maps to <c>413</c>.</summary>
public class PayloadTooLargeException(string message) : Exception(message)
{
}
