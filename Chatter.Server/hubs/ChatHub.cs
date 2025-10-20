// Hubs/ChatHub.cs
using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;

namespace Chatter.Server.Hubs;

public class ChatHub : Hub
{
    private const string LobbyId = "Lobby";

    // connectionId -> displayName (may be empty until SetDisplayName is called)
    private static readonly ConcurrentDictionary<string, string> _names = new();

    // chatId -> set of displayNames who are members of that chat (by logical identity)
    private static readonly ConcurrentDictionary<string, HashSet<string>> _chatMembers =
        new(StringComparer.OrdinalIgnoreCase);

    // chatId -> set of connectionIds that have joined the SignalR group for that chat
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _chatMembersByConn =
        new(StringComparer.OrdinalIgnoreCase);

    // dm chatId -> (user1, user2)
    private static readonly ConcurrentDictionary<string, (string User1, string User2)> _dmParticipants =
        new(StringComparer.OrdinalIgnoreCase);

    // connectionId -> displayName
    private static readonly ConcurrentDictionary<string, string> _connectionToDisplayName =
        new(StringComparer.OrdinalIgnoreCase);

    // displayName -> set of connectionIds (tabs/devices)
    private static readonly ConcurrentDictionary<string, HashSet<string>> _displayNameToConnections =
        new(StringComparer.OrdinalIgnoreCase);

    // ----- Connection lifecycle -----

    public override async Task OnConnectedAsync()
    {
        // Mark as connected but unnamed for now
        _names[Context.ConnectionId] = string.Empty;

        // Ensure Lobby chat exists
        _chatMembers.TryAdd(LobbyId, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        // Add this connection to the Lobby SIGNALR group so it can receive lobby traffic
        await Groups.AddToGroupAsync(Context.ConnectionId, LobbyId);

        // IMPORTANT: Do NOT broadcast roster here; the user is still "Unknown".
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? ex)
    {
        // Remove reverse indexes
        if (_connectionToDisplayName.TryRemove(Context.ConnectionId, out var name))
        {
            if (_displayNameToConnections.TryGetValue(name, out var set))
            {
                set.Remove(Context.ConnectionId);
                if (set.Count == 0)
                    _displayNameToConnections.TryRemove(name, out _);
            }
        }

        // Remove this connectionId from all group membership tracking
        foreach (var kvp in _chatMembersByConn)
            kvp.Value.TryRemove(Context.ConnectionId, out _);

        _names.TryRemove(Context.ConnectionId, out _);

        await BroadcastRosterAsync();
        await base.OnDisconnectedAsync(ex);
    }

    // ----- Identity -----

    // Called by client once it knows the user's display name
    public async Task SetDisplayName(string displayName)
    {
        _names[Context.ConnectionId] = displayName;

        _connectionToDisplayName[Context.ConnectionId] = displayName;
        _displayNameToConnections.AddOrUpdate(displayName,
            _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Context.ConnectionId },
            (_, set) => { set.Add(Context.ConnectionId); return set; });

        // Ensure the user is a logical member of Lobby
        _chatMembers.AddOrUpdate(LobbyId,
            _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { displayName },
            (_, set) => { set.Add(displayName); return set; });

        await BroadcastRosterAsync();
        await SendChatsToCallerAsync(displayName);
    }

    public async Task ChangeDisplayName(string newDisplayName)
    {
        var connId = Context.ConnectionId;
        var old = _names.TryGetValue(connId, out var prev) && !string.IsNullOrWhiteSpace(prev) ? prev : "Unknown";

        // Update primary map
        _names[connId] = newDisplayName;

        // Move this connection in reverse indexes
        if (_connectionToDisplayName.TryGetValue(connId, out var prevName))
        {
            if (_displayNameToConnections.TryGetValue(prevName, out var prevSet))
            {
                prevSet.Remove(connId);
                if (prevSet.Count == 0) _displayNameToConnections.TryRemove(prevName, out _);
            }
        }
        _connectionToDisplayName[connId] = newDisplayName;
        _displayNameToConnections.AddOrUpdate(newDisplayName,
            _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { connId },
            (_, set) => { set.Add(connId); return set; });

        // Migrate membership in all chats from old -> new
        foreach (var set in _chatMembers.Values)
        {
            if (set.Remove(old))
                set.Add(newDisplayName);
        }

        // Make sure this connection is in the Lobby SIGNALR group (defensive)
        await Groups.AddToGroupAsync(connId, LobbyId);

        // Broadcast both styles so client can handle either
        await Clients.Group(LobbyId).SendAsync("DisplayNameChanged", old, newDisplayName);

        await BroadcastRosterAsync();
        await SendChatsToCallerAsync(newDisplayName);
    }

