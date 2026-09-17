using System.Security.Cryptography;
using System.Text;

namespace Chatter.Server.Auth;

// Generates short-lived TURN credentials using coturn's standard "REST API" / use-auth-secret
// mechanism (https://github.com/coturn/coturn/blob/master/README.turnserver#L563) - a shared
// secret configured on both this server (Turn:SharedSecret) and the TURN server itself, never
// sent to any client. The client only ever receives a username/password pair that expires on its
// own shortly after, not the secret that could mint arbitrary ones.
public static class TurnCredentialSigner
{
    // How long a minted credential stays valid. Generous relative to a typical call's length (a
    // credential only needs to outlive ICE gathering/connection setup, not the whole call - once
    // the media path is established, TURN allocations aren't re-authenticated), but bounded so a
    // leaked/logged credential doesn't work forever.
    public static readonly TimeSpan CredentialLifetime = TimeSpan.FromHours(6);

    public static (string Username, string Credential) CreateEphemeralCredential(string userId, string sharedSecret)
    {
        var expiry = DateTimeOffset.UtcNow.Add(CredentialLifetime).ToUnixTimeSeconds();
        // coturn parses everything before the last ':' as opaque - embedding the user id is purely
        // for the TURN server's own logs (e.g. "who's using how much relay bandwidth"), it isn't
        // otherwise interpreted.
        var username = $"{expiry}:{userId}";

        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(sharedSecret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(username));
        var credential = Convert.ToBase64String(hash);

        return (username, credential);
    }
}
