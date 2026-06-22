using EpcForwarder.Core.Delivery;
using EpcForwarder.Core.Inventory;
using EpcForwarder.Core.Sessions;
using EpcForwarder.Core.Tests.Fakes;
using EpcForwarder.Functions.Api;
using EpcForwarder.Functions.Auth;
using EpcForwarder.Functions.Tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Xunit;

namespace EpcForwarder.Functions.Tests.Api;

public class InventoryApiTests
{
    private sealed class Ctx
    {
        public InMemorySessionStore Sessions { get; } = new();
        public InMemoryReadingStore Readings { get; } = new();
        public InMemoryProductCatalog Products { get; } = new();
        public InMemorySnapshotStore Snapshots { get; } = new();
        public FakeSecretStore Secrets { get; } = new();
        public CapturingWebhookSender Sender { get; } = new();
        public FakeDestinationCatalog Destinations { get; } = new();
        public FixedClock Clock { get; } = new(DateTimeOffset.UnixEpoch);
        public SequentialIdGenerator Ids { get; } = new();

        public InventoryApi Build()
        {
            var publisher = new SnapshotPublisher(Readings, Products, Snapshots, Sender, Secrets, new PayloadBuilder(), Clock, Ids);
            var deliverer = new InventoryDeliverer(Sessions, publisher, Clock);
            return new InventoryApi(new InventoryDispatcher(Sessions, deliverer, Destinations));
        }
    }

    /// <summary>認証済みテナントを持つ FunctionContext を生成。null は未認証(401 テスト用)。</summary>
    private static FunctionContext Context(int? tenant)
    {
        var ctx = new FakeFunctionContext();
        if (tenant is not null)
        {
            AuthenticatedTenant.Set(ctx, tenant.Value);
        }

        return ctx;
    }

    private static HttpRequest PlainRequest() => new DefaultHttpContext().Request;

    private static Guid OpenInventory(Ctx ctx, int tenant = 1)
    {
        var id = Guid.NewGuid();
        ctx.Sessions.Save(new Session(id, tenant, SessionType.Inventory, "INV-1", ctx.Clock.UtcNow));
        return id;
    }

    [Fact]
    public async Task Provisional_Valid_ReturnsOkDelivered()
    {
        var ctx = new Ctx();
        ctx.Destinations.Add(1, new DeliveryTarget("https://x.test/h", "POST", "1", false, null, new Dictionary<string, string>()));
        var api = ctx.Build();
        var id = OpenInventory(ctx);

        var result = await api.SendProvisional(PlainRequest(), id, Context(1));

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<InventoryResultDto>(ok.Value);
        Assert.True(dto.Delivered);
        Assert.Equal(200, dto.StatusCode);
    }

    [Fact]
    public async Task Finalize_Valid_ReturnsOkDelivered()
    {
        var ctx = new Ctx();
        ctx.Destinations.Add(1, new DeliveryTarget("https://x.test/h", "POST", "1", false, null, new Dictionary<string, string>()));
        var api = ctx.Build();
        var id = OpenInventory(ctx);

        var result = await api.Finalize(PlainRequest(), id, Context(1));

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<InventoryResultDto>(ok.Value);
        Assert.True(dto.Delivered);
        Assert.Equal(SessionStatus.Forwarded, ctx.Sessions.Get(id)!.Status);
    }

    [Fact]
    public async Task Provisional_NoActiveTarget_ReturnsOkNotDelivered()
    {
        var ctx = new Ctx(); // 宛先未登録
        var api = ctx.Build();
        var id = OpenInventory(ctx);

        var result = await api.SendProvisional(PlainRequest(), id, Context(1));

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<InventoryResultDto>(ok.Value);
        Assert.False(dto.Delivered);
        Assert.Null(dto.StatusCode);
    }

    [Fact]
    public async Task Provisional_NoTenant_ReturnsUnauthorized()
    {
        var ctx = new Ctx();
        var api = ctx.Build();
        var id = OpenInventory(ctx);
        Assert.IsType<UnauthorizedResult>(await api.SendProvisional(PlainRequest(), id, Context(null)));
    }

    [Fact]
    public async Task Provisional_WrongTenant_ReturnsNotFound()
    {
        var ctx = new Ctx();
        var api = ctx.Build();
        var id = OpenInventory(ctx, tenant: 1);
        Assert.IsType<NotFoundResult>(await api.SendProvisional(PlainRequest(), id, Context(2)));
    }

    [Fact]
    public async Task Finalize_AlreadyForwarded_ReturnsConflict()
    {
        var ctx = new Ctx();
        ctx.Destinations.Add(1, new DeliveryTarget("https://x.test/h", "POST", "1", false, null, new Dictionary<string, string>()));
        var api = ctx.Build();
        var id = OpenInventory(ctx);

        await api.Finalize(PlainRequest(), id, Context(1));                  // 1回目: forwarded
        var second = await api.Finalize(PlainRequest(), id, Context(1));     // 2回目: 状態不正

        Assert.IsType<ConflictObjectResult>(second);
    }

    [Fact]
    public async Task Provisional_EmptyGuid_ReturnsBadRequest()
    {
        var ctx = new Ctx();
        var api = ctx.Build();
        Assert.IsType<BadRequestObjectResult>(await api.SendProvisional(PlainRequest(), Guid.Empty, Context(1)));
    }
}
