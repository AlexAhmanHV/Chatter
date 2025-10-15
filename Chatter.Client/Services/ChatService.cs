using Microsoft.AspNetCore.SignalR.Client;

namespace Chatter.Client.Services;

public class ChatService
{
    private HubConnection? _conn;
    public event Action<string,string>? MessageReceived;

    public async Task StartAsync(string baseUrl)
    {
        _conn = new HubConnectionBuilder()
            .WithUrl($"{baseUrl}/hub/chat")
            .WithAutomaticReconnect()
            .Build();

        _conn.On<string,string>("ReceiveMessage", (user, msg) =>
            MessageReceived?.Invoke(user, msg));

        await _conn.StartAsync();
    }

    public Task SendAsync(string user, string msg)
        => _conn?.SendAsync("SendMessage", user, msg) ?? Task.CompletedTask;
}
