// src/EpcForwarder.Functions/Auth/AuthOptions.cs
namespace EpcForwarder.Functions.Auth;

/// <summary>External ID トークン検証の設定(アプリ設定 Auth__* から束縛)。</summary>
public sealed class AuthOptions
{
    public string Issuer { get; init; } = "";
    public string Audience { get; init; } = "";
    /// <summary>テナントを運ぶクレーム名。External ID では extension_&lt;appid&gt;_tenantId 形式。</summary>
    public string TenantClaim { get; init; } = "";
    /// <summary>OIDC メタデータURL(.../v2.0/.well-known/openid-configuration)。</summary>
    public string MetadataAddress { get; init; } = "";
}
