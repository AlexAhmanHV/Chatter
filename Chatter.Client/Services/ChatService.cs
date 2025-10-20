// Services/ChatService.cs
using Microsoft.AspNetCore.SignalR.Client;

namespace Chatter.Client.Services;

public class ChatService
{
    private readonly SupabaseAuthService _auth;
    private HubConnection? _conn;

    public event Action<string, string>? MessageReceived;
    public event Action<string, string>? DisplayNameChanged;
    public event Action<IReadOnlyList<string>>? ChatsForMeUpdated;
    public event Action<IReadOnlyList<string>>? ChatsUpdated; // optional:
    public event Action<string, string, string>? ChatMessageReceived; // (chatId,user,msg)
    public event Action<IReadOnlyList<string>>? OnlineUsersUpdated;
    public event Action<string, string>? AddedChat;                // (chatId, fromUser)
    public event Action<string, string, string>? DmNotify;         // (chatId, fromUser, message)


    public Func<string?>? OnGetCurrentDisplayName { get; set; }
    public bool IsConnected => _conn?.State == HubConnectionState.Connected;

    public ChatService(SupabaseAuthService auth) => _auth = auth;

    public async Task StartAsync(string baseUrl)
    {
        _conn = new HubConnectionBuilder()
            .WithUrl($"{baseUrl}/hub/chat", options =>
            {
                options.AccessTokenProvider = () => Task.FromResult(_auth.AccessToken);
            })
            .WithAutomaticReconnect()
            .Build();

        // Global broadcast (legacy)
        _conn.On<string, string>("ReceiveMessage", (user, msg) =>
            MessageReceived?.Invoke(user, msg));

        _conn.On<string, string>("DisplayNameChanged", (oldName, newName) =>
            DisplayNameChanged?.Invoke(oldName, newName));

        // Roster
        _conn.On<List<string>>("OnlineUsers", list =>
            OnlineUsersUpdated?.Invoke((list ?? new()).AsReadOnly()));

        // Chats + per-chat messages
        _conn.On<List<string>>("ChatsForMe", list =>
            ChatsForMeUpdated?.Invoke((list ?? new()).AsReadOnly()));

        _conn.On<List<string>>("ChatsUpdated", list =>
            ChatsUpdated?.Invoke((list ?? new()).AsReadOnly()));

        _conn.On<string, string, string>("ReceiveChatMessage", (chatId, user, msg) =>
            ChatMessageReceived?.Invoke(chatId, user, msg));

            _conn.On<string, string>("AddedChat", (chatId, fromUser) =>
        AddedChat?.Invoke(chatId, fromUser));

        _conn.On<string, string, string>("DmNotify", (chatId, fromUser, msg) =>
        DmNotify?.Invoke(chatId, fromUser, msg));

        _conn.Reconnected += async _ =>
        {
            try
            {
                var name = OnGetCurrentDisplayName?.Invoke();
                if (!string.IsNullOrWhiteSpace(name))
                    await SetDisplayNameAsync(name!);

                // refresh roster and chats
                var roster = await GetOnlineUsersAsync();
                OnlineUsersUpdated?.Invoke(roster);

                var myChats = await GetMyChatsAsync();
                ChatsForMeUpdated?.Invoke(myChats);
            }
            catch { }
        };

        await _conn.StartAsync();

        // After connect: set name, seed roster + chats
        var initialName = OnGetCurrentDisplayName?.Invoke();
        if (!string.IsNullOrWhiteSpace(initialName))
            await SetDisplayNameAsync(initialName!);

        var initialRoster = await GetOnlineUsersAsync();
        OnlineUsersUpdated?.Invoke(initialRoster);

        var myChats2 = await GetMyChatsAsync();
        ChatsForMeUpdated?.Invoke(myChats2);
    }

    // ===== Global (legacy) =====
    public Task SendAsync(string user, string msg) =>
        _conn?.SendAsync("SendMessage", user, msg) ?? Task.CompletedTask;

    // ===== Identity =====
    public Task SetDisplayNameAsync(string name) =>
        _conn?.SendAsync("SetDisplayName", name) ?? Task.CompletedTask;

    public Task ChangeDisplayNameAsync(string newName) =>
        _conn?.SendAsync("ChangeDisplayName", newName) ?? Task.CompletedTask;

    // ===== Roster =====
    public async Task<IReadOnlyList<string>> GetOnlineUsersAsync()
    {
        if (_conn is null) return Array.Empty<string>();
        var list = await _conn.InvokeAsync<List<string>>("GetOnlineUsers");
        return (list ?? new()).AsReadOnly();
    }

    // ===== Chats API =====
    public async Task<IReadOnlyList<string>> GetMyChatsAsync()
    {
        if (_conn is null) return Array.Empty<string>();
        var list = await _conn.InvokeAsync<List<string>>("GetMyChats");
        return (list ?? new()).AsReadOnly();
    }

    public Task JoinChatAsync(string chatId) =>
        _conn?.SendAsync("JoinChat", chatId) ?? Task.CompletedTask;

    public Task LeaveChatAsync(string chatId) =>
        _conn?.SendAsync("LeaveChat", chatId) ?? Task.CompletedTask;

    public Task<string?> CreateDmAsync(string otherDisplayName) =>
        _conn is null
            ? Task.FromResult<string?>(null)
            : _conn.InvokeAsync<string>("CreateDm", otherDisplayName);

    public Task SendToChatAsync(string chatId, string user, string message) =>
        _conn?.SendAsync("SendToChat", chatId, user, message) ?? Task.CompletedTask;

    // ===== Stop =====
    public async Task StopAsync()
    {
        if (_conn is null) return;
        await _conn.StopAsync();
        await _conn.DisposeAsync();
        _conn = null;
    }
}
