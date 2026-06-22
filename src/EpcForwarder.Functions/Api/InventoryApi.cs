using EpcForwarder.Core.Inventory;
using EpcForwarder.Functions.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace EpcForwarder.Functions.Api;

/// <summary>棚卸の HTTP 起動(仮確定/確定)。tenant は認証ミドルウェア解決済み。</summary>
public sealed class InventoryApi(InventoryDispatcher inventory)
{
    [Function("InventoryProvisional")]
    public Task<IActionResult> SendProvisional(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "sessions/{publicId:guid}/inventory/provisional")] HttpRequest req,
        Guid publicId,
        FunctionContext context,
        CancellationToken ct = default) =>
        RunAsync(context, publicId, ct, (tenant, c) => inventory.SendProvisionalAsync(tenant, publicId, c));

    [Function("InventoryFinalize")]
    public Task<IActionResult> Finalize(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "sessions/{publicId:guid}/inventory/finalize")] HttpRequest req,
        Guid publicId,
        FunctionContext context,
        CancellationToken ct = default) =>
        RunAsync(context, publicId, ct, (tenant, c) => inventory.FinalizeAndDeliverAsync(tenant, publicId, c));

    private static async Task<IActionResult> RunAsync(
        FunctionContext context,
        Guid publicId,
        CancellationToken ct,
        Func<int, CancellationToken, Task<InventoryPublishOutcome?>> publish)
    {
        if (Validate(context, publicId, out var tenantId) is { } error)
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

    private static IActionResult? Validate(FunctionContext context, Guid publicId, out int tenantId)
    {
        tenantId = 0;
        if (publicId == Guid.Empty)
        {
            return new BadRequestObjectResult("Invalid session id.");
        }

        if (!AuthenticatedTenant.TryGet(context, out tenantId))
        {
            return new UnauthorizedResult();
        }

        return null;
    }
}
