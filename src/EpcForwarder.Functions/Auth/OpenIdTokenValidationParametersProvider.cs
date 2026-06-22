// src/EpcForwarder.Functions/Auth/OpenIdTokenValidationParametersProvider.cs
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace EpcForwarder.Functions.Auth;

/// <summary>OIDC メタデータから署名鍵(JWKS)を取得して検証パラメータを構築。鍵はキャッシュ・自動更新。</summary>
public sealed class OpenIdTokenValidationParametersProvider : ITokenValidationParametersProvider
{
    private readonly AuthOptions _options;
    private readonly ConfigurationManager<OpenIdConnectConfiguration> _configManager;

    public OpenIdTokenValidationParametersProvider(AuthOptions options)
    {
        _options = options;
        _configManager = new ConfigurationManager<OpenIdConnectConfiguration>(
            options.MetadataAddress,
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever());
    }

    public async Task<TokenValidationParameters> GetAsync(CancellationToken ct)
    {
        var config = await _configManager.GetConfigurationAsync(ct);
        return new TokenValidationParameters
        {
            ValidIssuer = _options.Issuer,
            ValidAudience = _options.Audience,
            IssuerSigningKeys = config.SigningKeys,
            ValidateLifetime = true,
        };
    }
}
