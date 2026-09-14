/*
File: ApiAuthService.cs

What this does:
- Purpose: Talks to this app's own /auth/register and /auth/login endpoints (no external identity
  provider), and exposes the resulting access token + display name for the lifetime of the app run.
- How: Plain HttpClient POSTs against ServerConfig.BaseUrl.
- Where used: Injected into ChatService (as the SignalR access-token source) and the Login/Register/
  Settings view models.
*/

using System.Net;
using System.Net.Http.Json;
using Chatter.Shared.Models;

namespace Chatter.Client.Services;

public class ApiAuthService
{
    private readonly HttpClient _http;

    public string? AccessToken { get; private set; }
    public string? CurrentDisplayName { get; private set; }

    public ApiAuthService()
    {
        _http = new HttpClient { BaseAddress = new Uri(ServerConfig.BaseUrl) };
    }

    public async Task<string> SignInAsync(string email, string password)
    {
        var resp = await _http.PostAsJsonAsync("/auth/login", new LoginRequest(email, password));
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(await ExtractErrorAsync(resp));

        var body = await resp.Content.ReadFromJsonAsync<AuthResponse>()
            ?? throw new InvalidOperationException("Unexpected empty response from server.");

        AccessToken = body.AccessToken;
        CurrentDisplayName = body.DisplayName;
        return CurrentDisplayName;
    }

    public async Task<string> SignUpAsync(string email, string password, string? displayName)
    {
        var resp = await _http.PostAsJsonAsync("/auth/register", new RegisterRequest(email, password, displayName));
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(await ExtractErrorAsync(resp));

        var body = await resp.Content.ReadFromJsonAsync<AuthResponse>()
            ?? throw new InvalidOperationException("Unexpected empty response from server.");

        AccessToken = body.AccessToken;
        CurrentDisplayName = body.DisplayName;
        return CurrentDisplayName;
    }

    // The server-side source of truth is updated separately via ChatHub.ChangeDisplayName (see
    // ChatViewModel) - this just keeps what Settings pre-fills next time in sync for this run.
    public void UpdateLocalDisplayName(string newName) => CurrentDisplayName = newName;

    private static async Task<string> ExtractErrorAsync(HttpResponseMessage resp)
    {
        if (resp.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            return "Too many attempts. Please wait a moment and try again.";

        try
        {
            var doc = await resp.Content.ReadFromJsonAsync<Dictionary<string, string>>();
            if (doc is not null && doc.TryGetValue("error", out var msg) && !string.IsNullOrWhiteSpace(msg))
                return msg;
        }
        catch
        {
            // Response body wasn't the { "error": "..." } shape we expected - fall through.
        }

        return "Something went wrong. Please try again.";
    }
}
