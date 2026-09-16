using Microsoft.AspNetCore.Identity;

namespace Chatter.Server.Data;

// ASP.NET Core Identity's IdentityUser already covers Id/Email/PasswordHash/etc; DisplayName is
// the one extra field this app needs, carried into the JWT at login (see Auth/JwtIssuer) so
// ChatHub never has to ask anything else for a new connection's default name.
public class ApplicationUser : IdentityUser
{
    public string DisplayName { get; set; } = string.Empty;

    // Profile picture, stored as a blob (see ChatHub.UpdateAvatar for size/content-type limits).
    // Served at GET /avatars/{userId} behind a signed, expiring link rather than a bare
    // Authorization header - see Program.cs and Auth/AvatarUrlSigner.
    public byte[]? AvatarData { get; set; }
    public string? AvatarContentType { get; set; }

    // Updated when the user's last connection drops (see ChatHub.OnDisconnectedAsync); null means
    // "never seen offline this app has tracked" (e.g. never connected, or still online everywhere).
    public DateTime? LastSeenUtc { get; set; }
}
