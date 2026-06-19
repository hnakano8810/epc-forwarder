using EpcForwarder.Core.Abstractions;
using EpcForwarder.Core.Epc;
using EpcForwarder.Core.Query;
using EpcForwarder.Core.Sessions;
using EpcForwarder.Core.Tests.Fakes;
using EpcForwarder.Functions.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace EpcForwarder.Functions.Tests.Api;

public class SessionQueryApiTests
{
    private static (SessionQueryApi api, Guid id) BuildWithSession(int tenantId = 1)
    {
        var sessions = new InMemorySessionStore();
        var readings = new InMemoryReadingStore();
        var products = new InMemoryProductCatalog();
        var clock = new FixedClock(DateTimeOffset.UnixEpoch);
        var id = Guid.NewGuid();
        sessions.Save(new Session(id, tenantId, SessionType.Shipment, "BK-1", clock.UtcNow));
        const string epc = "302DB42318A0038000001231";
        var key = Sgtin96.DeriveSearchKey(epc);
        products.Add(tenantId, key, "ITEM-AAA");
        readings.Upsert(id, new ReadingEntry(epc, key, "devA", clock.UtcNow));
        var svc = new SessionQueryService(sessions, readings, products, clock);
        return (new SessionQueryApi(svc), id);
    }

    private static HttpRequest Request(int? tenant)
    {
        var ctx = new DefaultHttpContext();
        if (tenant is not null)
        {
            ctx.Request.Headers[RequestTenant.HeaderName] = tenant.Value.ToString();
        }

        return ctx.Request;
    }

    [Fact]
    public void GetSummary_ValidTenant_ReturnsOkWithDto()
    {
        var (api, id) = BuildWithSession();
        var result = api.GetSummary(Request(tenant: 1), id);

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<SummaryDto>(ok.Value);
        Assert.Equal(id, dto.SessionId);
        Assert.Equal("ITEM-AAA", Assert.Single(dto.Items).Sku);
    }

    [Fact]
    public void GetSummary_MissingTenantHeader_ReturnsBadRequest()
    {
        var (api, id) = BuildWithSession();
        var result = api.GetSummary(Request(tenant: null), id);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void GetSummary_WrongTenant_ReturnsNotFound()
    {
        var (api, id) = BuildWithSession(tenantId: 1);
        var result = api.GetSummary(Request(tenant: 2), id);
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public void GetSummary_GroupByLocation_ReturnsLocationDto()
    {
        var (api, id) = BuildWithSession();
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers[RequestTenant.HeaderName] = "1";
        ctx.Request.QueryString = new QueryString("?groupBy=location");

        var result = api.GetSummary(ctx.Request, id);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.IsType<LocationSummaryDto>(ok.Value);
    }

    [Fact]
    public void GetReconciliation_ValidTenant_ReturnsOk()
    {
        var (api, id) = BuildWithSession();
        var result = api.GetReconciliation(Request(tenant: 1), id);
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.IsType<ReconciliationDto>(ok.Value);
    }

    [Fact]
    public void GetUnknown_WrongTenant_ReturnsNotFound()
    {
        var (api, id) = BuildWithSession(tenantId: 1);
        var result = api.GetUnknown(Request(tenant: 2), id);
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public void GetReconciliation_WrongTenant_ReturnsNotFound()
    {
        var (api, id) = BuildWithSession(tenantId: 1);
        Assert.IsType<NotFoundResult>(api.GetReconciliation(Request(tenant: 2), id));
    }

    [Fact]
    public void GetUnknown_ValidTenant_ReturnsOk()
    {
        var (api, id) = BuildWithSession();
        var ok = Assert.IsType<OkObjectResult>(api.GetUnknown(Request(tenant: 1), id));
        Assert.IsType<UnknownDto>(ok.Value);
    }

    [Fact]
    public void GetSummary_EmptyGuid_ReturnsBadRequest()
    {
        var (api, _) = BuildWithSession();
        Assert.IsType<BadRequestObjectResult>(api.GetSummary(Request(tenant: 1), Guid.Empty));
    }
}
