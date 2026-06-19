using Microsoft.AspNetCore.Http;

namespace EpcForwarder.Functions.Api;

/// <summary>X-EPCF-Tenant ヘッダからテナントIDを取り出す。欠落/不正は false。</summary>
public static class RequestTenant
{
    public const string HeaderName = "X-EPCF-Tenant";

    public static bool TryGet(HttpRequest request, out int tenantId)
    {
        tenantId = 0;
        return request.Headers.TryGetValue(HeaderName, out var values)
            && int.TryParse(values.ToString(), out tenantId);
    }
}
