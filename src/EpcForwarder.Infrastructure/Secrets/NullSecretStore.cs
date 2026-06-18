using EpcForwarder.Core.Abstractions;

namespace EpcForwarder.Infrastructure.Secrets;

/// <summary>シークレットを持たない環境用(常に null)。Key Vault 未設定時の内側に使う。</summary>
public sealed class NullSecretStore : ISecretStore
{
    public Task<string?> GetAsync(string name, CancellationToken ct = default) => Task.FromResult<string?>(null);
}
