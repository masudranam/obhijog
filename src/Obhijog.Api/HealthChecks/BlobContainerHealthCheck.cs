using Azure.Storage.Blobs;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Obhijog.Api.HealthChecks;

/// <summary>
/// Readiness check for the attachment container. SPEC.md §14 F1 requires
/// <c>/health/ready</c> to check the blob container, not merely the storage account —
/// a reachable account with a missing container still fails every upload.
/// </summary>
public sealed class BlobContainerHealthCheck(BlobContainerClient container) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var exists = await container.ExistsAsync(cancellationToken);

            return exists.Value
                ? HealthCheckResult.Healthy($"Container '{container.Name}' is reachable.")
                : HealthCheckResult.Unhealthy($"Container '{container.Name}' does not exist.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Blob storage is not reachable.", ex);
        }
    }
}
