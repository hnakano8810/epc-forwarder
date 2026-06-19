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
        if (!RequestTenant.TryGet(req, out var tenantId))
        {
            return new BadRequestObjectResult($"Missing or invalid {RequestTenant.HeaderName} header.");
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
        if (!RequestTenant.TryGet(req, out var tenantId))
        {
            return new BadRequestObjectResult($"Missing or invalid {RequestTenant.HeaderName} header.");
        }

        var view = queries.GetReconciliation(tenantId, publicId);
        return view is null ? new NotFoundResult() : new OkObjectResult(ApiResponses.ToDto(view));
    }

    [Function("GetUnknown")]
    public IActionResult GetUnknown(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "sessions/{publicId:guid}/unknown")] HttpRequest req,
        Guid publicId)
    {
        if (!RequestTenant.TryGet(req, out var tenantId))
        {
            return new BadRequestObjectResult($"Missing or invalid {RequestTenant.HeaderName} header.");
        }

        var view = queries.GetUnknown(tenantId, publicId);
        return view is null ? new NotFoundResult() : new OkObjectResult(ApiResponses.ToDto(view));
    }
}
