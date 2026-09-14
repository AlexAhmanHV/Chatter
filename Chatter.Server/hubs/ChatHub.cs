// Hubs/ChatHub.cs
/*
File: ChatHub.cs

What this does:
- Purpose: SignalR hub that powers real-time chat. It tracks connections, display names, presence
  (online/away/busy), membership in chats (Lobby + DMs), and delivers messages, typing indicators,
  rosters, and status snapshots.
- How: Identity is derived from the caller's authenticated Supabase JWT (the "sub" claim), never from
  a client-supplied string. Display names are just a mutable label attached to that stable user ID, so
  renaming never changes who you are or what you have access to, and nobody can "become" someone else
  by typing their name. Chat membership and DM routing are keyed by user ID and checked on every join
  and send, so a DM's chat ID being guessed or shared doesn't grant access.
*/

using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Chatter.Server.Data;
using Chatter.Shared.Models;

namespace Chatter.Server.Hubs;

[Authorize]
public class ChatHub : Hub
{
    private readonly IDbContextFactory<ChatDbContext> _dbFactory;

    public ChatHub(IDbContextFactory<ChatDbContext> dbFactory) => _dbFactory = dbFactory;

    private const string LobbyId = "Lobby";
    private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;
    private const int MaxDisplayNameLength = 40;
    private const int MaxMessageLength = 2000;

    // ===== Connection & identity indices (all keyed by the stable Supabase user ID) =====
    // connectionId -> userId
    private static readonly ConcurrentDictionary<string, string> _connToUserId = new();

    // userId -> set of connectionIds (tabs/devices)
    private static readonly ConcurrentDictionary<string, HashSet<string>> _userIdToConns = new();

    // userId -> current display name
    private static readonly ConcurrentDictionary<string, string> _displayNameByUserId = new();

    // display name (lower) -> userId. Best-effort directory used to resolve "start a DM with X".
    // Last write wins, same as a real handle/username system without uniqueness enforcement.
    private static readonly ConcurrentDictionary<string, string> _userIdByDisplayName =
        new(Ci);

    // userId -> previous display names (cosmetic only, used to render "Alice (aka Bob)")
    private static readonly ConcurrentDictionary<string, List<string>> _akaHistoryByUserId = new();

    // ===== Chat membership indices =====
    // chatId -> set of userIds who are members of that chat
    private static readonly ConcurrentDictionary<string, HashSet<string>> _chatMembers = new();

    // chatId -> set of connectionIds that have joined the SignalR group for that chat
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _chatMembersByConn = new();

    // dm chatId -> (userId1, userId2)
    private static readonly ConcurrentDictionary<string, (string User1, string User2)> _dmParticipants = new();

    // ===== Presence =====
    // userId -> explicit status ("online" | "away" | "busy" | "offline"). No connections -> effective offline.
    private static readonly ConcurrentDictionary<string, string> _statusByUserId = new();

    // ===== Rate limiting =====
    // (connectionId, bucket name) -> (count so far this window, window start). Per-connection
    // fixed-window counters. ASP.NET Core's built-in rate limiting middleware only applies to
    // HTTP requests, not individual SignalR hub method invocations, so this is done by hand here.
    private static readonly ConcurrentDictionary<(string ConnId, string Bucket), (int Count, long WindowStartTicks)> _rateBuckets = new();