    // ----- Chats API -----

    // Returns list of chat ids for the current user (Lobby + DMs they’re part of)
    public Task<List<string>> GetMyChats()
    {
        var me = GetMe();
        var list = _chatMembers
            .Where(kvp => kvp.Value.Contains(me))
            .Select(kvp => kvp.Key)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Ensure lobby present
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

        // Track participants for DM notify
        _dmParticipants[chatId] = (me, otherDisplayName);

        // Sender joins the group
        await Groups.AddToGroupAsync(Context.ConnectionId, chatId);

        // Notify recipient that a chat exists (so it appears in their list)
        var recipientConns = ConnectionsFor(otherDisplayName).ToList();
        if (recipientConns.Count > 0)
            await Clients.Clients(recipientConns).SendAsync("AddedChat", chatId, me);

        // Also add on the caller (harmless redundancy)
        await Clients.Caller.SendAsync("AddedChat", chatId, me);

        // Optional global "chats updated"
        await Clients.All.SendAsync("ChatsUpdated", new[] { chatId });

        return chatId;
    }

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

    public async Task SendToChat(string chatId, string displayName, string message)
    {
        // 1) Deliver to all connections currently joined to this chat group
        await Clients.Group(chatId).SendAsync("ReceiveChatMessage", chatId, displayName, message);

        // 2) If DM, notify the other participant(s) who are online but haven't joined this chat group yet
        if (IsDirectMessageChat(chatId, out var participants))
        {
            // which connectionIds are already in the group?
            var groupMembers = _chatMembersByConn.TryGetValue(chatId, out var members)
                ? members.Keys
                : Enumerable.Empty<string>();

            // All live connectionIds for both DM participants
            var allConnIds = ResolveLiveConnectionsFor(participants);

            // Exclude sender's current connectionId and any that are already in the group
            var senderConnId = Context.ConnectionId;
            var notInGroup = allConnIds.Where(cid => cid != senderConnId && !groupMembers.Contains(cid));

            foreach (var cid in notInGroup)
                await Clients.Client(cid).SendAsync("DmNotify", chatId, displayName, message);
        }
    }

    // ----- Legacy global broadcast (kept for compatibility) -----

    public Task SendMessage(string user, string message)
        => Clients.All.SendAsync("ReceiveMessage", user, message);

    // ----- Roster -----

    public Task<IReadOnlyList<string>> GetOnlineUsers()
    {
        // Use reverse index keys to avoid dups and exclude empties
        var list = _displayNameToConnections.Keys
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

    // ----- Helpers -----

    private static string MakeDmId(string a, string b)
    {
        var pair = new[] { a, b }.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        return $"dm:{pair[0]}|{pair[1]}";
    }

    private string GetMe()
        => _names.TryGetValue(Context.ConnectionId, out var me) && !string.IsNullOrWhiteSpace(me) ? me : "Unknown";

    private static bool IsDm(string chatId)
        => chatId.StartsWith("dm:", StringComparison.OrdinalIgnoreCase);

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

    private IEnumerable<string> ConnectionsFor(string displayName)
        => _names.Where(kvp => string.Equals(kvp.Value, displayName, StringComparison.OrdinalIgnoreCase))
                 .Select(kvp => kvp.Key);

    private IEnumerable<string> ResolveLiveConnectionsFor(IEnumerable<string> displayNames)
    {
        foreach (var dn in displayNames)
        {
            if (_displayNameToConnections.TryGetValue(dn, out var set))
            {
                foreach (var cid in set)
                    yield return cid;
            }
        }
    }
}
