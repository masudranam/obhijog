using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace Obhijog.Api.Startup;

/// <summary>
/// Ensures the attachment container exists before the app reports itself ready.
///
/// In Azure the container is created by Bicep (SPEC.md §14 F15), so this is a no-op
/// there. Locally, <c>infra/docker-compose.yml</c> starts an empty Azurite and nothing
/// else would ever create it — which left <c>/health/ready</c> permanently 503 and the
/// M1 Definition of Done unmet.
///
/// <see cref="PublicAccessType.None"/> is not a default worth relying on: SPEC.md §17
/// requires the container to be private, with reads only through a short-lived SAS.
/// </summary>
public static class BlobContainerInitializer
{
    public static async Task EnsureContainerAsync(
        BlobContainerClient container,
        CancellationToken cancellationToken = default)
    {
        await container.CreateIfNotExistsAsync(
            PublicAccessType.None,
            cancellationToken: cancellationToken);
    }
}
