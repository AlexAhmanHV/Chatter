using Microsoft.AspNetCore.SignalR.Client;
using Chatter.Client.Services;

namespace Chatter.Client.Services;

public class ChatService
{
    private readonly SupabaseAuthService _auth;
    private HubConnection? _conn;

    public event Action<string, string>? MessageReceived;

    public ChatService(SupabaseAuthService auth)
    {
        _auth = auth;
    }

    public async Task StartAsync(string baseUrl)
    {
        _conn = new HubConnectionBuilder()
            .WithUrl($"{baseUrl}/hub/chat", options =>
            {
                options.AccessTokenProvider = () =>
                {
                    // supply Supabase JWT to the hub
                    var token = _auth.AccessToken;
                    return Task.FromResult(token);
                };
            })
            .WithAutomaticReconnect()
            .Build();

        _conn.On<string, string>("ReceiveMessage", (user, msg) =>
            MessageReceived?.Invoke(user, msg));

        await _conn.StartAsync();
    }

    public Task SendAsync(string user, string msg) =>
        _conn?.SendAsync("SendMessage", user, msg) ?? Task.CompletedTask;
}
