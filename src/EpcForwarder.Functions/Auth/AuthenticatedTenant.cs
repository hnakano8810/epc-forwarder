// src/EpcForwarder.Functions/Auth/AuthenticatedTenant.cs
using Microsoft.Azure.Functions.Worker;

namespace EpcForwarder.Functions.Auth;

/// <summary>ミドルウェアが解決した認証済み tenant_id を関数へ受け渡す。</summary>
public static class AuthenticatedTenant
{
    private const string Key = "EpcfTenantId";

    public static void Set(FunctionContext context, int tenantId) => context.Items[Key] = tenantId;

    public static bool TryGet(FunctionContext context, out int tenantId)
    {
        if (context.Items.TryGetValue(Key, out var v) && v is int id)
        {
            tenantId = id;
            return true;
        }
        tenantId = 0;
        return false;
    }
}
