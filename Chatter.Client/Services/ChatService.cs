/*
File: ChatService.cs

What this file does:
- Purpose: Client-side SignalR service that manages the real-time connection to the chat hub, raises UI-friendly events,
  and exposes async methods for presence, chats, messages, typing, and identity.
- How: Builds a HubConnection, subscribes to server-to-client events, re-seeds state on (re)connect, and provides
  null-safe wrappers around hub invocations so the rest of the app stays simple.
- Where used: Injected into ChatViewModel. The VM subscribes to the events and calls the async APIs (JoinChat, SendToChat, etc.)
*/

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;

namespace Chatter.Client.Services;

public class ChatService
{
    /* Fields & connection state
       Holds the auth provider, SignalR connection handle, and quick helpers like IsConnected.
       The BaseUrl is supplied by the caller (ViewModel) to StartAsync.
    */
    private readonly SupabaseAuthService _auth;
    private HubConnection? _conn;
    private const string LobbyId = "Lobby";

    public Func<string?>? OnGetCurrentDisplayName { get; set; }
    public bool IsConnected => _conn?.State == HubConnectionState.Connected;

    /* UI-facing events
       These events decouple network events from the ViewModel/UI.
       The ViewModel subscribes to update typing indicators, rosters, message lists, and presence.
    */
    public event EventHandler<(string ChannelId, string User, bool IsTyping)>? TypingChanged;
    public event Action<string, string>? OtherDisplayNameChanged;
    public event Action<string, string>? MessageReceived;
    public event Action<IReadOnlyList<string>>? OnlineUsersUpdated;
    public event Action<IReadOnlyList<string>>? ChatsForMeUpdated;
    public event Action<IReadOnlyList<string>>? ChatsUpdated;
    public event Action<string, string, string>? ChatMessageReceived; 
    public event Action<string, string>? AddedChat;                   
    public event Action<string, string, string>? DmNotify;            

    // Server feature: aliases + presence snapshots/deltas
    public event Action<Dictionary<string, string>>? NameAliasesReceived; 
    public event Action<Dictionary<string, string>>? StatusesUpdated;
    public event Action<string, string>? StatusChanged;

    /* Constructor
       Stores the auth dependency used to supply an access token when establishing the hub connection.
    */
    public ChatService(SupabaseAuthService auth) => _auth = auth;

    /* Presence APIs
       Set a presence value and fetch a snapshot of all statuses (if the server exposes these endpoints).
       Safe to call even if not connected—no-ops or empty results are returned.
    */
    public Task SetStatusAsync(string status) =>
        _conn?.SendAsync("SetStatus", status) ?? Task.CompletedTask;

    public async Task<Dictionary<string, string>> GetStatusesAsync()
    {
        if (_conn is null) return new();
        var dict = await _conn.InvokeAsync<Dictionary<string, string>>("GetStatuses");
        return dict ?? new();
    }

    /* Aliases API (optional)
       Some servers expose a mapping of historical -> current display names. This fetches that snapshot.
       The ViewModel uses it to normalize names in the roster and DM labels.
    */
    public async Task<Dictionary<string, string>> GetNameAliasesAsync()
    {
        if (_conn is null) return new();
        var dict = await _conn.InvokeAsync<Dictionary<string, string>>("GetNameAliases");
        return dict ?? new();
    }

