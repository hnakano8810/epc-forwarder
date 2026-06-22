// src/EpcForwarder.Functions/Auth/AuthenticationMiddleware.cs
using EpcForwarder.Core.Abstractions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace EpcForwarder.Functions.Auth;

/// <summary>HTTP 関数の External ID トークンを検証し tenant_id を文脈へ。失敗は 401/403 で短絡。</summary>
public sealed class AuthenticationMiddleware(JwtBearerValidator validator, ITenantLookup tenants, ILogger<AuthenticationMiddleware> logger) : IFunctionsWorkerMiddleware
{
    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        var http = context.GetHttpContext();
        if (http is null)
        {
            // 非HTTP(取込 EventHub トリガー等)は対象外。
            await next(context);
            return;
        }

        string? token = null;
        var header = http.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            token = header["Bearer ".Length..].Trim();
        }

        TokenValidationResult result;
        try
        {
            // 検証は OIDC メタデータ(JWKS)を HTTP 取得するため例外がありうる。
            // 検証処理が落ちた場合は fail-closed: 認証できない=401(スタックトレースは出さない)。
            result = await validator.ValidateAsync(token ?? "", context.CancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Token validation failed due to an error.");
            await WriteStatus(http, StatusCodes.Status401Unauthorized, "invalid_token");
            return;
        }

        if (result.Outcome == AuthOutcome.Unauthenticated)
        {
            await WriteStatus(http, StatusCodes.Status401Unauthorized, "invalid_token");
            return;
        }

        if (result.Outcome == AuthOutcome.NoTenant || tenants.ResolveId(result.TenantCode!) is not int tenantId)
        {
            await WriteStatus(http, StatusCodes.Status403Forbidden, null);
            return;
        }

        AuthenticatedTenant.Set(context, tenantId);
        await next(context);
    }

    private static async Task WriteStatus(HttpContext http, int code, string? error)
    {
        http.Response.StatusCode = code;
        if (code == StatusCodes.Status401Unauthorized)
        {
            http.Response.Headers.WWWAuthenticate = error is null ? "Bearer" : $"Bearer error=\"{error}\"";
        }
        await http.Response.WriteAsync(code == 401 ? "Unauthorized" : "Forbidden");
    }
}
