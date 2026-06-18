// src/EpcForwarder.Infrastructure/Delivery/HttpWebhookSender.cs
using System.Net.Http.Headers;
using System.Text;
using EpcForwarder.Core.Abstractions;

namespace EpcForwarder.Infrastructure.Delivery;

/// <summary>IWebhookSender の実HTTP実装。URLガードは上位(アプリ層)で実施済みの前提。</summary>
public sealed class HttpWebhookSender(HttpClient client) : IWebhookSender
{
    public async Task<WebhookResult> SendAsync(WebhookRequest request, CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Url);

        var contentType = request.Headers.TryGetValue("Content-Type", out var ctv)
            ? ctv
            : "application/json; charset=utf-8";
        message.Content = new StringContent(request.Body, Encoding.UTF8);
        message.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);

        foreach (var (name, value) in request.Headers)
        {
            if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                continue; // Content側で設定済み
            }

            message.Headers.TryAddWithoutValidation(name, value);
        }

        using var response = await client.SendAsync(message, ct);
        return new WebhookResult(response.IsSuccessStatusCode, (int)response.StatusCode);
    }
}