    // -------------------------------------------------------
    // Connection lifecycle
    // -------------------------------------------------------
    public override async Task OnConnectedAsync()
    {
        var userId = GetUserId();
        if (string.IsNullOrWhiteSpace(userId))
        {
            // [Authorize] should already have rejected this connection; this is a defensive backstop.
            Context.Abort();
            return;
        }

        _connToUserId[Context.ConnectionId] = userId;
        _userIdToConns.AddOrUpdate(userId,
            _ => new HashSet<string> { Context.ConnectionId },
            (_, set) => { lock (set) set.Add(Context.ConnectionId); return set; });

        if (!_displayNameByUserId.ContainsKey(userId))
        {
            var defaultName = DeriveDefaultDisplayName();
            ApplyDisplayName(userId, defaultName);
            await PersistDisplayNameAsync(userId, defaultName);
        }

        _statusByUserId.TryAdd(userId, "online");

        EnsureLobbyMembership(userId);
        await Groups.AddToGroupAsync(Context.ConnectionId, LobbyId);

        await base.OnConnectedAsync();

        await BroadcastRosterAsync();
        await SendChatsToCallerAsync(userId);
        await Clients.Caller.SendAsync("StatusChanged", DisplayNameOf(userId), GetEffectiveStatus(userId));
        await Clients.Caller.SendAsync("NameAliases", GetNameAliasesSnapshot());
        await BroadcastStatusesAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? ex)
    {
        if (_connToUserId.TryRemove(Context.ConnectionId, out var userId))
        {
            if (_userIdToConns.TryGetValue(userId, out var set))
                lock (set) set.Remove(Context.ConnectionId);

            foreach (var kvp in _chatMembersByConn)
                kvp.Value.TryRemove(Context.ConnectionId, out _);

            foreach (var key in _rateBuckets.Keys.Where(k => k.ConnId == Context.ConnectionId).ToList())
                _rateBuckets.TryRemove(key, out _);

            await BroadcastRosterAsync();

            var stillConnected = _userIdToConns.TryGetValue(userId, out var remaining) && remaining.Count > 0;
            if (!stillConnected)
                await Clients.All.SendAsync("StatusChanged", DisplayNameOf(userId), "offline");

            await BroadcastStatusesAsync();
        }

        await base.OnDisconnectedAsync(ex);
    }

    // -------------------------------------------------------
    // Identity
    // -------------------------------------------------------
    public async Task SetDisplayName(string displayName)
    {
        var userId = RequireUserId();
        var newName = SanitizeName(displayName, userId);
        var old = _displayNameByUserId.GetValueOrDefault(userId);

        if (!string.IsNullOrWhiteSpace(old) && !Ci.Equals(old, newName))
            RecordRename(userId, old);

        ApplyDisplayName(userId, newName);
        await PersistDisplayNameAsync(userId, newName);
        EnsureLobbyMembership(userId);
        await Groups.AddToGroupAsync(Context.ConnectionId, LobbyId);

        await BroadcastRosterAsync();
        await SendChatsToCallerAsync(userId);
        await Clients.Caller.SendAsync("StatusChanged", newName, GetEffectiveStatus(userId));
        await Clients.Caller.SendAsync("NameAliases", GetNameAliasesSnapshot());
        await BroadcastStatusesAsync();
    }

    public async Task ChangeDisplayName(string newDisplayName)
    {
        var userId = RequireUserId();
        var newName = SanitizeName(newDisplayName, userId);
        var old = _displayNameByUserId.GetValueOrDefault(userId) ?? "Unknown";

        if (Ci.Equals(old, newName))
        {
            await Clients.Caller.SendAsync("NameAliases", GetNameAliasesSnapshot());
            return;
        }

        RecordRename(userId, old);
        ApplyDisplayName(userId, newName);
        await PersistDisplayNameAsync(userId, newName);

        await Clients.Group(LobbyId).SendAsync("DisplayNameChanged", old, newName);

        await BroadcastRosterAsync();
        await Clients.All.SendAsync("StatusChanged", newName, GetEffectiveStatus(userId));
        await SendChatsToCallerAsync(userId);
        await Clients.All.SendAsync("NameAliases", GetNameAliasesSnapshot());
        await BroadcastStatusesAsync();
    }

    public Task<Dictionary<string, string>> GetNameAliases() => Task.FromResult(GetNameAliasesSnapshot());

