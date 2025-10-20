// Services/ChatService.cs
using Microsoft.AspNetCore.SignalR.Client;

namespace Chatter.Client.Services;

public class ChatService
{
    private readonly SupabaseAuthService _auth;
    private HubConnection? _conn;

    public event Action<string, string>? MessageReceived;
    public event Action<string, string>? DisplayNameChanged;

    // NEW: whenever the server sends a full roster
    public event Action<IReadOnlyList<string>>? OnlineUsersUpdated;

    public Func<string?>? OnGetCurrentDisplayName { get; set; }

    public bool IsConnected => _conn?.State == HubConnectionState.Connected;

    public ChatService(SupabaseAuthService auth) => _auth = auth;

    public async Task StartAsync(string baseUrl)
    {
        _conn = new HubConnectionBuilder()
            .WithUrl($"{baseUrl}/hub/chat", options =>
            {
                options.AccessTokenProvider = () =>
                    Task.FromResult(_auth.AccessToken);
            })
            .WithAutomaticReconnect()
            .Build();

        _conn.On<string, string>("ReceiveMessage", (user, msg) =>
            MessageReceived?.Invoke(user, msg));

        _conn.On<string, string>("DisplayNameChanged", (oldName, newName) =>
            DisplayNameChanged?.Invoke(oldName, newName));

        // NEW: full roster pushed by the server
        _conn.On<List<string>>("OnlineUsers", list =>
            OnlineUsersUpdated?.Invoke(list.AsReadOnly()));

        _conn.Reconnected += async _ =>
        {
            try
            {
                var name = OnGetCurrentDisplayName?.Invoke();
                if (!string.IsNullOrWhiteSpace(name))
                    await SetDisplayNameAsync(name!);

                // seed roster again on reconnect
                var roster = await GetOnlineUsersAsync();
                OnlineUsersUpdated?.Invoke(roster);
            }
            catch { }
        };

        await _conn.StartAsync();

        // after connect: set our name and fetch initial roster
        var initialName = OnGetCurrentDisplayName?.Invoke();
        if (!string.IsNullOrWhiteSpace(initialName))
            await SetDisplayNameAsync(initialName!);

        var initialRoster = await GetOnlineUsersAsync();
        OnlineUsersUpdated?.Invoke(initialRoster);
    }

    public Task SendAsync(string user, string msg) =>
        _conn?.SendAsync("SendMessage", user, msg) ?? Task.CompletedTask;

    public Task SetDisplayNameAsync(string name) =>
        _conn?.SendAsync("SetDisplayName", name) ?? Task.CompletedTask;

    public Task ChangeDisplayNameAsync(string newName) =>
        _conn?.SendAsync("ChangeDisplayName", newName) ?? Task.CompletedTask;

    // NEW: ask server for current list explicitly
    public async Task<IReadOnlyList<string>> GetOnlineUsersAsync()
    {
        if (_conn is null) return Array.Empty<string>();
        var list = await _conn.InvokeAsync<List<string>>("GetOnlineUsers");
        return (list ?? new List<string>()).AsReadOnly();
    }

    public async Task StopAsync()
    {
        if (_conn is null) return;
        await _conn.StopAsync();
        await _conn.DisposeAsync();
        _conn = null;
    }
}
