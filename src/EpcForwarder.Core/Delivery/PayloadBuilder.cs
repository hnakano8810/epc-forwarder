using System.Text.Json;

namespace EpcForwarder.Core.Delivery;

/// <summary>WebhookEnvelope を snake_case JSON へ直列化。詳細は docs/design/webhook-contract.md §3。</summary>
public sealed class PayloadBuilder
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    public string Serialize(WebhookEnvelope envelope) => JsonSerializer.Serialize(envelope, Options);
}
