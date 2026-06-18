using Azure;
using Azure.Security.KeyVault.Secrets;
using EpcForwarder.Core.Abstractions;

namespace EpcForwarder.Infrastructure.Secrets;

/// <summary>Azure Key Vault からシークレットを取得。実検証は実機(本クラスは build-only)。</summary>
public sealed class KeyVaultSecretStore(SecretClient client) : ISecretStore
{
    public async Task<string?> GetAsync(string name, CancellationToken ct = default)
    {
        try
        {
            var response = await client.GetSecretAsync(name, cancellationToken: ct);
            return response.Value.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }
}
