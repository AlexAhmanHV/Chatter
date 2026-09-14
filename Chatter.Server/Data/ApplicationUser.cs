using Microsoft.AspNetCore.Identity;

namespace Chatter.Server.Data;

// ASP.NET Core Identity's IdentityUser already covers Id/Email/PasswordHash/etc; DisplayName is
// the one extra field this app needs, carried into the JWT at login (see Auth/JwtIssuer) so
// ChatHub never has to ask anything else for a new connection's default name.
public class ApplicationUser : IdentityUser
{
    public string DisplayName { get; set; } = string.Empty;
}
