using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace ProtocolSpike;

// Synthetic keys/tokens only. Nothing here is a production validator or public API.
internal sealed class TokenFixture : IDisposable
{
    public const string Issuer = "https://issuer.example.test";
    public const string ClientId = "spike-cli";
    public const string AccessToken = "synthetic-access-token";
    public const string Nonce = "synthetic-independent-browser-nonce";
    private readonly RSA _rsa = RSA.Create(2048);

    public RsaSecurityKey SigningKey => new(_rsa) { KeyId = "spike-key" };

    public RsaSecurityKey PublicKey => new(_rsa.ExportParameters(false)) { KeyId = "spike-key" };

    public string CreateToken(string scenario = "valid", string? nonce = Nonce)
    {
        var now = DateTime.UtcNow;
        var claims = new List<Claim> { new("sub", "synthetic-subject") };
        if (nonce is not null)
        {
            claims.Add(new Claim("nonce", nonce));
        }

        if (scenario == "wrong_azp")
        {
            claims.Add(new Claim("azp", "different-client"));
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = scenario == "wrong_issuer" ? "https://wrong.example.test" : Issuer,
            Audience = scenario == "wrong_audience" ? "different-client" : ClientId,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = now.AddMinutes(-2),
            NotBefore = now.AddMinutes(-2),
            Expires = scenario == "expired" ? now.AddMinutes(-1) : now.AddMinutes(5),
            SigningCredentials = scenario == "unsigned" ? null : new SigningCredentials(
                SigningKey, scenario == "wrong_algorithm" ? SecurityAlgorithms.RsaSha512 : SecurityAlgorithms.RsaSha256)
        };
        return new JwtSecurityTokenHandler().CreateEncodedJwt(descriptor);
    }

    public ClaimsPrincipal Validate(string token, string? expectedNonce = Nonce, SecurityKey? key = null)
    {
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var user = handler.ValidateToken(token, new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = Issuer,
            ValidateAudience = true,
            ValidAudience = ClientId,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = key ?? PublicKey,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ClockSkew = TimeSpan.Zero
        }, out var validatedToken);

        var protocol = new OpenIdConnectProtocolValidator
        {
            RequireNonce = expectedNonce is not null,
            RequireTimeStampInNonce = false
        };
        protocol.ValidateTokenResponse(new OpenIdConnectProtocolValidationContext
        {
            ClientId = ClientId,
            Nonce = expectedNonce,
            ValidatedIdToken = (JwtSecurityToken)validatedToken,
            ProtocolMessage = new OpenIdConnectMessage
            {
                IdToken = token,
                AccessToken = AccessToken
            }
        });
        return user;
    }

    public string PublicJwks()
    {
        var parameters = _rsa.ExportParameters(false);
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            keys = new[] { new { kty = "RSA", kid = "spike-key", use = "sig", alg = "RS256",
                n = Base64UrlEncoder.Encode(parameters.Modulus!), e = Base64UrlEncoder.Encode(parameters.Exponent!) } }
        });
    }

    public void Dispose() => _rsa.Dispose();
}