    // -------------------------------------------------------
    // Presence
    // -------------------------------------------------------
    public async Task SetStatus(string status)
    {
        var userId = RequireUserId();
        status = NormalizeStatus(status);
        _statusByUserId[userId] = status;

        var name = DisplayNameOf(userId);
        await Clients.Group(LobbyId).SendAsync("LobbySystemMessage", $"{name} is now {status}.");
        await Clients.All.SendAsync("StatusChanged", name, GetEffectiveStatus(userId));
        await BroadcastStatusesAsync();
    }

    public Task<Dictionary<string, string>> GetStatuses() => Task.FromResult(ComputeStatusesSnapshot());

    private Task BroadcastStatusesAsync() => Clients.All.SendAsync("Statuses", ComputeStatusesSnapshot());

    // Not a plain ToDictionary: display names aren't guaranteed unique (two users can pick the
    // same name), and ToDictionary throws on a duplicate key. Last write wins here instead.
    private static Dictionary<string, string> ComputeStatusesSnapshot()
    {
        var result = new Dictionary<string, string>(Ci);
        foreach (var kv in _displayNameByUserId)
            result[kv.Value] = GetEffectiveStatus(kv.Key);
        return result;
    }

    private static string GetEffectiveStatus(string userId)
    {
        var hasConn = _userIdToConns.TryGetValue(userId, out var set) && set.Count > 0;
        if (!hasConn) return "offline";
        return _statusByUserId.TryGetValue(userId, out var s) && !string.IsNullOrWhiteSpace(s) ? s : "online";
    }

    // -------------------------------------------------------
    // Chats API (Lobby + DMs)
    // -------------------------------------------------------
    public Task<List<ChatSummary>> GetMyChats()
    {
        var userId = RequireUserId();
        return Task.FromResult(BuildChatSummaries(userId));
    }

    public async Task<string> CreateDm(string otherDisplayName)
    {
        var me = RequireUserId();
        EnforceRateLimit("createDm", maxPerWindow: 5, window: TimeSpan.FromSeconds(30));

        if (!_userIdByDisplayName.TryGetValue((otherDisplayName ?? string.Empty).Trim(), out var otherId))
            throw new HubException($"Could not find a user named '{otherDisplayName}'.");

        if (Ci.Equals(otherId, me))
            throw new HubException("You cannot start a DM with yourself.");

        var chatId = MakeDmId(me, otherId);

        var set = _chatMembers.GetOrAdd(chatId, _ => new HashSet<string>());
        lock (set) { set.Add(me); set.Add(otherId); }

        _dmParticipants[chatId] = (me, otherId);

        var connMap = _chatMembersByConn.GetOrAdd(chatId, _ => new ConcurrentDictionary<string, byte>());
        await Groups.AddToGroupAsync(Context.ConnectionId, chatId);
        connMap[Context.ConnectionId] = 1;

        // Join all of the recipient's live connections so the very first message lands.
        var recipientConns = ConnectionsFor(otherId).ToList();
        foreach (var cid in recipientConns)
        {
            await Groups.AddToGroupAsync(cid, chatId);
            connMap[cid] = 1;
        }

        // Tell each side about the chat, labeled from *their* point of view.
        if (recipientConns.Count > 0)
            await Clients.Clients(recipientConns).SendAsync("AddedChat", chatId, DisplayNameOf(me));

        await Clients.Caller.SendAsync("AddedChat", chatId, DisplayNameOf(otherId));
        await Clients.All.SendAsync("ChatsUpdated", new[] { chatId });

        return chatId;
    }

    // Convenience: create a DM and immediately send the first message.
    public async Task<string> SendDmFirst(string otherDisplayName, string message)
    {
        var chatId = await CreateDm(otherDisplayName);
        await SendToChat(chatId, message);
        return chatId;
    }

    public async Task JoinChat(string chatId)
    {
        var me = RequireUserId();
        RequireMembership(chatId, me);

        var set = _chatMembersByConn.GetOrAdd(chatId, _ => new ConcurrentDictionary<string, byte>());
        set[Context.ConnectionId] = 1;
        await Groups.AddToGroupAsync(Context.ConnectionId, chatId);
    }

