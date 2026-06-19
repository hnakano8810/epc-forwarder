// tests/EpcForwarder.Core.Tests/Infrastructure/HttpWebhookSenderTests.cs
using System.Net;
using System.Text;
using EpcForwarder.Core.Abstractions;
using EpcForwarder.Core.Tests.Fakes;
using EpcForwarder.Infrastructure.Delivery;
using Xunit;

namespace EpcForwarder.Core.Tests.Infrastructure;

public class HttpWebhookSenderTests
{
    [Fact]
    public async Task SendAsync_PostsBodyAndHeaders_ReturnsSuccess()
    {
        using var listener = new HttpListener();
        var prefix = "http://127.0.0.1:18791/hook/"; // 非特権ポート(>1024)
        listener.Prefixes.Add(prefix);
        listener.Start();

        string? receivedBody = null;
        string? receivedIdem = null;
        var serverTask = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            receivedBody = await reader.ReadToEndAsync();
            receivedIdem = ctx.Request.Headers["Idempotency-Key"];
            ctx.Response.StatusCode = 200;
            ctx.Response.Close();
        });

        using var client = new HttpClient();
        var sut = new HttpWebhookSender(new SingleClientHttpClientFactory(client));
        var headers = new Dictionary<string, string>
        {
            ["Content-Type"] = "application/json; charset=utf-8",
            ["Idempotency-Key"] = "abc-123",
        };

        var result = await sut.SendAsync(new WebhookRequest(prefix, "POST", headers, "{\"hello\":1}"));

        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        listener.Stop();

        Assert.True(result.Success);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal("{\"hello\":1}", receivedBody);
        Assert.Equal("abc-123", receivedIdem);
    }
}