    /* Start & wire hub
       Builds the SignalR connection, attaches all server → client handlers, configures reconnect behavior,
       then starts the connection and seeds initial client state (presence, lists, aliases, and identity).
    */
    public async Task StartAsync(string baseUrl)
    {
        _conn = new HubConnectionBuilder()
            .WithUrl($"{baseUrl}/hub/chat", options =>
            {
                options.AccessTokenProvider = () => Task.FromResult(_auth.AccessToken);
            })
            .WithAutomaticReconnect()
            .Build();

        // ----- Handlers: Typing -----
        _conn.On<string, string, bool>("Typing", (channelId, user, isTyping) =>
        {
            var payload = (ChannelId: channelId, User: user, IsTyping: isTyping);
            TypingChanged?.Invoke(this, payload);
        });

        // ----- Handlers: Presence -----
        _conn.On<Dictionary<string, string>>("Statuses", dict =>
            StatusesUpdated?.Invoke(dict ?? new()));

        _conn.On<string, string>("StatusChanged", (displayName, status) =>
            StatusChanged?.Invoke(displayName, status));

        // ----- Handlers: Legacy broadcast (optional) -----
        _conn.On<string, string>("ReceiveMessage", (user, msg) =>
            MessageReceived?.Invoke(user, msg));

        // ----- Handlers: Name change notifications -----
        _conn.On<string, string>("DisplayNameChanged", (oldName, newName) =>
            ChatMessageReceived?.Invoke(LobbyId, "system",
                $"{oldName} changed their name to “{newName}”."));
        _conn.On<string, string>("DisplayNameChanged", (oldName, newName) =>
            OtherDisplayNameChanged?.Invoke(oldName, newName));

        _conn.On<string>("LobbySystemMessage", text =>
            ChatMessageReceived?.Invoke(LobbyId, "system", text));

        // ----- Handlers: Rosters & chat lists -----
        _conn.On<List<string>>("OnlineUsers", list =>
            OnlineUsersUpdated?.Invoke((list ?? new()).AsReadOnly()));

        _conn.On<List<string>>("ChatsForMe", list =>
            ChatsForMeUpdated?.Invoke((list ?? new()).AsReadOnly()));

        _conn.On<List<string>>("ChatsUpdated", list =>
            ChatsUpdated?.Invoke((list ?? new()).AsReadOnly()));

        // ----- Handlers: Per-chat messages -----
        _conn.On<string, string, string>("ReceiveChatMessage", (chatId, user, msg) =>
            ChatMessageReceived?.Invoke(chatId, user, msg));

        // ----- Handlers: DM helpers -----
        _conn.On<string, string>("AddedChat", (chatId, fromUser) =>
            AddedChat?.Invoke(chatId, fromUser));

        _conn.On<string, string, string>("DmNotify", (chatId, fromUser, msg) =>
            DmNotify?.Invoke(chatId, fromUser, msg));

        // Reconnect flow: re-assert identity and refresh all lists/snapshots
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
                catch {}
            }
            catch
            {
                // ignore reconnect errors; SignalR will keep trying
            }
        };

        // Start the connection
        await _conn.StartAsync();

        // Initial seed after connect
        try
        {
            var statuses = await GetStatusesAsync();
            StatusesUpdated?.Invoke(statuses);
        }
        catch { }

        var initialName = OnGetCurrentDisplayName?.Invoke();
        if (!string.IsNullOrWhiteSpace(initialName))
            await SetDisplayNameAsync(initialName!);

        OnlineUsersUpdated?.Invoke(await GetOnlineUsersAsync());
        ChatsForMeUpdated?.Invoke(await GetMyChatsAsync());

        try
        {
            var initialAliases = await GetNameAliasesAsync();
            NameAliasesReceived?.Invoke(initialAliases);
        }
        catch { }
    }

    /* Global (legacy) messaging
       Sends a message on the legacy/global channel if your server supports it.
       Safe no-op if not connected.
    */
    public Task SendAsync(string user, string msg) =>
        _conn?.SendAsync("SendMessage", user, msg) ?? Task.CompletedTask;

    /* Identity APIs
       Sets or changes the local user's display name on the server.
       The ViewModel calls these when the user updates their name.
    */
    public Task SetDisplayNameAsync(string name) =>
        _conn?.SendAsync("SetDisplayName", name) ?? Task.CompletedTask;

    public Task ChangeDisplayNameAsync(string newName) =>
        _conn?.SendAsync("ChangeDisplayName", newName) ?? Task.CompletedTask;

    /* Typing indicator
       Notifies the server that this user started/stopped typing in a channel.
       The server relays Typing events, which we surface via the TypingChanged event.
    */
    public Task SendTypingAsync(string channelId, string user, bool isTyping) =>
        _conn?.InvokeAsync("Typing", channelId, user, isTyping) ?? Task.CompletedTask;

    /* Roster APIs
       Fetches the list of currently online users from the server.
       Returned as a read-only list for safety in consumers.
    */
    public async Task<IReadOnlyList<string>> GetOnlineUsersAsync()
    {
        if (_conn is null) return Array.Empty<string>();
        var list = await _conn.InvokeAsync<List<string>>("GetOnlineUsers");
        return (list ?? new()).AsReadOnly();
    }

    /* Chats APIs
       Fetches the current user's chat list, joins/leaves chats, creates DMs, and sends chat messages.
       All methods are safe no-ops when not connected and return reasonable defaults.
    */
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
        : _conn.InvokeAsync<string?>("CreateDm", otherDisplayName);

    public Task SendToChatAsync(string chatId, string user, string message) =>
        _conn?.SendAsync("SendToChat", chatId, user, message) ?? Task.CompletedTask;

    /* Stop & dispose
       Gracefully stops the connection and releases resources.
       After StopAsync, this service can be started again with StartAsync.
    */
    public async Task StopAsync()
    {
        if (_conn is null) return;
        await _conn.StopAsync();
        await _conn.DisposeAsync();
        _conn = null;
    }
}
