// src/EpcForwarder.Functions/Auth/TokenValidationResult.cs
namespace EpcForwarder.Functions.Auth;

public enum AuthOutcome { Authenticated, NoTenant, Unauthenticated }

/// <summary>トークン検証の結果。Authenticated のときのみ TenantCode を持つ。</summary>
public sealed record TokenValidationResult(AuthOutcome Outcome, string? TenantCode)
{
    public static readonly TokenValidationResult Unauthenticated = new(AuthOutcome.Unauthenticated, null);
    public static readonly TokenValidationResult NoTenant = new(AuthOutcome.NoTenant, null);
    public static TokenValidationResult Authenticated(string tenantCode) => new(AuthOutcome.Authenticated, tenantCode);
}
