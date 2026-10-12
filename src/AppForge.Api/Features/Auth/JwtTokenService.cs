using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using AppForge.Api.Domain.Entities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AppForge.Api.Features.Auth;

internal interface IJwtTokenService
{
    string CreateAccessToken(User user, IReadOnlyList<string> roleNames, Guid? tenantId = null);

    // Story 12.4 — platform-super-admin token path. Distinct from CreateAccessToken
    // because PlatformAdmin is not a User: no RBAC role lookup (the single "roles"
    // claim is always the hardcoded literal below) and never a tenantId claim.
    string CreateAccessTokenForPlatformAdmin(Guid platformAdminId, string email);

    // Public-guest session token: userId + email + tenantId, the single hidden `public-guest`
    // role and a `guest=true` claim, with a caller-supplied (short) lifetime. Never paired
    // with a refresh token.
    string CreateGuestAccessToken(Guid userId, string email, Guid tenantId, TimeSpan lifetime);
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI.")]
internal sealed class JwtTokenService(IOptions<JwtOptions> jwtOptions) : IJwtTokenService
{
    public string CreateAccessToken(User user, IReadOnlyList<string> roleNames, Guid? tenantId = null)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(roleNames);

        var claims = new List<Claim>
        {
            new("userId", user.Id.ToString()),
            new("email", user.Email),
        };

        foreach (var role in roleNames)
        {
            claims.Add(new Claim("roles", role));
        }

        // Story 12.3 — only present when the authenticating user resolved through
        // tenant_user_index (AuthService.LoginAsync). Legacy public.users logins keep
        // issuing claim-less tokens, matching the additive (not hard-cutover) decision.
        if (tenantId is not null)
        {
            claims.Add(new Claim("tenantId", tenantId.Value.ToString()));
        }

        return BuildToken(claims);
    }

    // Story 12.4 — platform-super-admin token path: userId + email claims, a single
    // hardcoded "platform-super-admin" roles claim (no RBAC lookup — PlatformAdmin has
    // no roles table), and never a tenantId claim.
    public string CreateAccessTokenForPlatformAdmin(Guid platformAdminId, string email)
    {
        ArgumentException.ThrowIfNullOrEmpty(email);

        var claims = new List<Claim>
        {
            new("userId", platformAdminId.ToString()),
            new("email", email),
            new("roles", "platform-super-admin"),
        };

        return BuildToken(claims);
    }

    public string CreateGuestAccessToken(Guid userId, string email, Guid tenantId, TimeSpan lifetime)
    {
        ArgumentException.ThrowIfNullOrEmpty(email);

        var claims = new List<Claim>
        {
            new("userId", userId.ToString()),
            new("email", email),
            new("roles", "public-guest"),
            new("tenantId", tenantId.ToString()),
            new("guest", "true"),
        };

        return BuildToken(claims, lifetime);
    }

    private string BuildToken(List<Claim> claims, TimeSpan? lifetime = null)
    {
        var options = jwtOptions.Value;

        if (string.IsNullOrWhiteSpace(options.SigningKey))
            throw new InvalidOperationException("Jwt:SigningKey is required.");

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var now = DateTime.UtcNow;

        // `iat` is NOT auto-emitted by JwtSecurityToken (it emits `nbf` from notBefore
        // and `exp` from expires only). Add it explicitly with Integer64 so the JWT
        // payload serializer writes it as a JSON number per RFC 7519 §2 (NumericDate).
        var issuedAtUnix = new DateTimeOffset(now).ToUnixTimeSeconds()
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        claims.Add(new Claim("iat", issuedAtUnix, ClaimValueTypes.Integer64));

        var token = new JwtSecurityToken(
            issuer: options.Issuer,
            audience: options.Audience,
            claims: claims,
            notBefore: now,
            expires: lifetime is { } l ? now.Add(l) : now.AddMinutes(options.AccessTokenTtlMinutes),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
