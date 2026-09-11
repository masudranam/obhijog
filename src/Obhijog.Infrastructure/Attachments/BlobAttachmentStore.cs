using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Microsoft.Extensions.Options;
using Obhijog.Infrastructure.Options;

namespace Obhijog.Infrastructure.Attachments;

/// <summary>
/// <see cref="IAttachmentStore"/> over Azure Blob Storage, and over Azurite locally — the
/// two differ only in the connection string (SPEC.md §7).
/// </summary>
public class BlobAttachmentStore(
    BlobContainerClient container,
    IOptions<StorageOptions> options,
    TimeProvider timeProvider) : IAttachmentStore
{
    private readonly StorageOptions _storage = options.Value;

    public async Task UploadAsync(
        string blobName,
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        var blob = container.GetBlobClient(blobName);

        await blob.UploadAsync(
            content,
            new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = contentType },
            },
            cancellationToken);
    }

    /// <summary>
    /// A short-lived read-only SAS. The window comes from <c>Storage:ReadSasMinutes</c>, and
    /// starts slightly in the past so a small clock difference between this host and the
    /// storage account does not produce a URL that is not yet valid.
    /// </summary>
    public Uri CreateReadUrl(string blobName)
    {
        var blob = container.GetBlobClient(blobName);

        if (!blob.CanGenerateSasUri)
        {
            // Reached when the client was built from a token credential rather than a
            // shared key — the managed-identity path. That needs a user delegation key,
            // which is an M9 concern along with the rest of the Azure wiring; failing
            // loudly here beats returning a URL that 403s in the browser.
            throw new InvalidOperationException(
                "This BlobContainerClient cannot sign a SAS. A shared-key connection string "
                + "is required until user delegation keys are wired up (SPEC.md §17, M9).");
        }

        var builder = new BlobSasBuilder
        {
            BlobContainerName = container.Name,
            BlobName = blobName,
            Resource = "b",
            StartsOn = timeProvider.GetUtcNow().AddMinutes(-5),
            ExpiresOn = timeProvider.GetUtcNow().AddMinutes(_storage.ReadSasMinutes),
        };

        builder.SetPermissions(BlobSasPermissions.Read);

        return blob.GenerateSasUri(builder);
    }

    public async Task DeleteAsync(string blobName, CancellationToken cancellationToken = default) =>
        await container.DeleteBlobIfExistsAsync(
            blobName,
            DeleteSnapshotsOption.IncludeSnapshots,
            cancellationToken: cancellationToken);
}
