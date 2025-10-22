using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;

namespace Chatter.Client.Services;

public class ChatService
{
    private readonly SupabaseAuthService _auth;
    private HubConnection? _conn;

    // Renames (others)
    public event Action<string, string>? OtherDisplayNameChanged;

    // Optional legacy global broadcast
    public event Action<string, string>? MessageReceived;

    // Lists & rosters
    public event Action<IReadOnlyList<string>>? OnlineUsersUpdated;
    public event Action<IReadOnlyList<string>>? ChatsForMeUpdated;
    public event Action<IReadOnlyList<string>>? ChatsUpdated;

    // Per-chat messages
    public event Action<string, string, string>? ChatMessageReceived; // (chatId, user, msg)

    // DMs
    public event Action<string, string>? AddedChat;                // (chatId, fromUser)
    public event Action<string, string, string>? DmNotify;         // (chatId, fromUser, message)

    private const string LobbyId = "Lobby";

    // Aliases snapshot (oldName -> latestName), if server supports
    public event Action<Dictionary<string, string>>? NameAliasesReceived;

    // Presence
    // A snapshot/delta from server: displayName -> "online|away|busy|offline"
    public event Action<Dictionary<string, string>>? StatusesUpdated;
    // A single-user change push (optional)
    public event Action<string, string>? StatusChanged;

    public Func<string?>? OnGetCurrentDisplayName { get; set; }
    public bool IsConnected => _conn?.State == HubConnectionState.Connected;

    public ChatService(SupabaseAuthService auth) => _auth = auth;

    // --- Presence APIs ---
    public Task SetStatusAsync(string status) =>
        _conn?.SendAsync("SetStatus", status) ?? Task.CompletedTask;

    public async Task<Dictionary<string, string>> GetStatusesAsync()
    {
        if (_conn is null) return new();
        var dict = await _conn.InvokeAsync<Dictionary<string, string>>("GetStatuses");
        return dict ?? new();
    }

    // --- Aliases API (optional, if server supports) ---
    public async Task<Dictionary<string, string>> GetNameAliasesAsync()
    {
        if (_conn is null) return new();
        var dict = await _conn.InvokeAsync<Dictionary<string, string>>("GetNameAliases");
        return dict ?? new();
    }

    public async Task StartAsync(string baseUrl)
    {
        _conn = new HubConnectionBuilder()
            .WithUrl($"{baseUrl}/hub/chat", options =>
            {
                options.AccessTokenProvider = () => Task.FromResult(_auth.AccessToken);
            })
            .WithAutomaticReconnect()
            .Build();

        // ----- Presence pushes -----
        // Server can push a batch map under "Statuses"
        _conn.On<Dictionary<string, string>>("Statuses", dict =>
            StatusesUpdated?.Invoke(dict ?? new()));

        // Or a single change
        _conn.On<string, string>("StatusChanged", (displayName, status) =>
            StatusChanged?.Invoke(displayName, status));

        // ----- Legacy broadcast (optional) -----
        _conn.On<string, string>("ReceiveMessage", (user, msg) =>
            MessageReceived?.Invoke(user, msg));

        // ----- Name change notifications -----
        _conn.On<string, string>("DisplayNameChanged", (oldName, newName) =>
            ChatMessageReceived?.Invoke(LobbyId, "system",
                $"{oldName} changed their name to “{newName}”."));
        _conn.On<string, string>("DisplayNameChanged", (oldName, newName) =>
            OtherDisplayNameChanged?.Invoke(oldName, newName));

        // Optional lobby system message
        _conn.On<string>("LobbySystemMessage", text =>
            ChatMessageReceived?.Invoke(LobbyId, "system", text));

        // ----- Roster & chat lists -----
        _conn.On<List<string>>("OnlineUsers", list =>
            OnlineUsersUpdated?.Invoke((list ?? new()).AsReadOnly()));

        _conn.On<List<string>>("ChatsForMe", list =>
            ChatsForMeUpdated?.Invoke((list ?? new()).AsReadOnly()));

        _conn.On<List<string>>("ChatsUpdated", list =>
            ChatsUpdated?.Invoke((list ?? new()).AsReadOnly()));

        // ----- Per-chat messages -----
        _conn.On<string, string, string>("ReceiveChatMessage", (chatId, user, msg) =>
            ChatMessageReceived?.Invoke(chatId, user, msg));

        // ----- DM helpers -----
        _conn.On<string, string>("AddedChat", (chatId, fromUser) =>
            AddedChat?.Invoke(chatId, fromUser));
        _conn.On<string, string, string>("DmNotify", (chatId, fromUser, msg) =>
            DmNotify?.Invoke(chatId, fromUser, msg));

        // ----- Reconnect: restore identity + refresh lists + statuses + aliases -----
        _conn.Reconnected += async _ =>
        {
            try
            {
                var name = OnGetCurrentDisplayName?.Invoke();
                if (!string.IsNullOrWhiteSpace(name))
                    await SetDisplayNameAsync(name!);

                OnlineUsersUpdated?.Invoke(await GetOnlineUsersAsync());
                ChatsForMeUpdated?.Invoke(await GetMyChatsAsync());

                var statuses = await GetStatusesAsync();
                StatusesUpdated?.Invoke(statuses);

                try
                {
                    var aliases = await GetNameAliasesAsync();
                    NameAliasesReceived?.Invoke(aliases);
                }
                catch { /* optional */ }
            }
            catch
            {
                // ignore
            }
        };

        // ----- Start + initial seed -----
        await _conn.StartAsync();

        // (optional) server may push an initial "Statuses" event right after connect;
        // but we proactively fetch a snapshot to be safe:
        try
        {
            var statuses = await GetStatusesAsync();
            StatusesUpdated?.Invoke(statuses);
        }
        catch { /* optional */ }

        var initialName = OnGetCurrentDisplayName?.Invoke();
        if (!string.IsNullOrWhiteSpace(initialName))
            await SetDisplayNameAsync(initialName!);

        OnlineUsersUpdated?.Invoke(await GetOnlineUsersAsync());
        ChatsForMeUpdated?.Invoke(await GetMyChatsAsync());

        // Seed aliases (if supported)
        try
        {
            var initialAliases = await GetNameAliasesAsync();
            NameAliasesReceived?.Invoke(initialAliases);
        }
        catch { /* optional */ }
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
