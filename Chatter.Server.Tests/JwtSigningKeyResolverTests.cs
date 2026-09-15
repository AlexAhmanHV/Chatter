using Chatter.Server.Auth;
using Xunit;

namespace Chatter.Server.Tests;

// Covers the "no Jwt:SigningKey configured" fallback Program.cs uses so `dotnet run`/
// `docker compose up` work with zero manual setup - see JwtSigningKeyResolver's own comments for
// why the key is written next to the SQLite database rather than somewhere else.
public class JwtSigningKeyResolverTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "chatter-jwt-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void ResolveOrGenerate_NoExistingFile_GeneratesAndPersistsAKey()
    {
        var dir = NewTempDir();
        try
        {
            var connectionString = $"Data Source={Path.Combine(dir, "chatter.db")}";

            var key = JwtSigningKeyResolver.ResolveOrGenerate(connectionString);

            Assert.False(string.IsNullOrWhiteSpace(key));
            var keyFilePath = Path.Combine(dir, "jwt-signing-key.txt");
            Assert.True(File.Exists(keyFilePath));
            Assert.Equal(key, File.ReadAllText(keyFilePath).Trim());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ResolveOrGenerate_CalledTwice_ReusesTheSameKey()
    {
        var dir = NewTempDir();
        try
        {
            var connectionString = $"Data Source={Path.Combine(dir, "chatter.db")}";

            var first = JwtSigningKeyResolver.ResolveOrGenerate(connectionString);
            var second = JwtSigningKeyResolver.ResolveOrGenerate(connectionString);

            Assert.Equal(first, second);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ResolveOrGenerate_KeyFileAlreadyExists_ReturnsItsContentsInstead()
    {
        var dir = NewTempDir();
        try
        {
            var connectionString = $"Data Source={Path.Combine(dir, "chatter.db")}";
            File.WriteAllText(Path.Combine(dir, "jwt-signing-key.txt"), "a-pre-existing-key");

            var key = JwtSigningKeyResolver.ResolveOrGenerate(connectionString);

            Assert.Equal("a-pre-existing-key", key);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ResolveOrGenerate_DataDirectoryDoesNotExistYet_CreatesIt()
    {
        var parent = NewTempDir();
        try
        {
            var nestedDataDir = Path.Combine(parent, "data");
            var connectionString = $"Data Source={Path.Combine(nestedDataDir, "chatter.db")}";

            var key = JwtSigningKeyResolver.ResolveOrGenerate(connectionString);

            Assert.False(string.IsNullOrWhiteSpace(key));
            Assert.True(File.Exists(Path.Combine(nestedDataDir, "jwt-signing-key.txt")));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }
}
