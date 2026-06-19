using EpcForwarder.Core.Inventory;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace EpcForwarder.Functions.Api;

/// <summary>棚卸の HTTP 起動(仮確定/確定)。tenant 突合＋宛先解決は InventoryDispatcher に委譲。</summary>
public sealed class InventoryApi(InventoryDispatcher inventory)
{
    [Function("InventoryProvisional")]
    public Task<IActionResult> SendProvisional(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "sessions/{publicId:guid}/inventory/provisional")] HttpRequest req,
        Guid publicId,
        CancellationToken ct = default) =>
        RunAsync(req, publicId, ct, (tenant, c) => inventory.SendProvisionalAsync(tenant, publicId, c));

    [Function("InventoryFinalize")]
    public Task<IActionResult> Finalize(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "sessions/{publicId:guid}/inventory/finalize")] HttpRequest req,
        Guid publicId,
        CancellationToken ct = default) =>
        RunAsync(req, publicId, ct, (tenant, c) => inventory.FinalizeAndDeliverAsync(tenant, publicId, c));

    private static async Task<IActionResult> RunAsync(
        HttpRequest req,
        Guid publicId,
        CancellationToken ct,
        Func<int, CancellationToken, Task<InventoryPublishOutcome?>> publish)
    {
        if (Validate(req, publicId, out var tenantId) is { } error)
        {
            return error;
        }

        try
        {
            var outcome = await publish(tenantId, ct);
            if (outcome is null)
            {
                return new NotFoundResult();
            }

            return new OkObjectResult(new InventoryResultDto
            {
                Delivered = outcome.Delivered,
                StatusCode = outcome.Delivery?.StatusCode,
            });
        }
        catch (InvalidOperationException ex)
        {
            // 状態不正(仮確定: open でない / 確定: 既 forwarded)。
            return new ConflictObjectResult(ex.Message);
        }
    }

    private static IActionResult? Validate(HttpRequest req, Guid publicId, out int tenantId)
    {
        tenantId = 0;
        if (publicId == Guid.Empty)
        {
            return new BadRequestObjectResult("Invalid session id.");
        }

        if (!RequestTenant.TryGet(req, out tenantId))
        {
            return new BadRequestObjectResult($"Missing or invalid {RequestTenant.HeaderName} header.");
        }

        return null;
    }
}
