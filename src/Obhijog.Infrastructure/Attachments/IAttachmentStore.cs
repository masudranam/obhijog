namespace Obhijog.Infrastructure.Attachments;

/// <summary>
/// The blob store, behind an interface. SPEC.md §7 (decision) and §18.
///
/// Azurite and Azure Blob differ only in a connection string, so the interface does not
/// exist to swap providers — it exists so the tests never need either. §18 is explicit that
/// no test spins up Azurite; this is what gets doubled instead.
/// </summary>
public interface IAttachmentStore
{
    Task UploadAsync(
        string blobName,
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// A read-only URL valid for <c>Storage:ReadSasMinutes</c>. The container is private, so
    /// this is the only way a browser can fetch the bytes — a direct container URL fails
    /// (F6).
    /// </summary>
    Uri CreateReadUrl(string blobName);

    Task DeleteAsync(string blobName, CancellationToken cancellationToken = default);
}
