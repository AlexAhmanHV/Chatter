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

    // alias map: oldName -> latestName (chains possible; use Canon() to resolve)
    private static readonly ConcurrentDictionary<string, string> _aliases =
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

    // displayName -> explicit status ("online" | "away" | "busy"). If user has no connections → offline.
    private static readonly ConcurrentDictionary<string, string> _statusByDisplayName =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] _validStatuses = new[] { "online", "away", "busy" };
    private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;

    private static readonly ConcurrentDictionary<string, string> _statusByName =
    new(StringComparer.OrdinalIgnoreCase); // name -> "online"/"away"/"busy"/"offline"  
    // ----- Connection lifecycle -----

public override async Task OnConnectedAsync()
{
    _names[Context.ConnectionId] = string.Empty;
    _chatMembers.TryAdd(LobbyId, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    await Groups.AddToGroupAsync(Context.ConnectionId, LobbyId);
    await base.OnConnectedAsync();
    // Send everyone an updated online roster and statuses (new connection soon sets name)
    await BroadcastRosterAsync();
    await BroadcastStatusesAsync();
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

        // Let clients recompute status; also push a targeted event for the name we just lost
        if (!string.IsNullOrWhiteSpace(name))
            await Clients.All.SendAsync("StatusChanged", name, GetEffectiveStatus(name));
            await BroadcastRosterAsync();
            await BroadcastStatusesAsync();
        await base.OnDisconnectedAsync(ex);
    }

    // ----- Identity / Aliases -----

    // Called by client once it knows the user's display name
    public async Task SetDisplayName(string displayName)
    {
        var connId = Context.ConnectionId;
        var canon = Canon(displayName);

        _names[connId] = canon;

        _connectionToDisplayName[connId] = canon;
        _displayNameToConnections.AddOrUpdate(canon,
            _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { connId },
            (_, set) => { set.Add(connId); return set; });

        // default to "online" if not set yet
        _statusByDisplayName.TryAdd(canon, "online");

        // Ensure the user is a logical member of Lobby
        _chatMembers.AddOrUpdate(LobbyId,
            _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { canon },
            (_, set) => { set.Add(canon); return set; });

        await BroadcastRosterAsync();
        await SendChatsToCallerAsync(canon);

        // Also send current status snapshot for this user
        await Clients.Caller.SendAsync("StatusChanged", canon, GetEffectiveStatus(canon));

        // Send alias snapshot (optional but useful for new clients)
        await Clients.Caller.SendAsync("NameAliases", await GetNameAliases());

            _statusByName.AddOrUpdate(displayName, _ => "online", (_, __) => "online");
    
    await BroadcastStatusesAsync();
    await SendChatsToCallerAsync(displayName);
    }

    private Task BroadcastStatusesAsync()
{
    // Send a compact name->status dictionary
    var snapshot = _statusByName.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
    return Clients.All.SendAsync("Statuses", snapshot);
}

    // User changes their visible name
    public async Task ChangeDisplayName(string newDisplayName)
    {
        var connId = Context.ConnectionId;

        var old = _names.TryGetValue(connId, out var prev) && !string.IsNullOrWhiteSpace(prev)
            ? prev
            : "Unknown";

        var newCanon = Canon(newDisplayName); // may compress to existing current

        // If the canonical result equals old, nothing to do
        if (Ci.Equals(newCanon, old))
        {
            // still re-seed alias snapshot to caller (harmless)
            await Clients.Caller.SendAsync("NameAliases", await GetNameAliases());
            return;
        }


            _names[connId] = newDisplayName;

    if (_statusByName.TryRemove(old, out var oldStatus))
        _statusByName[newDisplayName] = oldStatus;

        // Update aliases: old current -> new current
        _aliases[old] = newCanon;

        // Update primary map
        _names[connId] = newCanon;

        // Move this connection in reverse indexes
        if (_connectionToDisplayName.TryGetValue(connId, out var prevName))
        {
            if (_displayNameToConnections.TryGetValue(prevName, out var prevSet))
            {
                prevSet.Remove(connId);
                if (prevSet.Count == 0) _displayNameToConnections.TryRemove(prevName, out _);
            }
        }
        _connectionToDisplayName[connId] = newCanon;
        _displayNameToConnections.AddOrUpdate(newCanon,
            _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { connId },
            (_, set) => { set.Add(connId); return set; });

        // Migrate membership in all chats from old -> new
        foreach (var set in _chatMembers.Values)
        {
            if (set.Remove(old))
                set.Add(newCanon);
        }

        // Keep DM participant index consistent (just refresh tuple if encountered)
        foreach (var kv in _dmParticipants.ToArray())
        {
            var (u1, u2) = kv.Value;
            var c1 = Canon(u1);
            var c2 = Canon(u2);
            if (!Ci.Equals(u1, c1) || !Ci.Equals(u2, c2))
                _dmParticipants[kv.Key] = (c1, c2);
        }

        // Make sure this connection is in the Lobby SIGNALR group (defensive)
        await Groups.AddToGroupAsync(connId, LobbyId);

        // Broadcast both styles so client can handle either
        await Clients.Group(LobbyId).SendAsync("DisplayNameChanged", old, newCanon);
        await Clients.Group(LobbyId).SendAsync("DisplayNameChanged", old, newDisplayName);

        await BroadcastRosterAsync();

        // Push status for the affected name
        await Clients.All.SendAsync("StatusChanged", newCanon, GetEffectiveStatus(newCanon));

        // Refresh their chat list
        await SendChatsToCallerAsync(newCanon);

        // Send alias snapshot to everyone (optional: could send only to caller)
        await Clients.All.SendAsync("NameAliases", await GetNameAliases());
        await BroadcastStatusesAsync();
        await SendChatsToCallerAsync(newDisplayName);
    }

    // Returns dictionary of old->new aliases (canonicalized)
    public Task<Dictionary<string, string>> GetNameAliases()
    {
        // Build a stable copy with path compression applied
        var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in _aliases.ToArray())
        {
            var canonOld = Canon(kv.Key);
            var canonNew = Canon(kv.Value);
            if (!Ci.Equals(canonOld, canonNew))
                copy[kv.Key] = canonNew; // keep original key so clients can map any seen old name
        }
        return Task.FromResult(copy);
    }

    // ----- Status -----

    // Client sets their own status: "online" | "away" | "busy"
