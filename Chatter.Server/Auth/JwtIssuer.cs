using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Chatter.Server.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace Chatter.Server.Auth;

// Issues the server's own JWTs (no external identity provider). Claims are named to match what
// ChatHub already expects from the earlier Supabase-based setup: "sub" for the stable user id,
// plus "email" and "display_name" so ChatHub.DeriveDefaultDisplayName never needs a second call.
public static class JwtIssuer
{
    // No refresh-token flow - a token is just valid for this long. Simple, but means a signed-out
    // device stays "signed in" for up to a week if the token isn't explicitly discarded client-side.
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromDays(7);

    public static (string Token, DateTime ExpiresAtUtc) CreateToken(ApplicationUser user, IConfiguration config)
    {
        var signingKey = config["Jwt:SigningKey"]
            ?? throw new InvalidOperationException(
                "Missing configuration value 'Jwt:SigningKey'. Set it in appsettings.json or the Jwt__SigningKey environment variable.");
        var issuer = config["Jwt:Issuer"] ?? "ChatterServer";
        var audience = config["Jwt:Audience"] ?? "ChatterClient";

        var expiresAtUtc = DateTime.UtcNow.Add(TokenLifetime);

        var claims = new[]
        {
            new Claim("sub", user.Id),
            new Claim("email", user.Email ?? string.Empty),
            new Claim("display_name", user.DisplayName),
            new Claim("is_admin", user.IsAdmin ? "true" : "false"),
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            expires: expiresAtUtc,
            signingCredentials: credentials);

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAtUtc);
    }
}