    public Task LeaveChat(string chatId)
    {
        var me = RequireUserId();
        _ = Groups.RemoveFromGroupAsync(Context.ConnectionId, chatId);

        if (_chatMembers.TryGetValue(chatId, out var set)) lock (set) set.Remove(me);
        if (_chatMembersByConn.TryGetValue(chatId, out var conns)) conns.TryRemove(Context.ConnectionId, out _);

        return Task.CompletedTask;
    }

    public async Task SendToChat(string chatId, string message)
    {
        var me = RequireUserId();
        RequireMembership(chatId, me);
        EnforceRateLimit("sendToChat", maxPerWindow: 10, window: TimeSpan.FromSeconds(10));

        message = (message ?? string.Empty).Trim();
        if (message.Length == 0) return;
        if (message.Length > MaxMessageLength) message = message[..MaxMessageLength];

        var name = DisplayNameOf(me);

        await PersistMessageAsync(chatId, me, name, message);

        // Deliver to everyone currently joined to this chat's SignalR group.
        await Clients.Group(chatId).SendAsync("ReceiveChatMessage", chatId, name, message);

        // If it's a DM, also notify participants who haven't joined the group yet (e.g. another tab/device).
        if (_dmParticipants.TryGetValue(chatId, out var pair))
        {
            var groupMembers = _chatMembersByConn.TryGetValue(chatId, out var members)
                ? members.Keys
                : Enumerable.Empty<string>();

            var allConnIds = ResolveLiveConnectionsFor(new[] { pair.User1, pair.User2 });
            var senderConnId = Context.ConnectionId;
            var notInGroup = allConnIds.Where(cid => cid != senderConnId && !groupMembers.Contains(cid));

            foreach (var cid in notInGroup)
                await Clients.Client(cid).SendAsync("DmNotify", chatId, name, message);
        }
    }

    // Backfills a chat's message history for a client that just opened it (e.g. after
    // reconnecting, or opening the app fresh and everything else is in-memory-only).
    public async Task<List<ChatMessageDto>> GetChatHistory(string chatId, int take = 50)
    {
        var me = RequireUserId();
        RequireMembership(chatId, me);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var rows = await db.Messages
            .Where(m => m.ChatId == chatId)
            .OrderByDescending(m => m.SentAtUtc)
            .Take(Math.Clamp(take, 1, 200))
            .ToListAsync();

        rows.Reverse();
        return rows.Select(m => new ChatMessageDto(m.SenderDisplayName, m.Body, m.SentAtUtc)).ToList();
    }

    // -------------------------------------------------------
    // Typing indicator
    // -------------------------------------------------------
    public async Task Typing(string channelId, bool isTyping)
    {
        var me = RequireUserId();

        // Low-stakes and high-frequency: silently drop excess calls instead of throwing.
        if (!TryConsumeRateLimit("typing", maxPerWindow: 10, window: TimeSpan.FromSeconds(2)))
            return;

        await Clients.OthersInGroup(channelId).SendAsync("Typing", channelId, DisplayNameOf(me), isTyping);
    }

    // -------------------------------------------------------
    // Legacy global broadcast (kept for compatibility; sender is always server-resolved)
    // -------------------------------------------------------
    public Task SendMessage(string message) =>
        Clients.All.SendAsync("ReceiveMessage", DisplayNameOf(RequireUserId()), message);

    // -------------------------------------------------------
    // Roster & snapshots
    // -------------------------------------------------------
    public Task<IReadOnlyList<string>> GetOnlineUsers()
    {
        var list = _userIdToConns
            .Where(kv => kv.Value.Count > 0)
            .Select(kv => DisplayNameOf(kv.Key))
            .OrderBy(n => n, Ci)
            .ToList()
            .AsReadOnly();

        return Task.FromResult((IReadOnlyList<string>)list);
    }

    private Task BroadcastRosterAsync()
    {
        var online = _userIdToConns
            .Where(kv => kv.Value.Count > 0)
            .Select(kv => DisplayNameOf(kv.Key))
            .OrderBy(n => n, Ci)
            .ToList();

        return Clients.All.SendAsync("OnlineUsers", online);
    }

