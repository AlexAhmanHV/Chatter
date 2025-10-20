// Services/SupabaseAuthService.cs
using Supabase;
using Supabase.Gotrue;
using System.Text.Json;

namespace Chatter.Client.Services;

public class SupabaseAuthService
{
    // Qualify the type to avoid "Client" ambiguity
    private readonly Supabase.Client _client;

    public SupabaseAuthService(string url, string anonKey)
    {
        var options = new SupabaseOptions
        {
            AutoConnectRealtime = false
        };

        _client = new Supabase.Client(url, anonKey, options);
    }

    public async Task InitializeAsync() => await _client.InitializeAsync();

    public async Task<Session?> SignInAsync(string email, string password)
    {
        await InitializeAsync();
        // In your SDK version this returns Session? directly
        var session = await _client.Auth.SignInWithPassword(email, password);
        return session;
    }

    public async Task<Session?> SignUpAsync(string email, string password, Dictionary<string, object>? metadata = null)
    {
        await InitializeAsync();

        var options = new SignUpOptions
        {
            Data = metadata ?? new Dictionary<string, object>()
        };

        // ✅ In your SDK version, SignUp returns Session? directly.
        var session = await _client.Auth.SignUp(email, password, options);
        return session;

        // If you later update the package and get a compile error here,
        // switch to: await _client.Auth.SignUpWithPassword(email, password, options);
        // …which also returns Session?.
    }

    public string? CurrentDisplayName
    {
        get
        {
            var user = _client.Auth.CurrentUser;
            if (user == null)
                return null;

            // Treat metadata as object first so we can type-check at runtime
            var meta = user.UserMetadata as object;

            // Case 1: metadata is a dictionary
            if (meta is IDictionary<string, object> dict &&
                dict.TryGetValue("display_name", out var val) &&
                val is not null)
                return val.ToString();

            // Case 2: metadata is JSON (some SDK versions)
            if (meta is JsonElement elem &&
                elem.ValueKind == JsonValueKind.Object &&
                elem.TryGetProperty("display_name", out var dn))
                return dn.GetString();

            return null;
        }
    }


    public string? AccessToken => _client.Auth.CurrentSession?.AccessToken;

    public async Task<string?> UpdateDisplayNameAsync(string newName)
    {
        await InitializeAsync();

        var attrs = new UserAttributes
        {
            Data = new Dictionary<string, object>
            {
                ["display_name"] = newName
            }
        };

        // Returns the updated user
        var updatedUser = await _client.Auth.Update(attrs);

        // Some SDK versions need a manual refresh; this makes sure CurrentUser is fresh:
        // await _client.Auth.GetUser();

        return CurrentDisplayName;
    }

}
