/*
File: SupabaseAuthService.cs

What this does:
- Purpose: Thin wrapper around the Supabase .NET SDK that initializes the client, signs users up/in,
  reads/stores display names in user metadata, and exposes the current access token.
- How: Creates a single Supabase.Client instance, normalizes SDK return types, and guards calls with InitializeAsync().
- Where used: Injected into ChatService to supply an access token; can also power a login/settings UI.
*/

using Supabase;
using Supabase.Gotrue;
using System.Text.Json;

namespace Chatter.Client.Services;

public class SupabaseAuthService
{
    /* Backing client
       Holds the single Supabase.Client instance. We fully qualify the type name to avoid conflicts with
       any other "Client" types in the solution and configure options (e.g., disabling realtime auto-connect).
    */
    private readonly Supabase.Client _client;

    /* Construction & configuration
       Builds the Supabase client with the provided URL and anon key. We keep options minimal and
       leave the realtime connection off—this service only concerns auth.
    */
    public SupabaseAuthService(string url, string anonKey)
    {
        var options = new SupabaseOptions
        {
            AutoConnectRealtime = false
        };

        _client = new Supabase.Client(url, anonKey, options);
    }

    /* Initialization
       Ensures the underlying SDK is initialized before any auth calls. Callers can safely invoke
       InitializeAsync() repeatedly—SDK handles idempotency.
    */
    public async Task InitializeAsync() => await _client.InitializeAsync();

    /* Sign in
       Authenticates a user with email/password and returns the active Session (or null on failure).
       We always initialize first to avoid "client not initialized" issues across app lifetimes.
    */
    public async Task<Session?> SignInAsync(string email, string password)
    {
        await InitializeAsync();
        var session = await _client.Auth.SignInWithPassword(email, password);
        return session;
    }

    /* Sign up
       Registers a new user and optionally writes initial metadata (e.g., display_name).
       The SDK version in use returns Session? directly; if you upgrade and signatures change, see the note below.
    */
    public async Task<Session?> SignUpAsync(string email, string password, Dictionary<string, object>? metadata = null)
    {
        await InitializeAsync();

        var options = new SignUpOptions
        {
            Data = metadata ?? new Dictionary<string, object>()
        };

        var session = await _client.Auth.SignUp(email, password, options);
        return session;
    }

    /* Current display name
       Reads the user's display name from metadata, supporting both dictionary and JSON-shaped metadata
       depending on SDK/runtime behavior. Returns null if no user or no display_name is set.
    */
    public string? CurrentDisplayName
    {
        get
        {
            var user = _client.Auth.CurrentUser;
            if (user == null)
                return null;

            var meta = user.UserMetadata as object;

            // Case 1: metadata is a dictionary
            if (meta is IDictionary<string, object> dict &&
                dict.TryGetValue("display_name", out var val) &&
                val is not null)
                return val.ToString();

            // Case 2: metadata is JSON
            if (meta is JsonElement elem &&
                elem.ValueKind == JsonValueKind.Object &&
                elem.TryGetProperty("display_name", out var dn))
                return dn.GetString();

            return null;
        }
    }

    /* Access token
       Convenience accessor for the current session token. Used by ChatService to authenticate
       the SignalR connection via AccessTokenProvider.
    */
    public string? AccessToken => _client.Auth.CurrentSession?.AccessToken;

    /* Update display name
       Writes a new display_name value to the user's metadata. Returns the effective display name
       after update (reads it back via CurrentDisplayName).
    */
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

        // Returns the updated user; CurrentDisplayName reflects the new value.
        var updatedUser = await _client.Auth.Update(attrs);

        return CurrentDisplayName;
    }
}
