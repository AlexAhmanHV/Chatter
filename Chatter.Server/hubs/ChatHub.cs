// Hubs/ChatHub.cs
using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;

namespace Chatter.Server.Hubs;

public class ChatHub : Hub
{
    private static readonly ConcurrentDictionary<string, string> _names = new();

    // Track which chats exist and who's in them (display-name based for demo)
    private static readonly ConcurrentDictionary<string, HashSet<string>> _chatMembers =
        new(StringComparer.OrdinalIgnoreCase);

    // Keep track of who joined each chat by connectionId
    // (Thread-safe set; you can use ConcurrentDictionary<string, byte> as a HashSet substitute)
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _chatMembersByConn
    = new(StringComparer.OrdinalIgnoreCase);

    // Keeps track of DM participants per chatId
    private static readonly ConcurrentDictionary<string, (string User1, string User2)> _dmParticipants 
        = new(StringComparer.OrdinalIgnoreCase);

    // Keeps track of which connectionId belongs to which displayName
    private static readonly ConcurrentDictionary<string, string> _connectionToDisplayName 
        = new(StringComparer.OrdinalIgnoreCase);

    // Keeps track of which displayName is connected with which connectionIds (for multiple tabs/devices)
    private static readonly ConcurrentDictionary<string, HashSet<string>> _displayNameToConnections 
        = new(StringComparer.OrdinalIgnoreCase);


    private const string LobbyId = "Lobby";

    // Check if a chat is a DM (two participants)
    private bool IsDirectMessageChat(string chatId, out string[] participants)
    {
        if (_dmParticipants.TryGetValue(chatId, out var tup))
        {
            participants = new[] { tup.User1, tup.User2 };
            return true;
        }
        participants = Array.Empty<string>();
        return false;
    }

    // Utility: resolve live connectionIds for display names
    private IEnumerable<string> ResolveLiveConnectionsFor(IEnumerable<string> displayNames)
    {
        foreach (var dn in displayNames)
            if (_displayNameToConnections.TryGetValue(dn, out var set))
                foreach (var cid in set)
                    yield return cid;
    }

    public override async Task OnConnectedAsync()
    {
        _names[Context.ConnectionId] = string.Empty;

        _chatMembers.TryAdd(LobbyId, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        await Groups.AddToGroupAsync(Context.ConnectionId, LobbyId);
        await BroadcastRosterAsync();
        await base.OnConnectedAsync();
    }


    // Remove the connection from all indexes when a socket drops
    public override async Task OnDisconnectedAsync(Exception? ex)
    {
        if (_connectionToDisplayName.TryRemove(Context.ConnectionId, out var name))
        {
            if (_displayNameToConnections.TryGetValue(name, out var set))
            {
                set.Remove(Context.ConnectionId);
                if (set.Count == 0)
                    _displayNameToConnections.TryRemove(name, out _);
            }
        }

        foreach (var kvp in _chatMembersByConn)
            kvp.Value.TryRemove(Context.ConnectionId, out _);

        _names.TryRemove(Context.ConnectionId, out _);

        await BroadcastRosterAsync();
        await base.OnDisconnectedAsync(ex);
    }

    // Called from your existing SetDisplayName(displayName)
    public async Task SetDisplayName(string displayName)
    {
        // NEW: keep _names in sync so GetMe() works correctly
        _names[Context.ConnectionId] = displayName;

        _connectionToDisplayName[Context.ConnectionId] = displayName;

        _displayNameToConnections.AddOrUpdate(displayName,
            _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Context.ConnectionId },
            (_, set) => { set.Add(Context.ConnectionId); return set; });

        await BroadcastRosterAsync();
    }


    public async Task ChangeDisplayName(string newDisplayName)
    {
        var connId = Context.ConnectionId;
        var old = _names.TryGetValue(connId, out var prev) ? prev : "Unknown";
        _names[connId] = newDisplayName;

        // migrate membership in all chats
        foreach (var set in _chatMembers.Values)
        {
            if (set.Remove(old))
                set.Add(newDisplayName);
        }

        await Clients.All.SendAsync("DisplayNameChanged", old, newDisplayName);
        await BroadcastRosterAsync();
        await SendChatsToCallerAsync(newDisplayName);
    }

    // ====== Chats API ======

    // Returns list of chat ids for the current user (Lobby + DMs they’re part of)
    public Task<List<string>> GetMyChats()
    {
        var me = GetMe();
        var list = _chatMembers
            .Where(kvp => kvp.Value.Contains(me))
            .Select(kvp => kvp.Key)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // make sure lobby is always present
        if (!list.Contains(LobbyId)) list.Insert(0, LobbyId);
        return Task.FromResult(list);
    }

