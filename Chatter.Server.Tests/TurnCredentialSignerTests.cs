using System.Security.Cryptography;
using System.Text;
using Chatter.Server.Auth;

namespace Chatter.Server.Tests;

public class TurnCredentialSignerTests
{
    [Fact]
    public void CreateEphemeralCredential_UsernameEncodesExpiryAndUserId()
    {
        var (username, _) = TurnCredentialSigner.CreateEphemeralCredential("user-123", "secret");

        var parts = username.Split(':', 2);
        Assert.Equal(2, parts.Length);
        Assert.True(long.TryParse(parts[0], out var expiry));
        Assert.True(expiry > DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        Assert.Equal("user-123", parts[1]);
    }

    [Fact]
    public void CreateEphemeralCredential_CredentialIsHmacSha1OfUsername()
    {
        var (username, credential) = TurnCredentialSigner.CreateEphemeralCredential("user-123", "secret");

        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes("secret"));
        var expected = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(username)));

        Assert.Equal(expected, credential);
    }

    [Fact]
    public void CreateEphemeralCredential_DifferentSecrets_ProduceDifferentCredentials()
    {
        var (username, credentialA) = TurnCredentialSigner.CreateEphemeralCredential("user-123", "secret-a");

        using var hmacB = new HMACSHA1(Encoding.UTF8.GetBytes("secret-b"));
        var credentialB = Convert.ToBase64String(hmacB.ComputeHash(Encoding.UTF8.GetBytes(username)));

        Assert.NotEqual(credentialA, credentialB);
    }
}
