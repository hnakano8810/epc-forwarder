// src/EpcForwarder.Functions/Auth/JwtBearerValidator.cs
using Microsoft.IdentityModel.JsonWebTokens;

namespace EpcForwarder.Functions.Auth;

/// <summary>External ID の JWT を検証し tenant クレームを取り出す。署名鍵はプロバイダ経由。</summary>
public sealed class JwtBearerValidator(ITokenValidationParametersProvider parametersProvider, AuthOptions options)
{
    private readonly JsonWebTokenHandler _handler = new();

    public async Task<TokenValidationResult> ValidateAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return TokenValidationResult.Unauthenticated;
        }

        var parameters = await parametersProvider.GetAsync(ct);
        var result = await _handler.ValidateTokenAsync(token, parameters);
        if (!result.IsValid)
        {
            return TokenValidationResult.Unauthenticated;
        }

        var tenant = result.ClaimsIdentity.FindFirst(options.TenantClaim)?.Value;
        return string.IsNullOrEmpty(tenant)
            ? TokenValidationResult.NoTenant
            : TokenValidationResult.Authenticated(tenant);
    }
}