public async Task SetStatus(string status) // "online" | "away" | "busy" | "offline"
{
    var me = GetMe();
    if (string.IsNullOrWhiteSpace(me)) return;

    // Validate
    status = (status ?? "").ToLowerInvariant();
    if (status is not ("online" or "away" or "busy" or "offline"))
        status = "online";

    _statusByName[me] = status;

    // (optional) A system line in Lobby:
    await Clients.Group(LobbyId).SendAsync("LobbySystemMessage", $"{me} is now {status}.");

    await BroadcastStatusesAsync();
}

    // Snapshot of statuses for all known users
public Task<Dictionary<string,string>> GetStatuses()
{
    var snapshot = _statusByName.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
    return Task.FromResult(snapshot);
}

    private static string GetEffectiveStatus(string displayName)
    {
        var hasConn = _displayNameToConnections.TryGetValue(displayName, out var set) && set.Count > 0;
        if (!hasConn) return "offline";

        return _statusByDisplayName.TryGetValue(displayName, out var s) && !string.IsNullOrWhiteSpace(s)
            ? s
            : "online";
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
        var other = Canon(otherDisplayName);
        var chatId = MakeDmId(me, other);

        var set = _chatMembers.GetOrAdd(chatId, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        set.Add(me);
        set.Add(other);

        // Track participants for DM notify (canonical)
        _dmParticipants[chatId] = (me, other);

        // Sender joins the group
        await Groups.AddToGroupAsync(Context.ConnectionId, chatId);

        // Notify recipient that a chat exists (so it appears in their list)
        var recipientConns = ConnectionsFor(other).ToList();
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
        var nameCanon = Canon(displayName);

        // 1) Deliver to all connections currently joined to this chat group
        await Clients.Group(chatId).SendAsync("ReceiveChatMessage", chatId, nameCanon, message);

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
                await Clients.Client(cid).SendAsync("DmNotify", chatId, nameCanon, message);
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
        => _names.TryGetValue(Context.ConnectionId, out var me) && !string.IsNullOrWhiteSpace(me) ? Canon(me) : "Unknown";

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
        => _names.Where(kvp => Ci.Equals(kvp.Value, displayName))
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

    // ----- Alias canonicalization helpers -----

    // Follow alias chain to latest name, with path compression
    private static string Canon(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return name;

        string cur = name;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { cur };

        while (_aliases.TryGetValue(cur, out var next)
               && !string.Equals(next, cur, StringComparison.OrdinalIgnoreCase)
               && !seen.Contains(next))
        {
            seen.Add(next);
            cur = next;
        }

        // path compression
        foreach (var s in seen)
            _aliases[s] = cur;

        return cur;
    }
}