    private Task SendChatsToCallerAsync(string userId) =>
        Clients.Caller.SendAsync("ChatsForMe", BuildChatSummaries(userId));

    private List<ChatSummary> BuildChatSummaries(string userId)
    {
        var ids = _chatMembers
            .Where(kv => kv.Value.Contains(userId))
            .Select(kv => kv.Key)
            .Concat(new[] { LobbyId })
            .Distinct(Ci)
            .ToList();

        var list = ids.Select(id => new ChatSummary(id, ComputeChatLabelForUser(id, userId))).ToList();

        var lobby = list.FirstOrDefault(c => Ci.Equals(c.Id, LobbyId));
        if (lobby is not null && list.IndexOf(lobby) != 0)
        {
            list.Remove(lobby);
            list.Insert(0, lobby);
        }

        return list;
    }

    private string ComputeChatLabelForUser(string chatId, string callerUserId)
    {
        if (Ci.Equals(chatId, LobbyId)) return "Lobby";

        if (_dmParticipants.TryGetValue(chatId, out var pair))
        {
            var otherId = Ci.Equals(pair.User1, callerUserId) ? pair.User2 : pair.User1;
            return DisplayNameOf(otherId);
        }

        return chatId;
    }

    // -------------------------------------------------------
    // Persistence
    // -------------------------------------------------------

    // Called once at app startup (see Program.cs) to warm the in-memory display-name
    // cache from what was saved in previous runs, before any client connects.
    public static void PreloadDisplayNames(IEnumerable<UserProfileEntity> profiles)
    {
        foreach (var p in profiles)
            ApplyDisplayName(p.UserId, p.DisplayName);
    }

