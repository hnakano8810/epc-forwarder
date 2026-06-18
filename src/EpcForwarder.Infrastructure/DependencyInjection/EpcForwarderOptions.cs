// src/EpcForwarder.Infrastructure/DependencyInjection/EpcForwarderOptions.cs
namespace EpcForwarder.Infrastructure.DependencyInjection;

public sealed class EpcForwarderOptions
{
    public required string SqlConnectionString { get; init; }
    /// <summary>未設定なら Key Vault を使わず NullSecretStore を内側にする。</summary>
    public string? KeyVaultUri { get; init; }
    public TimeSpan SecretCacheTtl { get; init; } = TimeSpan.FromMinutes(5);
}
