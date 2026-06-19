using EpcForwarder.Core.Query;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace EpcForwarder.Functions.Api;

/// <summary>端末向け HTTP クエリAPI。AuthorizationLevel.Function(Functionsキー)＋ X-EPCF-Tenant 突合。</summary>
public sealed class SessionQueryApi(SessionQueryService queries)
{
    [Function("GetSummary")]
    public IActionResult GetSummary(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "sessions/{publicId:guid}/summary")] HttpRequest req,
        Guid publicId)
    {
        if (Validate(req, publicId, out var tenantId) is { } error)
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
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "sessions/{publicId:guid}/reconciliation")] HttpRequest req,
        Guid publicId)
    {
        if (Validate(req, publicId, out var tenantId) is { } error)
        {
            return error;
        }

        var view = queries.GetReconciliation(tenantId, publicId);
        return view is null ? new NotFoundResult() : new OkObjectResult(ApiResponses.ToDto(view));
    }

    [Function("GetUnknown")]
    public IActionResult GetUnknown(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "sessions/{publicId:guid}/unknown")] HttpRequest req,
        Guid publicId)
    {
        if (Validate(req, publicId, out var tenantId) is { } error)
        {
            return error;
        }

        var view = queries.GetUnknown(tenantId, publicId);
        return view is null ? new NotFoundResult() : new OkObjectResult(ApiResponses.ToDto(view));
    }

    // tenant ヘッダ＋publicId を検証。エラーなら IActionResult を返す(成功時 null)。
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
