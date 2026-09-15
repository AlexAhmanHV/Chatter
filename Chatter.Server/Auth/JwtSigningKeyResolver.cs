using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Chatter.Server.Auth;

// Used by Program.cs when Jwt:SigningKey isn't explicitly configured, so `dotnet run`/
// `docker compose up` work with zero manual setup for local/first-time use. Explicitly setting
// Jwt__SigningKey (as docker-compose.yml's template still does) always wins over this and skips
// it entirely - a real deployment should keep doing that rather than rely on a file quietly
// written to disk.
public static class JwtSigningKeyResolver
{
    private const string KeyFileName = "jwt-signing-key.txt";

    // Reuses an already-generated key from disk if present; otherwise generates a new one and
    // writes it next to the SQLite database so it's found (and reused) on the next run. The file
    // lives alongside the database specifically so the Docker image's persistent volume covers it
    // too - without that, every container recreation would silently invalidate every session.
    public static string ResolveOrGenerate(string sqliteConnectionString)
    {
        var keyFilePath = Path.Combine(GetDataDirectory(sqliteConnectionString), KeyFileName);

        if (File.Exists(keyFilePath))
        {
            var existing = File.ReadAllText(keyFilePath).Trim();
            if (existing.Length > 0) return existing;
        }

        var generated = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        Directory.CreateDirectory(Path.GetDirectoryName(keyFilePath)!);
        File.WriteAllText(keyFilePath, generated);
        Console.WriteLine(
            $"[Chatter] No Jwt:SigningKey configured - generated one and saved it to {keyFilePath}. " +
            "Fine for local development; a real deployment should set the Jwt__SigningKey environment variable explicitly instead.");
        return generated;
    }

    private static string GetDataDirectory(string sqliteConnectionString)
    {
        var dataSource = new SqliteConnectionStringBuilder(sqliteConnectionString).DataSource;
        return dataSource.Length > 0
            ? Path.GetDirectoryName(Path.GetFullPath(dataSource))!
            : Directory.GetCurrentDirectory();
    }
}
