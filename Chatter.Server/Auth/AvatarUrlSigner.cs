using System.Security.Cryptography;
using System.Text;

namespace Chatter.Server.Auth;

// Signs/validates short-lived tokens for GET /avatars/{userId} (see Program.cs).
//
// Avatars used to be served with no check at all beyond knowing a userId - fine in theory since
// ids are random GUIDs, but it meant the URL itself was the only thing standing between "anyone on
// the internet" and a user's picture, forever, with no way to revoke it. Requiring a real
// Authorization header instead isn't practical here since MAUI's plain <Image Source="url"/>
// can't attach one - so instead, the *link itself* only exists because an authenticated hub caller
// (ChatHub.GetAvatarUrls, which requires [Authorize]) minted it, and it stops working after a
// short expiry. No new secret to manage: reuses the same Jwt:SigningKey already required to run
// this server at all.
public static class AvatarUrlSigner
{
    public static string Sign(string userId, long expUnixSeconds, string signingKey)
    {
        var payload = $"{userId}:{expUnixSeconds}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(signingKey));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToBase64String(hash).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    public static bool Validate(string userId, long expUnixSeconds, string? signature, string signingKey)
    {
        if (string.IsNullOrEmpty(signature)) return false;
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expUnixSeconds) return false;

        var expected = Sign(userId, expUnixSeconds, signingKey);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(signature);
        if (expectedBytes.Length != actualBytes.Length) return false;

        return CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }
}
