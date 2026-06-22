// src/EpcForwarder.Functions/Auth/ITokenValidationParametersProvider.cs
using Microsoft.IdentityModel.Tokens;

namespace EpcForwarder.Functions.Auth;

/// <summary>署名鍵を含む検証パラメータを供給する。本番は OIDC メタデータ由来、テストは固定鍵。</summary>
public interface ITokenValidationParametersProvider
{
    Task<TokenValidationParameters> GetAsync(CancellationToken ct);
}