    public async Task<string> CreateDm(string otherDisplayName)
    {
        var me = GetMe();
        var chatId = MakeDmId(me, otherDisplayName);

        var set = _chatMembers.GetOrAdd(chatId, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        set.Add(me);
        set.Add(otherDisplayName);

        // NEW: mark this chat as a DM between the two users
        _dmParticipants[chatId] = (me, otherDisplayName);

        await Groups.AddToGroupAsync(Context.ConnectionId, chatId);

        // Tell the recipient the chat now exists (so it shows up in their left list)
        var recipientConns = ConnectionsFor(otherDisplayName).ToList();
        if (recipientConns.Count > 0)
            await Clients.Clients(recipientConns).SendAsync("AddedChat", chatId, me);

        // Optional: Add to the sender too (UI already has the tab via Join, but harmless):
        await Clients.Caller.SendAsync("AddedChat", chatId, me);

        await Clients.All.SendAsync("ChatsUpdated", new[] { chatId });
        return chatId;
    }


    // // Create a DM server-side
    // public Task<string> CreateDmChat(string user1, string user2)
    // {
    //     var chatId = $"dm:{Guid.NewGuid():N}";
    //     _dmParticipants[chatId] = (user1, user2);

    //     // Tell both users a new chat exists (appears in their left list)
    //     var both = new[] { user1, user2 };
    //     var connIds = ResolveLiveConnectionsFor(both);
    //     foreach (var cid in connIds)
    //         _ = Clients.Client(cid).SendAsync("AddedChat", chatId, /* createdBy: */ user1);

    //     return Task.FromResult(chatId);
    // }

    public async Task JoinChat(string chatId)
    {
        var set = _chatMembersByConn.GetOrAdd(chatId, _ => new ConcurrentDictionary<string, byte>());
        set[Context.ConnectionId] = 1;
        await Groups.AddToGroupAsync(Context.ConnectionId, chatId);
    }

    public Task LeaveChat(string chatId)
    {
        var me = GetMe();
        _ = Groups.RemoveFromGroupAsync(Context.ConnectionId, chatId);
        if (_chatMembers.TryGetValue(chatId, out var set))
            set.Remove(me);
        return Task.CompletedTask;
    }

    // When sending to a chat:
    public async Task SendToChat(string chatId, string displayName, string message)
    {
        // 1) Broadcast to everyone who joined the chat group
        await Clients.Group(chatId).SendAsync("ReceiveChatMessage", chatId, displayName, message);

        // 2) If DM: notify recipient(s) who haven't joined yet
        if (IsDirectMessageChat(chatId, out var participants))
        {
            // group members = connectionIds that joined this chat
            var groupMembers = _chatMembersByConn.TryGetValue(chatId, out var members)
                ? members.Keys
                : Enumerable.Empty<string>();

            // all live connections for both DM participants
            var allConnIds = ResolveLiveConnectionsFor(participants);

            // current sender's connectionId (exclude from notify)
            var senderConnId = Context.ConnectionId;

            // notify only connectionIds that are (a) live, (b) not in group, (c) not the sender
            var notInGroup = allConnIds
                .Where(cid => cid != senderConnId && !groupMembers.Contains(cid));

            foreach (var cid in notInGroup)
                await Clients.Client(cid).SendAsync("DmNotify", chatId, displayName, message);
        }
    }
    // ====== Existing message + roster ======

    public Task SendMessage(string user, string message)
        => Clients.All.SendAsync("ReceiveMessage", user, message);

    public Task<IReadOnlyList<string>> GetOnlineUsers()
    {
        var list = _names.Values
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList()
            .AsReadOnly();

        return Task.FromResult((IReadOnlyList<string>)list);
    }

    private Task BroadcastRosterAsync()
    {
        var online = _displayNameToConnections.Keys
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Clients.All.SendAsync("OnlineUsers", online);
    }

    private Task SendChatsToCallerAsync(string me)
    {
        var list = _chatMembers
            .Where(kvp => kvp.Value.Contains(me))
            .Select(kvp => kvp.Key)
            .Concat(new[] { LobbyId })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Clients.Caller.SendAsync("ChatsForMe", list);
    }

    private static string MakeDmId(string a, string b)
    {
        var pair = new[] { a, b }.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        return $"dm:{pair[0]}|{pair[1]}";
    }

    private string GetMe()
        => _names.TryGetValue(Context.ConnectionId, out var me) && !string.IsNullOrWhiteSpace(me) ? me : "Unknown";


    private static bool IsDm(string chatId) =>
        chatId.StartsWith("dm:", StringComparison.OrdinalIgnoreCase);

    private IEnumerable<string> ConnectionsFor(string displayName) =>
        _names.Where(kvp => string.Equals(kvp.Value, displayName, StringComparison.OrdinalIgnoreCase))
              .Select(kvp => kvp.Key);
}
