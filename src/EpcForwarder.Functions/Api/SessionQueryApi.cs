using EpcForwarder.Core.Query;
using EpcForwarder.Functions.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace EpcForwarder.Functions.Api;

/// <summary>端末向け HTTP クエリAPI。認証ミドルウェア解決済み tenant を FunctionContext 経由で取得。</summary>
public sealed class SessionQueryApi(SessionQueryService queries)
{
    [Function("GetSummary")]
    public IActionResult GetSummary(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sessions/{publicId:guid}/summary")] HttpRequest req,
        Guid publicId,
        FunctionContext context)
    {
        if (Validate(context, publicId, out var tenantId) is { } error)
        {
            return error;
        }

        if (string.Equals(req.Query["groupBy"], "location", StringComparison.OrdinalIgnoreCase))
        {
            var loc = queries.GetLocationSummary(tenantId, publicId);
            return loc is null ? new NotFoundResult() : new OkObjectResult(ApiResponses.ToDto(loc));
        }

        var summary = queries.GetSummary(tenantId, publicId);
        return summary is null ? new NotFoundResult() : new OkObjectResult(ApiResponses.ToDto(summary));
    }

    [Function("GetReconciliation")]
    public IActionResult GetReconciliation(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sessions/{publicId:guid}/reconciliation")] HttpRequest req,
        Guid publicId,
        FunctionContext context)
    {
        if (Validate(context, publicId, out var tenantId) is { } error)
        {
            return error;
        }

        var view = queries.GetReconciliation(tenantId, publicId);
        return view is null ? new NotFoundResult() : new OkObjectResult(ApiResponses.ToDto(view));
    }

    [Function("GetUnknown")]
    public IActionResult GetUnknown(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sessions/{publicId:guid}/unknown")] HttpRequest req,
        Guid publicId,
        FunctionContext context)
    {
        if (Validate(context, publicId, out var tenantId) is { } error)
        {
            return error;
        }

        var view = queries.GetUnknown(tenantId, publicId);
        return view is null ? new NotFoundResult() : new OkObjectResult(ApiResponses.ToDto(view));
    }

    // tenant は認証ミドルウェアが解決済み。publicId のみ検証。
    private static IActionResult? Validate(FunctionContext context, Guid publicId, out int tenantId)
    {
        tenantId = 0;
        if (publicId == Guid.Empty)
        {
            return new BadRequestObjectResult("Invalid session id.");
        }

        if (!AuthenticatedTenant.TryGet(context, out tenantId))
        {
            // ミドルウェアを通っていれば通常ここには来ない(防御的に 401)。
            return new UnauthorizedResult();
        }

        return null;
    }
}
