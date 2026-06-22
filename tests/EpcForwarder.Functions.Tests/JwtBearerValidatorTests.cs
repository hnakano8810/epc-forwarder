// tests/EpcForwarder.Functions.Tests/JwtBearerValidatorTests.cs
using System.Security.Claims;
using System.Security.Cryptography;
using EpcForwarder.Functions.Auth;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace EpcForwarder.Functions.Tests;

public sealed class JwtBearerValidatorTests
{
    private const string Issuer = "https://test.ciamlogin.com/";
    private const string Audience = "api://epcf-test";
    private const string TenantClaim = "extension_tenantId";

    private readonly RsaSecurityKey _key = new(RSA.Create(2048)) { KeyId = "test-key" };

    private sealed class FixedProvider(SecurityKey key) : ITokenValidationParametersProvider
    {
        public Task<TokenValidationParameters> GetAsync(CancellationToken ct) =>
            Task.FromResult(new TokenValidationParameters
            {
                ValidIssuer = Issuer,
                ValidAudience = Audience,
                IssuerSigningKey = key,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero,
            });
    }

    private JwtBearerValidator NewSut() =>
        new(new FixedProvider(_key), new AuthOptions { Issuer = Issuer, Audience = Audience, TenantClaim = TenantClaim });

    private string MakeToken(
        string? tenant = "acme",
        string? issuer = Issuer,
        string? audience = Audience,
        DateTime? expires = null,
        SecurityKey? signingKey = null)
    {
        var claims = new List<Claim>();
        if (tenant is not null) claims.Add(new Claim(TenantClaim, tenant));
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Expires = expires ?? DateTime.UtcNow.AddMinutes(10),
            Subject = new ClaimsIdentity(claims),
            SigningCredentials = new SigningCredentials(signingKey ?? _key, SecurityAlgorithms.RsaSha256),
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    [Fact]
    public async Task Valid_token_returns_authenticated_with_tenant()
    {
        var r = await NewSut().ValidateAsync(MakeToken(tenant: "acme"), default);
        Assert.Equal(AuthOutcome.Authenticated, r.Outcome);
        Assert.Equal("acme", r.TenantCode);
    }

    [Fact]
    public async Task Missing_tenant_claim_returns_no_tenant()
    {
        var r = await NewSut().ValidateAsync(MakeToken(tenant: null), default);
        Assert.Equal(AuthOutcome.NoTenant, r.Outcome);
    }

    [Fact]
    public async Task Wrong_issuer_is_unauthenticated()
    {
        var r = await NewSut().ValidateAsync(MakeToken(issuer: "https://evil/"), default);
        Assert.Equal(AuthOutcome.Unauthenticated, r.Outcome);
    }

    [Fact]
    public async Task Wrong_audience_is_unauthenticated()
    {
        var r = await NewSut().ValidateAsync(MakeToken(audience: "api://other"), default);
        Assert.Equal(AuthOutcome.Unauthenticated, r.Outcome);
    }

    [Fact]
    public async Task Expired_token_is_unauthenticated()
    {
        var r = await NewSut().ValidateAsync(MakeToken(expires: DateTime.UtcNow.AddMinutes(-5)), default);
        Assert.Equal(AuthOutcome.Unauthenticated, r.Outcome);
    }

    [Fact]
    public async Task Wrong_signing_key_is_unauthenticated()
    {
        var other = new RsaSecurityKey(RSA.Create(2048)) { KeyId = "other" };
        var r = await NewSut().ValidateAsync(MakeToken(signingKey: other), default);
        Assert.Equal(AuthOutcome.Unauthenticated, r.Outcome);
    }

    [Fact]
    public async Task Garbage_token_is_unauthenticated()
    {
        var r = await NewSut().ValidateAsync("not-a-jwt", default);
        Assert.Equal(AuthOutcome.Unauthenticated, r.Outcome);
    }
}
