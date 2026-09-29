namespace AppForge.Api.Features.Provisioning;

internal interface IProvisioningService
{
    ValueTask EnqueueAsync(ProvisioningJob job, CancellationToken ct = default);
}