    private async Task PersistDisplayNameAsync(string userId, string displayName)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var existing = await db.UserProfiles.FindAsync(userId);
            if (existing is null)
            {
                db.UserProfiles.Add(new UserProfileEntity
                {
                    UserId = userId,
                    DisplayName = displayName,
                    UpdatedAtUtc = DateTime.UtcNow
                });
            }
            else
            {
                existing.DisplayName = displayName;
                existing.UpdatedAtUtc = DateTime.UtcNow;
            }
            await db.SaveChangesAsync();
        }
        catch
        {
            // Best-effort: in-memory state (already applied by the caller) still
            // works for the rest of this run even if the write fails.
        }
    }

    private async Task PersistMessageAsync(string chatId, string senderUserId, string senderDisplayName, string body)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            db.Messages.Add(new ChatMessageEntity
            {
                ChatId = chatId,
                SenderUserId = senderUserId,
                SenderDisplayName = senderDisplayName,
                Body = body,
                SentAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }
        catch
        {
            // Best-effort: the message still gets delivered live even if it fails to persist.
        }
    }

    // -------------------------------------------------------
    // Rate limiting
    // -------------------------------------------------------
    private bool TryConsumeRateLimit(string bucket, int maxPerWindow, TimeSpan window)
    {
        var key = (Context.ConnectionId, bucket);
        var now = DateTime.UtcNow.Ticks;
        var windowTicks = window.Ticks;

        var updated = _rateBuckets.AddOrUpdate(key,
            _ => (1, now),
            (_, old) => now - old.WindowStartTicks > windowTicks ? (1, now) : (old.Count + 1, old.WindowStartTicks));

        return updated.Count <= maxPerWindow;
    }

    private void EnforceRateLimit(string bucket, int maxPerWindow, TimeSpan window)
    {
        if (!TryConsumeRateLimit(bucket, maxPerWindow, window))
            throw new HubException("You're doing that too quickly. Please slow down.");
    }

    // -------------------------------------------------------
    // Helpers
    // -------------------------------------------------------
    private void RequireMembership(string chatId, string userId)
    {
        if (Ci.Equals(chatId, LobbyId)) return;

        if (!_chatMembers.TryGetValue(chatId, out var members) || !members.Contains(userId))
            throw new HubException("You are not a member of this chat.");
    }

    private static string MakeDmId(string a, string b)
    {
        var pair = new[] { a, b }.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        return $"dm:{pair[0]}|{pair[1]}";
    }

    private static string DisplayNameOf(string userId) =>
        _displayNameByUserId.TryGetValue(userId, out var n) && !string.IsNullOrWhiteSpace(n) ? n : "Unknown";

    private void EnsureLobbyMembership(string userId) =>
        _chatMembers.AddOrUpdate(LobbyId,
            _ => new HashSet<string> { userId },
            (_, set) => { lock (set) set.Add(userId); return set; });

    private static void ApplyDisplayName(string userId, string newName)
    {
        _displayNameByUserId[userId] = newName;
        _userIdByDisplayName[newName] = userId;
        _statusByUserId.TryAdd(userId, "online");
    }

    private static void RecordRename(string userId, string oldName)
    {
        var hist = _akaHistoryByUserId.GetOrAdd(userId, _ => new List<string>());
        lock (hist)
        {
            if (!hist.Contains(oldName, Ci)) hist.Add(oldName);
        }
    }

    private static Dictionary<string, string> GetNameAliasesSnapshot()
    {
        var result = new Dictionary<string, string>(Ci);
        foreach (var kv in _akaHistoryByUserId)
        {
            if (!_displayNameByUserId.TryGetValue(kv.Key, out var current)) continue;
            List<string> historySnapshot;
            lock (kv.Value) historySnapshot = kv.Value.ToList();

            foreach (var old in historySnapshot)
                if (!Ci.Equals(old, current))
                    result[old] = current;
        }
        return result;
    }

    private string SanitizeName(string? raw, string userId)
    {
        var trimmed = (raw ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return _displayNameByUserId.TryGetValue(userId, out var existing) && !string.IsNullOrWhiteSpace(existing)
                ? existing
                : DeriveDefaultDisplayName();
        }
        return trimmed.Length > MaxDisplayNameLength ? trimmed[..MaxDisplayNameLength] : trimmed;
    }

    private static string NormalizeStatus(string? status)
    {
        var s = (status ?? string.Empty).ToLowerInvariant();
        return s is "online" or "away" or "busy" or "offline" ? s : "online";
    }

    private static IEnumerable<string> ConnectionsFor(string userId) =>
        _userIdToConns.TryGetValue(userId, out var set) ? set.ToArray() : Array.Empty<string>();

    private static IEnumerable<string> ResolveLiveConnectionsFor(IEnumerable<string> userIds)
    {
        foreach (var uid in userIds)
            foreach (var cid in ConnectionsFor(uid))
                yield return cid;
    }

    private string RequireUserId() => GetUserId() ?? throw new HubException("Unauthorized.");

    // The Supabase JWT's "sub" claim is the stable, non-spoofable identity of the caller.
    private string? GetUserId() =>
        Context.User?.FindFirst("sub")?.Value
        ?? Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    private string DeriveDefaultDisplayName()
    {
        var meta = Context.User?.FindFirst("user_metadata")?.Value;
        if (!string.IsNullOrWhiteSpace(meta))
        {
            try
            {
                using var doc = JsonDocument.Parse(meta);
                if (doc.RootElement.TryGetProperty("display_name", out var dn) && dn.ValueKind == JsonValueKind.String)
                {
                    var val = dn.GetString();
                    if (!string.IsNullOrWhiteSpace(val)) return val!.Trim();
                }
            }
            catch (JsonException)
            {
                // Malformed/unexpected metadata shape: fall through to the next default.
            }
        }

        var email = Context.User?.FindFirst("email")?.Value;
        if (!string.IsNullOrWhiteSpace(email))
        {
            var at = email.IndexOf('@');
            return at > 0 ? email[..at] : email;
        }

        var userId = GetUserId() ?? Guid.NewGuid().ToString("N");
        return "User-" + userId[..Math.Min(8, userId.Length)];
    }
}
