// Hubs/ChatHub.cs
/*
File: ChatHub.cs

What this does:
- Purpose: SignalR hub that powers real-time chat. It tracks connections, display names, presence
  (online/away/busy), membership in chats (Lobby + DMs), and delivers messages, typing indicators,
  rosters, and status snapshots.
- How: Identity is derived from the caller's authenticated JWT (issued by this app's own /auth/login
  and /auth/register endpoints - see Auth/JwtIssuer), never from a client-supplied string. The "sub"
  claim is the stable user ID. Display names are just a mutable label attached to that ID, so
  renaming never changes who you are or what you have access to, and nobody can "become" someone else
  by typing their name. Chat membership and DM routing are keyed by user ID and checked on every join
  and send, so a DM's chat ID being guessed or shared doesn't grant access.
*/

using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Chatter.Server.Auth;
using Chatter.Server.Data;
using Chatter.Server.Services;
using Chatter.Shared.Models;

namespace Chatter.Server.Hubs;

[Authorize]
public class ChatHub : Hub
{
    private readonly IDbContextFactory<ChatDbContext> _dbFactory;

    private readonly LinkPreviewFetcher _linkPreviewFetcher;
    private readonly IConfiguration _config;

    // How long a signed avatar link stays valid (see AvatarUrlSigner/GetAvatarUrls) before a
    // client needs to re-resolve it. Generous on purpose: an already-loaded image stays displayed
    // regardless (MAUI doesn't re-fetch a bitmap it already rendered), this just bounds how long a
    // copied/leaked link keeps working.
    private static readonly TimeSpan AvatarLinkLifetime = TimeSpan.FromHours(1);

    public ChatHub(IDbContextFactory<ChatDbContext> dbFactory, LinkPreviewFetcher linkPreviewFetcher, IConfiguration config)
    {
        _dbFactory = dbFactory;
        _linkPreviewFetcher = linkPreviewFetcher;
        _config = config;
    }

    private const string LobbyId = "Lobby";
    private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;
    private const int MaxDisplayNameLength = 40;
    private const int MaxMessageLength = 2000;
    private const int MaxAttachmentBytes = 5 * 1024 * 1024; // 5 MB
    private const int MaxAvatarBytes = 512 * 1024; // 512 KB

    // Images only, deliberately - this is "share a photo in chat", not general file transfer.
    private static readonly HashSet<string> AllowedImageContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/gif", "image/webp"
    };

    // A voice note capped at ~2 minutes of compressed audio, not a general audio-file uploader.
    private const int MaxVoiceMessageBytes = 2 * 1024 * 1024; // 2 MB
    private static readonly HashSet<string> AllowedAudioContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "audio/mp4", "audio/aac", "audio/wav", "audio/webm", "audio/ogg"
    };

    // A short video clip, not a general video-file uploader - capped well above an image but
    // still small enough not to turn the SQLite file into a video store.
    private const int MaxVideoBytes = 20 * 1024 * 1024; // 20 MB
    private static readonly HashSet<string> AllowedVideoContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "video/mp4", "video/quicktime", "video/webm"
    };

    // Generic documents (PDF, Office files, plain text, zip, ...) - a content-type allowlist
    // isn't practical here the way it is for images/audio/video (there are too many legitimate
    // document MIME types, and clients don't always report them accurately), so this instead
    // denylists file extensions that would make this "share a document" feature into a way to
    // pass around executables. Same size cap as video.
    private const int MaxDocumentBytes = MaxVideoBytes;
    private static readonly HashSet<string> DisallowedDocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".bat", ".cmd", ".com", ".msi", ".scr", ".ps1", ".vbs", ".js", ".jar", ".app", ".sh", ".apk"
    };

    // ===== Connection & identity indices (all keyed by the stable Identity user ID) =====
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

    // group chatId -> group name. Membership for groups lives in _chatMembers like everything
    // else; this only holds the label, since (unlike a DM) it can't be derived from the id.
    private static readonly ConcurrentDictionary<string, string> _groupChatNames = new();

    // group chatId -> the userId that originally created it. Kept only as metadata/history
    // ("created by") - it no longer implies admin rights on its own, see _groupChatAdmins.
    private static readonly ConcurrentDictionary<string, string> _groupChatCreator = new();

    // group chatId -> set of userIds who are admins of that group. The creator starts as the
    // sole admin; PromoteGroupAdmin/DemoteGroupAdmin (admin-only) can add or remove others. If
    // the last admin leaves or is removed while other members remain, the earliest-joined
    // remaining member is auto-promoted so the group is never left without one - see
    // SucceedAdminIfNoneRemain.
    private static readonly ConcurrentDictionary<string, HashSet<string>> _groupChatAdmins = new();

    // ===== Blocking & muting =====
    // blocker userId -> set of userIds they've blocked. Enforced both ways (see
    // IsBlockedEitherWay) when starting or posting to a DM.
    private static readonly ConcurrentDictionary<string, HashSet<string>> _blockedByUser = new();

    // (userId, chatId) muted by that user - messages still arrive, this only suppresses their
    // own unread badge/notification. The other party is never told.
    private static readonly ConcurrentDictionary<(string UserId, string ChatId), byte> _mutedChats = new();

    // (userId, chatId) pinned by that user - a pure per-user ordering preference (see
    // BuildChatSummariesAsync), doesn't affect anyone else or delivery.
    private static readonly ConcurrentDictionary<(string UserId, string ChatId), byte> _pinnedChats = new();

    // ===== Presence =====
    // userId -> explicit status ("online" | "away" | "busy" | "offline"). No connections -> effective offline.
    private static readonly ConcurrentDictionary<string, string> _statusByUserId = new();

    // ===== Rate limiting =====
    // (connectionId, bucket name) -> timestamps of recent calls, oldest first. A sliding window
    // log rather than a fixed window: a fixed window lets a caller burst up to 2x the limit
    // right at the window boundary (all of window N's tail plus all of window N+1's head).
    // ASP.NET Core's built-in rate limiting middleware only applies to HTTP requests, not
    // individual SignalR hub method invocations, so this is done by hand here.
    private static readonly ConcurrentDictionary<(string ConnId, string Bucket), ConcurrentQueue<long>> _rateBuckets = new();

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
            {
                await Clients.All.SendAsync("StatusChanged", DisplayNameOf(userId), "offline");
                await PersistLastSeenAsync(userId);
            }

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

    // Saves a profile picture. Served back publicly and unauthenticated at GET /avatars/{userId}
    // (see Program.cs) - simplest way for a plain <Image Source="{url}"/> to work in the MAUI
    // client without wiring an Authorization header through image loading. See the README's
    // known-simplifications list for the tradeoff.
    public async Task UpdateAvatar(byte[] data, string contentType)
    {
        var me = RequireUserId();

        if (data is null || data.Length == 0)
            throw new HubException("Avatar image is empty.");
        if (data.Length > MaxAvatarBytes)
            throw new HubException($"Avatar too large (max {MaxAvatarBytes / 1024} KB).");
        if (string.IsNullOrWhiteSpace(contentType) || !AllowedImageContentTypes.Contains(contentType))
            throw new HubException("Only PNG, JPEG, GIF, or WebP images are supported.");

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var user = await db.Users.FindAsync(me);
            if (user is null) throw new HubException("Account not found.");

            user.AvatarData = data;
            user.AvatarContentType = contentType;
            await db.SaveChangesAsync();
        }
        catch (HubException) { throw; }
        catch (Exception ex)
        {
            throw new HubException("Failed to save the avatar. Please try again.", ex);
        }

        // "version" lets clients cache-bust the URL (same path, new image) instead of caching
        // the old picture forever - see the client's AvatarChanged handler.
        await Clients.All.SendAsync("AvatarChanged", DisplayNameOf(me), DateTime.UtcNow.Ticks);
    }

    // Resolves avatar URLs for a batch of display names at once (the client already knows names,
    // never raw user ids, so this is the one place that translates - the URL embeds the id, the
    // caller never sees it directly). Each URL carries a short-lived signature (see
    // AvatarUrlSigner) rather than being a bare, forever-valid "/avatars/{id}" - since this method
    // requires [Authorize] like the rest of the hub, only an already-authenticated caller can ever
    // mint one, and it stops working on its own after AvatarLinkLifetime.
    public Task<Dictionary<string, string>> GetAvatarUrls(List<string> displayNames)
    {
        var signingKey = _config["Jwt:SigningKey"]!;
        var exp = DateTimeOffset.UtcNow.Add(AvatarLinkLifetime).ToUnixTimeSeconds();

        var result = new Dictionary<string, string>(Ci);
        foreach (var raw in displayNames ?? new List<string>())
        {
            var name = (raw ?? string.Empty).Trim();
            if (name.Length == 0) continue;
            if (_userIdByDisplayName.TryGetValue(name, out var userId))
            {
                var sig = AvatarUrlSigner.Sign(userId, exp, signingKey);
                result[name] = $"/avatars/{userId}?exp={exp}&sig={sig}";
            }
        }
        return Task.FromResult(result);
    }

    // Batch-resolves "last seen" timestamps for offline users (by display name, same reasoning
    // as GetAvatarUrls: the client never learns raw user ids). A name missing from the result is
    // either currently online, or has never disconnected while this server has tracked it.
    public async Task<Dictionary<string, DateTime>> GetLastSeen(List<string> displayNames)
    {
        var ids = new Dictionary<string, string>(Ci);
        foreach (var raw in displayNames ?? new List<string>())
        {
            var name = (raw ?? string.Empty).Trim();
            if (name.Length > 0 && _userIdByDisplayName.TryGetValue(name, out var userId))
                ids[name] = userId;
        }

        var result = new Dictionary<string, DateTime>(Ci);
        if (ids.Count == 0) return result;

        await using var db = await _dbFactory.CreateDbContextAsync();
        var userIds = ids.Values.Distinct().ToList();
        var rows = await db.Users
            .Where(u => userIds.Contains(u.Id) && u.LastSeenUtc != null)
            .Select(u => new { u.Id, u.LastSeenUtc })
            .ToListAsync();
        var lastSeenByUserId = rows.ToDictionary(r => r.Id, r => r.LastSeenUtc!.Value);

        foreach (var (name, userId) in ids)
            if (lastSeenByUserId.TryGetValue(userId, out var lastSeen))
                result[name] = lastSeen;

        return result;
    }

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
        return BuildChatSummariesAsync(userId);
    }

    public async Task<string> CreateDm(string otherDisplayName)
    {
        var me = RequireUserId();
        EnforceRateLimit("createDm", maxPerWindow: 5, window: TimeSpan.FromSeconds(30));

        if (!_userIdByDisplayName.TryGetValue((otherDisplayName ?? string.Empty).Trim(), out var otherId))
            throw new HubException($"Could not find a user named '{otherDisplayName}'.");

        if (Ci.Equals(otherId, me))
            throw new HubException("You cannot start a DM with yourself.");

        if (IsBlockedEitherWay(me, otherId))
            throw new HubException("You can't start a chat with this user.");

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

    // Named, multi-member chat. Unlike a DM, membership can't be derived from the chat id, so
    // it's tracked the same way as everything else in _chatMembers and persisted separately.
    public async Task<string> CreateGroupChat(string name, List<string> memberDisplayNames)
    {
        var me = RequireUserId();
        EnforceRateLimit("createGroup", maxPerWindow: 5, window: TimeSpan.FromSeconds(30));

        name = (name ?? string.Empty).Trim();
        if (name.Length == 0) throw new HubException("Group name can't be empty.");
        if (name.Length > MaxDisplayNameLength) name = name[..MaxDisplayNameLength];

        var memberIds = new HashSet<string> { me };
        foreach (var displayName in memberDisplayNames ?? new List<string>())
        {
            var trimmed = (displayName ?? string.Empty).Trim();
            if (trimmed.Length == 0) continue;

            if (!_userIdByDisplayName.TryGetValue(trimmed, out var userId))
                throw new HubException($"Could not find a user named '{trimmed}'.");

            memberIds.Add(userId);
        }

        if (memberIds.Count < 2)
            throw new HubException("A group needs at least one other member.");

        var chatId = "group:" + Guid.NewGuid().ToString("N");

        _chatMembers[chatId] = new HashSet<string>(memberIds);
        _groupChatNames[chatId] = name;
        _groupChatCreator[chatId] = me;
        _groupChatAdmins[chatId] = new HashSet<string> { me };

        await PersistGroupChatAsync(chatId, name, me, memberIds);

        var connMap = _chatMembersByConn.GetOrAdd(chatId, _ => new ConcurrentDictionary<string, byte>());
        foreach (var uid in memberIds)
        {
            var conns = ConnectionsFor(uid).ToList();
            foreach (var cid in conns)
            {
                await Groups.AddToGroupAsync(cid, chatId);
                connMap[cid] = 1;
            }

            // Notify every member (including the caller) so the chat shows up immediately.
            if (conns.Count > 0)
                await Clients.Clients(conns).SendAsync("AddedChat", chatId, name);
        }

        await Clients.All.SendAsync("ChatsUpdated", new[] { chatId });

        return chatId;
    }

    public Task<List<string>> GetGroupMembers(string chatId)
    {
        var me = RequireUserId();
        RequireMembership(chatId, me);

        var names = _chatMembers.TryGetValue(chatId, out var members)
            ? members.Select(DisplayNameOf).OrderBy(n => n, Ci).ToList()
            : new List<string>();
        return Task.FromResult(names);
    }

    // Whoever created a group is its only admin - no delegated admins, no ownership transfer.
    // If the creator leaves the group (LeaveChat), they lose admin rights too: RequireGroupAdmin
    // also checks current membership, not just "were they the original creator".
    public async Task AddGroupMember(string chatId, string displayName)
    {
        var me = RequireUserId();
        RequireGroupAdmin(chatId, me);

        if (!_userIdByDisplayName.TryGetValue((displayName ?? string.Empty).Trim(), out var targetId))
            throw new HubException($"Could not find a user named '{displayName}'.");

        var set = _chatMembers.GetOrAdd(chatId, _ => new HashSet<string>());
        bool added;
        lock (set) added = set.Add(targetId);
        if (!added) return; // already a member

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            if (!await db.ChatMembers.AnyAsync(m => m.ChatId == chatId && m.UserId == targetId))
            {
                db.ChatMembers.Add(new ChatMemberEntity { ChatId = chatId, UserId = targetId });
                await db.SaveChangesAsync();
            }
        }
        catch { }

        var label = _groupChatNames.TryGetValue(chatId, out var n) ? n : chatId;
        var connMap = _chatMembersByConn.GetOrAdd(chatId, _ => new ConcurrentDictionary<string, byte>());
        var targetConns = ConnectionsFor(targetId).ToList();
        foreach (var cid in targetConns)
        {
            await Groups.AddToGroupAsync(cid, chatId);
            connMap[cid] = 1;
        }
        if (targetConns.Count > 0)
            await Clients.Clients(targetConns).SendAsync("AddedChat", chatId, label);

        await Clients.Group(chatId).SendAsync("ChatSystemMessage", chatId, $"{DisplayNameOf(targetId)} was added to the group.");
        await Clients.All.SendAsync("ChatsUpdated", new[] { chatId });
    }

    public async Task RemoveGroupMember(string chatId, string displayName)
    {
        var me = RequireUserId();
        RequireGroupAdmin(chatId, me);

        if (!_userIdByDisplayName.TryGetValue((displayName ?? string.Empty).Trim(), out var targetId))
            throw new HubException($"Could not find a user named '{displayName}'.");

        if (Ci.Equals(targetId, me))
            throw new HubException("Use \"leave\" to remove yourself.");

        if (_chatMembers.TryGetValue(chatId, out var set)) lock (set) set.Remove(targetId);
        if (_groupChatAdmins.TryGetValue(chatId, out var admins)) lock (admins) admins.Remove(targetId);

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var row = await db.ChatMembers.FindAsync(chatId, targetId);
            if (row is not null)
            {
                db.ChatMembers.Remove(row);
                await db.SaveChangesAsync();
            }
        }
        catch { }

        var targetConns = ConnectionsFor(targetId).ToList();
        if (_chatMembersByConn.TryGetValue(chatId, out var connMap))
        {
            foreach (var cid in targetConns)
            {
                await Groups.RemoveFromGroupAsync(cid, chatId);
                connMap.TryRemove(cid, out _);
            }
        }

        if (targetConns.Count > 0)
            await Clients.Clients(targetConns).SendAsync("RemovedFromChat", chatId);

        await Clients.Group(chatId).SendAsync("ChatSystemMessage", chatId, $"{DisplayNameOf(targetId)} was removed from the group.");

        var successor = await SucceedAdminIfNoneRemainAsync(chatId);
        if (successor is not null)
            await Clients.Group(chatId).SendAsync("ChatSystemMessage", chatId, $"{DisplayNameOf(successor)} is now an admin of this group.");

        await Clients.All.SendAsync("ChatsUpdated", new[] { chatId });
    }

    public async Task RenameGroupChat(string chatId, string newName)
    {
        var me = RequireUserId();
        RequireGroupAdmin(chatId, me);

        newName = (newName ?? string.Empty).Trim();
        if (newName.Length == 0) throw new HubException("Group name can't be empty.");
        if (newName.Length > MaxDisplayNameLength) newName = newName[..MaxDisplayNameLength];

        _groupChatNames[chatId] = newName;

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var chat = await db.Chats.FindAsync(chatId);
            if (chat is not null)
            {
                chat.Name = newName;
                await db.SaveChangesAsync();
            }
        }
        catch { }

        await Clients.Group(chatId).SendAsync("ChatRenamed", chatId, newName);
    }

    public async Task JoinChat(string chatId)
    {
        var me = RequireUserId();
        RequireMembership(chatId, me);

        var set = _chatMembersByConn.GetOrAdd(chatId, _ => new ConcurrentDictionary<string, byte>());
        set[Context.ConnectionId] = 1;
        await Groups.AddToGroupAsync(Context.ConnectionId, chatId);
    }

    public async Task LeaveChat(string chatId)
    {
        var me = RequireUserId();
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, chatId);

        if (_chatMembers.TryGetValue(chatId, out var set)) lock (set) set.Remove(me);
        if (_groupChatAdmins.TryGetValue(chatId, out var admins)) lock (admins) admins.Remove(me);
        if (_chatMembersByConn.TryGetValue(chatId, out var conns)) conns.TryRemove(Context.ConnectionId, out _);

        // Only group membership is persisted here - Lobby/DMs aren't rows in ChatMembers to begin
        // with (see ChatMemberEntity), so there's nothing to remove for those.
        if (chatId.StartsWith("group:", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync();
                var row = await db.ChatMembers.FindAsync(chatId, me);
                if (row is not null)
                {
                    db.ChatMembers.Remove(row);
                    await db.SaveChangesAsync();
                }
            }
            catch { }

            var successor = await SucceedAdminIfNoneRemainAsync(chatId);
            if (successor is not null)
                await Clients.Group(chatId).SendAsync("ChatSystemMessage", chatId, $"{DisplayNameOf(successor)} is now an admin of this group.");

            await Clients.Group(chatId).SendAsync("ChatSystemMessage", chatId, $"{DisplayNameOf(me)} left the group.");
            await Clients.All.SendAsync("ChatsUpdated", new[] { chatId });
        }
    }

    public async Task SendToChat(string chatId, string message, long? replyToMessageId = null)
    {
        var me = RequireUserId();
        RequireMembership(chatId, me);
        EnforceRateLimit("sendToChat", maxPerWindow: 10, window: TimeSpan.FromSeconds(10));

        // A block that happens after a DM already exists still stops new messages both ways.
        if (_dmParticipants.TryGetValue(chatId, out var dmPair) && IsBlockedEitherWay(dmPair.User1, dmPair.User2))
            throw new HubException("You can't send messages in this chat.");

        message = (message ?? string.Empty).Trim();
        if (message.Length == 0) return;
        if (message.Length > MaxMessageLength) message = message[..MaxMessageLength];

        var name = DisplayNameOf(me);
        var sentAt = DateTime.UtcNow;

        await using var db = await _dbFactory.CreateDbContextAsync();
        var replyTo = await BuildReplyPreviewAsync(db, chatId, replyToMessageId);

        var entity = new ChatMessageEntity
        {
            ChatId = chatId,
            SenderUserId = me,
            SenderDisplayName = name,
            Body = message,
            SentAtUtc = sentAt,
            ReplyToMessageId = replyTo?.MessageId,
        };
        db.Messages.Add(entity);
        await db.SaveChangesAsync();

        await BroadcastMessageAsync(chatId, entity.Id, name, message, sentAt, attachment: null, isForwarded: false, replyTo);
    }

    // Shared by SendToChat/SendAttachment/SendVoiceMessage/ForwardMessage: deliver to everyone
    // currently joined to this chat's SignalR group, then (for a DM) also notify participants who
    // haven't joined the group yet - e.g. a second tab/device that hasn't opened this conversation.
    private async Task BroadcastMessageAsync(
        string chatId, long messageId, string senderDisplayName, string body, DateTime sentAt,
        AttachmentMetaDto? attachment, bool isForwarded, ReplyPreviewDto? replyTo)
    {
        await Clients.Group(chatId).SendAsync("ReceiveChatMessage",
            chatId, messageId, senderDisplayName, body, sentAt, attachment, isForwarded, replyTo);

        if (!_dmParticipants.TryGetValue(chatId, out var pair)) return;

        var groupMembers = _chatMembersByConn.TryGetValue(chatId, out var members)
            ? members.Keys
            : Enumerable.Empty<string>();

        var allConnIds = ResolveLiveConnectionsFor(new[] { pair.User1, pair.User2 });
        var senderConnId = Context.ConnectionId;
        var notInGroup = allConnIds.Where(cid => cid != senderConnId && !groupMembers.Contains(cid));

        foreach (var cid in notInGroup)
        {
            await Clients.Client(cid).SendAsync("DmNotify",
                chatId, messageId, senderDisplayName, body, sentAt, attachment, isForwarded, replyTo);
        }
    }

    // Resolves a reply-to preview, validating the referenced message actually lives in the same
    // chat being sent to - without that check, a caller could reference a message id from a
    // completely different (private) chat and have its content leaked into this one via the
    // preview. Returns null for a null replyToMessageId (the common "not a reply" case).
    private async Task<ReplyPreviewDto?> BuildReplyPreviewAsync(ChatDbContext db, string chatId, long? replyToMessageId)
    {
        if (replyToMessageId is not { } id) return null;

        var target = await db.Messages.FindAsync(id)
            ?? throw new HubException("The message you're replying to no longer exists.");
        if (!Ci.Equals(target.ChatId, chatId))
            throw new HubException("Can't reply to a message from a different chat.");

        return BuildReplyPreview(target);
    }

    private static ReplyPreviewDto BuildReplyPreview(ChatMessageEntity target)
    {
        if (target.IsDeleted)
            return new ReplyPreviewDto(target.Id, target.SenderDisplayName, string.Empty, IsDeleted: true);

        var snippet = target.AttachmentContentType switch
        {
            null => target.Body,
            var ct when ct.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) => "🎤 Voice message",
            var ct when ct.StartsWith("video/", StringComparison.OrdinalIgnoreCase) => "🎥 Video",
            var ct when ct.StartsWith("image/", StringComparison.OrdinalIgnoreCase) => "📷 Photo",
            _ => $"📄 {target.AttachmentFileName ?? "File"}",
        };
        if (snippet.Length > 80) snippet = snippet[..80] + "…";

        return new ReplyPreviewDto(target.Id, target.SenderDisplayName, snippet, IsDeleted: false);
    }

    // Common validation for anything that posts a binary attachment into a chat (image, voice
    // note, or a forwarded copy of one): membership, DM blocking, size cap, allowed content types.
    private void ValidateAttachmentPost(string chatId, string me, int dataLength, int maxBytes, string contentType, HashSet<string> allowedTypes, string kindLabel)
    {
        RequireMembership(chatId, me);

        if (_dmParticipants.TryGetValue(chatId, out var dmPair) && IsBlockedEitherWay(dmPair.User1, dmPair.User2))
            throw new HubException("You can't send messages in this chat.");

        if (dataLength == 0)
            throw new HubException($"{kindLabel} is empty.");
        if (dataLength > maxBytes)
            throw new HubException($"{kindLabel} too large (max {maxBytes / 1024 / 1024.0:0.#} MB).");
        if (string.IsNullOrWhiteSpace(contentType) || !allowedTypes.Contains(contentType))
            throw new HubException($"Unsupported {kindLabel.ToLowerInvariant()} type.");
    }

    // Shares an image in a chat, optionally with a text caption. Images only (see
    // AllowedImageContentTypes) and capped at MaxAttachmentBytes - this is "share a photo",
    // not general file transfer.
    public async Task<long> SendAttachment(string chatId, string fileName, string contentType, byte[] data, string? caption, long? replyToMessageId = null)
    {
        var me = RequireUserId();
        EnforceRateLimit("sendToChat", maxPerWindow: 10, window: TimeSpan.FromSeconds(10));
        ValidateAttachmentPost(chatId, me, data?.Length ?? 0, MaxAttachmentBytes, contentType, AllowedImageContentTypes, "Attachment");

        fileName = string.IsNullOrWhiteSpace(fileName) ? "image" : fileName.Trim();
        if (fileName.Length > 200) fileName = fileName[..200];

        var body = (caption ?? string.Empty).Trim();
        if (body.Length > MaxMessageLength) body = body[..MaxMessageLength];

        var name = DisplayNameOf(me);
        var sentAt = DateTime.UtcNow;

        long messageId;
        ReplyPreviewDto? replyTo;
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            replyTo = await BuildReplyPreviewAsync(db, chatId, replyToMessageId);
            var entity = new ChatMessageEntity
            {
                ChatId = chatId,
                SenderUserId = me,
                SenderDisplayName = name,
                Body = body,
                SentAtUtc = sentAt,
                AttachmentFileName = fileName,
                AttachmentContentType = contentType,
                AttachmentData = data,
                AttachmentSizeBytes = data!.Length,
                ReplyToMessageId = replyTo?.MessageId,
            };
            db.Messages.Add(entity);
            await db.SaveChangesAsync();
            messageId = entity.Id;
        }
        catch (HubException) { throw; }
        catch (Exception ex)
        {
            throw new HubException("Failed to save the attachment. Please try again.", ex);
        }

        var meta = new AttachmentMetaDto(fileName, contentType, data.Length, DurationSeconds: null);
        await BroadcastMessageAsync(chatId, messageId, name, body, sentAt, meta, isForwarded: false, replyTo);
        return messageId;
    }

    // Shares a voice note - same storage/lazy-fetch model as an image attachment (see
    // SendAttachment/GetAttachmentData), just with an audio content-type allowlist, a smaller
    // size cap suited to ~2 minutes of compressed audio, and a client-reported duration purely
    // for display (not re-validated server-side - a wrong value only affects the shown label).
    public async Task<long> SendVoiceMessage(string chatId, string contentType, byte[] data, int durationSeconds, long? replyToMessageId = null)
    {
        var me = RequireUserId();
        EnforceRateLimit("sendToChat", maxPerWindow: 10, window: TimeSpan.FromSeconds(10));
        ValidateAttachmentPost(chatId, me, data?.Length ?? 0, MaxVoiceMessageBytes, contentType, AllowedAudioContentTypes, "Voice message");

        durationSeconds = Math.Max(0, durationSeconds);
        var name = DisplayNameOf(me);
        var sentAt = DateTime.UtcNow;

        long messageId;
        ReplyPreviewDto? replyTo;
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            replyTo = await BuildReplyPreviewAsync(db, chatId, replyToMessageId);
            var entity = new ChatMessageEntity
            {
                ChatId = chatId,
                SenderUserId = me,
                SenderDisplayName = name,
                Body = string.Empty,
                SentAtUtc = sentAt,
                AttachmentFileName = "Voice message",
                AttachmentContentType = contentType,
                AttachmentData = data,
                AttachmentSizeBytes = data!.Length,
                AttachmentDurationSeconds = durationSeconds,
                ReplyToMessageId = replyTo?.MessageId,
            };
            db.Messages.Add(entity);
            await db.SaveChangesAsync();
            messageId = entity.Id;
        }
        catch (HubException) { throw; }
        catch (Exception ex)
        {
            throw new HubException("Failed to save the voice message. Please try again.", ex);
        }

        var meta = new AttachmentMetaDto("Voice message", contentType, data.Length, durationSeconds);
        await BroadcastMessageAsync(chatId, messageId, name, string.Empty, sentAt, meta, isForwarded: false, replyTo);
        return messageId;
    }

    // Shares a short video clip - same storage/lazy-fetch model as an image or voice attachment,
    // just with a video content-type allowlist and a larger size cap. DurationSeconds is
    // optional and purely for display (the client has no reliable way to read a picked video's
    // duration without a dedicated media library, so it's often just omitted) - unlike a voice
    // message, where the recorder always knows exactly how long it ran.
    public async Task<long> SendVideo(string chatId, string fileName, string contentType, byte[] data, int? durationSeconds = null, long? replyToMessageId = null)
    {
        var me = RequireUserId();
        EnforceRateLimit("sendToChat", maxPerWindow: 10, window: TimeSpan.FromSeconds(10));
        ValidateAttachmentPost(chatId, me, data?.Length ?? 0, MaxVideoBytes, contentType, AllowedVideoContentTypes, "Video");

        fileName = string.IsNullOrWhiteSpace(fileName) ? "video" : fileName.Trim();
        if (fileName.Length > 200) fileName = fileName[..200];
        var duration = durationSeconds is { } d ? Math.Max(0, d) : (int?)null;

        var name = DisplayNameOf(me);
        var sentAt = DateTime.UtcNow;

        long messageId;
        ReplyPreviewDto? replyTo;
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            replyTo = await BuildReplyPreviewAsync(db, chatId, replyToMessageId);
            var entity = new ChatMessageEntity
            {
                ChatId = chatId,
                SenderUserId = me,
                SenderDisplayName = name,
                Body = string.Empty,
                SentAtUtc = sentAt,
                AttachmentFileName = fileName,
                AttachmentContentType = contentType,
                AttachmentData = data,
                AttachmentSizeBytes = data!.Length,
                AttachmentDurationSeconds = duration,
                ReplyToMessageId = replyTo?.MessageId,
            };
            db.Messages.Add(entity);
            await db.SaveChangesAsync();
            messageId = entity.Id;
        }
        catch (HubException) { throw; }
        catch (Exception ex)
        {
            throw new HubException("Failed to save the video. Please try again.", ex);
        }

        var meta = new AttachmentMetaDto(fileName, contentType, data.Length, duration);
        await BroadcastMessageAsync(chatId, messageId, name, string.Empty, sentAt, meta, isForwarded: false, replyTo);
        return messageId;
    }

    // Shares a generic document (PDF, Office file, zip, ...) - same storage/lazy-fetch model as
    // everything else, but no content-type allowlist (see DisallowedDocumentExtensions above for
    // why a denylist instead). The client opens it externally via the OS's own handler rather
    // than rendering it inline, the same way it already does for a video.
    public async Task<long> SendFile(string chatId, string fileName, string contentType, byte[] data, long? replyToMessageId = null)
    {
        var me = RequireUserId();
        EnforceRateLimit("sendToChat", maxPerWindow: 10, window: TimeSpan.FromSeconds(10));
        RequireMembership(chatId, me);

        if (_dmParticipants.TryGetValue(chatId, out var dmPair) && IsBlockedEitherWay(dmPair.User1, dmPair.User2))
            throw new HubException("You can't send messages in this chat.");

        if (data is null || data.Length == 0)
            throw new HubException("File is empty.");
        if (data.Length > MaxDocumentBytes)
            throw new HubException($"File too large (max {MaxDocumentBytes / 1024 / 1024} MB).");

        fileName = string.IsNullOrWhiteSpace(fileName) ? "file" : fileName.Trim();
        if (fileName.Length > 200) fileName = fileName[..200];
        if (DisallowedDocumentExtensions.Contains(Path.GetExtension(fileName)))
            throw new HubException("This file type isn't allowed for sharing.");

        contentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType;

        var name = DisplayNameOf(me);
        var sentAt = DateTime.UtcNow;

        long messageId;
        ReplyPreviewDto? replyTo;
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            replyTo = await BuildReplyPreviewAsync(db, chatId, replyToMessageId);
            var entity = new ChatMessageEntity
            {
                ChatId = chatId,
                SenderUserId = me,
                SenderDisplayName = name,
                Body = string.Empty,
                SentAtUtc = sentAt,
                AttachmentFileName = fileName,
                AttachmentContentType = contentType,
                AttachmentData = data,
                AttachmentSizeBytes = data.Length,
                ReplyToMessageId = replyTo?.MessageId,
            };
            db.Messages.Add(entity);
            await db.SaveChangesAsync();
            messageId = entity.Id;
        }
        catch (HubException) { throw; }
        catch (Exception ex)
        {
            throw new HubException("Failed to save the file. Please try again.", ex);
        }

        var meta = new AttachmentMetaDto(fileName, contentType, data.Length, DurationSeconds: null);
        await BroadcastMessageAsync(chatId, messageId, name, string.Empty, sentAt, meta, isForwarded: false, replyTo);
        return messageId;
    }

    // Fetches an attachment's bytes on demand - GetChatHistory/live broadcasts only ever carry
    // metadata (filename/content type/size/duration), so opening a chat with a long history of
    // images/voice notes doesn't mean downloading every one of them up front.
    public async Task<AttachmentDataDto> GetAttachmentData(long messageId)
    {
        var me = RequireUserId();

        await using var db = await _dbFactory.CreateDbContextAsync();
        var msg = await db.Messages.FindAsync(messageId) ?? throw new HubException("Message not found.");
        RequireMembership(msg.ChatId, me);

        if (msg.AttachmentData is null || msg.AttachmentContentType is null)
            throw new HubException("This message has no attachment.");

        return new AttachmentDataDto(msg.AttachmentContentType, msg.AttachmentData);
    }

    // Copies a message (text and/or attachment) into a different chat the caller is a member of.
    // A forward is its own new message - editing/deleting the original never touches the copy -
    // sent as the forwarder, not the original sender, and flagged IsForwarded so the client can
    // label it "Forwarded" without guessing.
    public async Task<long> ForwardMessage(long messageId, string targetChatId)
    {
        var me = RequireUserId();
        RequireMembership(targetChatId, me);
        EnforceRateLimit("sendToChat", maxPerWindow: 10, window: TimeSpan.FromSeconds(10));

        if (_dmParticipants.TryGetValue(targetChatId, out var dmPair) && IsBlockedEitherWay(dmPair.User1, dmPair.User2))
            throw new HubException("You can't send messages in this chat.");

        await using var db = await _dbFactory.CreateDbContextAsync();
        var source = await db.Messages.FindAsync(messageId) ?? throw new HubException("Message not found.");
        RequireMembership(source.ChatId, me);
        if (source.IsDeleted) throw new HubException("Can't forward a deleted message.");

        var name = DisplayNameOf(me);
        var sentAt = DateTime.UtcNow;

        long newMessageId;
        try
        {
            var copy = new ChatMessageEntity
            {
                ChatId = targetChatId,
                SenderUserId = me,
                SenderDisplayName = name,
                Body = source.Body,
                SentAtUtc = sentAt,
                AttachmentFileName = source.AttachmentFileName,
                AttachmentContentType = source.AttachmentContentType,
                AttachmentData = source.AttachmentData,
                AttachmentSizeBytes = source.AttachmentSizeBytes,
                AttachmentDurationSeconds = source.AttachmentDurationSeconds,
                IsForwarded = true,
            };
            db.Messages.Add(copy);
            await db.SaveChangesAsync();
            newMessageId = copy.Id;
        }
        catch (Exception ex)
        {
            throw new HubException("Failed to forward the message. Please try again.", ex);
        }

        var meta = source.AttachmentContentType is null ? null
            : new AttachmentMetaDto(source.AttachmentFileName ?? "attachment", source.AttachmentContentType,
                source.AttachmentSizeBytes ?? 0, source.AttachmentDurationSeconds);

        await BroadcastMessageAsync(targetChatId, newMessageId, name, source.Body, sentAt, meta, isForwarded: true, replyTo: null);
        return newMessageId;
    }

    public async Task EditMessage(long messageId, string newBody)
    {
        var me = RequireUserId();

        await using var db = await _dbFactory.CreateDbContextAsync();
        var msg = await db.Messages.FindAsync(messageId) ?? throw new HubException("Message not found.");
        if (!Ci.Equals(msg.SenderUserId, me)) throw new HubException("You can only edit your own messages.");
        if (msg.IsDeleted) throw new HubException("Can't edit a deleted message.");

        newBody = (newBody ?? string.Empty).Trim();
        if (newBody.Length == 0) throw new HubException("Message can't be empty.");
        if (newBody.Length > MaxMessageLength) newBody = newBody[..MaxMessageLength];

        if (!Ci.Equals(msg.Body, newBody))
        {
            db.MessageEditHistory.Add(new MessageEditHistoryEntity
            {
                MessageId = msg.Id,
                PreviousBody = msg.Body,
                EditedAtUtc = DateTime.UtcNow,
            });
        }

        msg.Body = newBody;
        msg.EditedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();

        await Clients.Group(msg.ChatId).SendAsync("MessageEdited", msg.ChatId, msg.Id, newBody, msg.EditedAtUtc);
    }

    // Full history of what a message used to say, oldest first, ending just before its current
    // body (which the client already has). Membership-checked against the message's chat, same
    // as GetAttachmentData.
    public async Task<List<MessageEditHistoryDto>> GetMessageEditHistory(long messageId)
    {
        var me = RequireUserId();

        await using var db = await _dbFactory.CreateDbContextAsync();
        var msg = await db.Messages.FindAsync(messageId) ?? throw new HubException("Message not found.");
        RequireMembership(msg.ChatId, me);

        var rows = await db.MessageEditHistory
            .Where(h => h.MessageId == messageId)
            .OrderBy(h => h.EditedAtUtc)
            .ToListAsync();

        return rows.Select(h => new MessageEditHistoryDto(h.PreviousBody, h.EditedAtUtc)).ToList();
    }

    public async Task DeleteMessage(long messageId)
    {
        var me = RequireUserId();

        await using var db = await _dbFactory.CreateDbContextAsync();
        var msg = await db.Messages.FindAsync(messageId) ?? throw new HubException("Message not found.");
        if (!Ci.Equals(msg.SenderUserId, me)) throw new HubException("You can only delete your own messages.");

        msg.IsDeleted = true;
        msg.Body = string.Empty;
        var wasPinned = msg.IsPinned;
        msg.IsPinned = false; // a deleted message can't stay in the pinned list
        await db.SaveChangesAsync();

        await Clients.Group(msg.ChatId).SendAsync("MessageDeleted", msg.ChatId, msg.Id);
        if (wasPinned)
            await Clients.Group(msg.ChatId).SendAsync("MessageUnpinned", msg.ChatId, msg.Id);
    }

    // Any current member can pin/unpin - same "no special permission gate" philosophy as
    // reactions/forwarding, rather than restricting it to group admins.
    private const int MaxPinnedPerChat = 20;

    public async Task PinMessage(long messageId)
    {
        var me = RequireUserId();

        await using var db = await _dbFactory.CreateDbContextAsync();
        var msg = await db.Messages.FindAsync(messageId) ?? throw new HubException("Message not found.");
        RequireMembership(msg.ChatId, me);
        if (msg.IsDeleted) throw new HubException("Can't pin a deleted message.");
        if (msg.IsPinned) return;

        var pinnedCount = await db.Messages.CountAsync(m => m.ChatId == msg.ChatId && m.IsPinned);
        if (pinnedCount >= MaxPinnedPerChat)
            throw new HubException($"This chat already has the maximum of {MaxPinnedPerChat} pinned messages - unpin one first.");

        msg.IsPinned = true;
        await db.SaveChangesAsync();

        await Clients.Group(msg.ChatId).SendAsync("MessagePinned", msg.ChatId, msg.Id, DisplayNameOf(me));
    }

    public async Task UnpinMessage(long messageId)
    {
        var me = RequireUserId();

        await using var db = await _dbFactory.CreateDbContextAsync();
        var msg = await db.Messages.FindAsync(messageId) ?? throw new HubException("Message not found.");
        RequireMembership(msg.ChatId, me);
        if (!msg.IsPinned) return;

        msg.IsPinned = false;
        await db.SaveChangesAsync();

        await Clients.Group(msg.ChatId).SendAsync("MessageUnpinned", msg.ChatId, msg.Id);
    }

    public async Task<List<ChatMessageDto>> GetPinnedMessages(string chatId)
    {
        var me = RequireUserId();
        RequireMembership(chatId, me);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var rows = await db.Messages
            .Where(m => m.ChatId == chatId && m.IsPinned)
            .OrderBy(m => m.SentAtUtc)
            .ToListAsync();

        return await ToDtosAsync(db, rows, me);
    }

    // Toggle: reacting again with the same emoji removes it. One reaction per (user, emoji) per
    // message, enforced by a unique index - see ChatDbContext.
    public async Task ToggleReaction(long messageId, string emoji)
    {
        var me = RequireUserId();
        EnforceRateLimit("reaction", maxPerWindow: 20, window: TimeSpan.FromSeconds(10));

        emoji = (emoji ?? string.Empty).Trim();
        if (emoji.Length == 0 || emoji.Length > 8) throw new HubException("Invalid emoji.");

        await using var db = await _dbFactory.CreateDbContextAsync();
        var msg = await db.Messages.FindAsync(messageId) ?? throw new HubException("Message not found.");
        RequireMembership(msg.ChatId, me);

        var existing = await db.Reactions.FirstOrDefaultAsync(
            r => r.MessageId == messageId && r.UserId == me && r.Emoji == emoji);

        bool added;
        if (existing is not null)
        {
            db.Reactions.Remove(existing);
            added = false;
        }
        else
        {
            db.Reactions.Add(new MessageReactionEntity { MessageId = messageId, UserId = me, Emoji = emoji });
            added = true;
        }
        await db.SaveChangesAsync();

        var count = await db.Reactions.CountAsync(r => r.MessageId == messageId && r.Emoji == emoji);
        await Clients.Group(msg.ChatId).SendAsync("ReactionChanged", msg.ChatId, messageId, emoji, count, added, DisplayNameOf(me));
    }

    // Tells other chat members "I've seen up to message X", so a DM can show a "Seen" marker.
    public async Task MarkRead(string chatId, long lastReadMessageId)
    {
        var me = RequireUserId();
        RequireMembership(chatId, me);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var existing = await db.ReadReceipts.FindAsync(chatId, me);

        if (existing is null)
        {
            db.ReadReceipts.Add(new ReadReceiptEntity
            {
                ChatId = chatId,
                UserId = me,
                LastReadMessageId = lastReadMessageId,
                LastReadAtUtc = DateTime.UtcNow
            });
        }
        else if (lastReadMessageId > existing.LastReadMessageId)
        {
            existing.LastReadMessageId = lastReadMessageId;
            existing.LastReadAtUtc = DateTime.UtcNow;
        }
        else
        {
            return; // Nothing newer than what's already recorded - no-op.
        }

        await db.SaveChangesAsync();
        await Clients.OthersInGroup(chatId).SendAsync("ReadReceipt", chatId, DisplayNameOf(me), lastReadMessageId);
    }

    // Bootstraps a client's view of who has read up to where in this chat - the live "ReadReceipt"
    // event above only carries deltas going forward, so a client that just opened a group chat
    // needs this to know what happened before it connected (used to compute "seen by N/M").
    public async Task<Dictionary<string, long>> GetChatReadReceipts(string chatId)
    {
        var me = RequireUserId();
        RequireMembership(chatId, me);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var rows = await db.ReadReceipts.Where(r => r.ChatId == chatId).ToListAsync();

        var result = new Dictionary<string, long>(Ci);
        foreach (var r in rows) result[DisplayNameOf(r.UserId)] = r.LastReadMessageId;
        return result;
    }

    // -------------------------------------------------------
    // Blocking & muting
    // -------------------------------------------------------
    public async Task BlockUser(string displayName)
    {
        var me = RequireUserId();

        if (!_userIdByDisplayName.TryGetValue((displayName ?? string.Empty).Trim(), out var targetId))
            throw new HubException($"Could not find a user named '{displayName}'.");
        if (Ci.Equals(targetId, me))
            throw new HubException("You can't block yourself.");

        _blockedByUser.AddOrUpdate(me,
            _ => new HashSet<string> { targetId },
            (_, set) => { lock (set) set.Add(targetId); return set; });

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            if (!await db.Blocks.AnyAsync(b => b.BlockerUserId == me && b.BlockedUserId == targetId))
            {
                db.Blocks.Add(new BlockedUserEntity { BlockerUserId = me, BlockedUserId = targetId, CreatedAtUtc = DateTime.UtcNow });
                await db.SaveChangesAsync();
            }
        }
        catch { }
    }

    public async Task UnblockUser(string displayName)
    {
        var me = RequireUserId();

        if (!_userIdByDisplayName.TryGetValue((displayName ?? string.Empty).Trim(), out var targetId))
            throw new HubException($"Could not find a user named '{displayName}'.");

        if (_blockedByUser.TryGetValue(me, out var set)) lock (set) set.Remove(targetId);

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var row = await db.Blocks.FindAsync(me, targetId);
            if (row is not null)
            {
                db.Blocks.Remove(row);
                await db.SaveChangesAsync();
            }
        }
        catch { }
    }

    public Task<List<string>> GetBlockedUsers()
    {
        var me = RequireUserId();
        var names = _blockedByUser.TryGetValue(me, out var set)
            ? set.Select(DisplayNameOf).OrderBy(n => n, Ci).ToList()
            : new List<string>();
        return Task.FromResult(names);
    }

    private static bool IsBlockedEitherWay(string a, string b) =>
        (_blockedByUser.TryGetValue(a, out var setA) && setA.Contains(b)) ||
        (_blockedByUser.TryGetValue(b, out var setB) && setB.Contains(a));

    // Muting never touches delivery - the message still arrives and gets persisted normally,
    // this only tells the *caller's own* client to stop bumping the unread badge for this chat.
    public async Task SetChatMuted(string chatId, bool muted)
    {
        var me = RequireUserId();
        RequireMembership(chatId, me);

        var key = (me, chatId);
        if (muted) _mutedChats[key] = 1;
        else _mutedChats.TryRemove(key, out _);

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var existing = await db.MutedChats.FindAsync(me, chatId);
            if (muted && existing is null)
            {
                db.MutedChats.Add(new MutedChatEntity { UserId = me, ChatId = chatId, MutedAtUtc = DateTime.UtcNow });
                await db.SaveChangesAsync();
            }
            else if (!muted && existing is not null)
            {
                db.MutedChats.Remove(existing);
                await db.SaveChangesAsync();
            }
        }
        catch { }
    }

    // Purely a per-user ordering preference - pinned chats sort to the top of that user's own
    // list (see BuildChatSummariesAsync); nobody else sees it and delivery is unaffected.
    public async Task SetChatPinned(string chatId, bool pinned)
    {
        var me = RequireUserId();
        RequireMembership(chatId, me);

        var key = (me, chatId);
        if (pinned) _pinnedChats[key] = 1;
        else _pinnedChats.TryRemove(key, out _);

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var existing = await db.PinnedChats.FindAsync(me, chatId);
            if (pinned && existing is null)
            {
                db.PinnedChats.Add(new PinnedChatEntity { UserId = me, ChatId = chatId, PinnedAtUtc = DateTime.UtcNow });
                await db.SaveChangesAsync();
            }
            else if (!pinned && existing is not null)
            {
                db.PinnedChats.Remove(existing);
                await db.SaveChangesAsync();
            }
        }
        catch { }
    }

    // Backfills a chat's message history for a client that just opened it (e.g. after
    // reconnecting, or opening the app fresh and everything else is in-memory-only).
    // Pass beforeMessageId (the oldest message id currently loaded) to page further back.
    public async Task<List<ChatMessageDto>> GetChatHistory(string chatId, long? beforeMessageId = null, int take = 50)
    {
        var me = RequireUserId();
        RequireMembership(chatId, me);
        take = Math.Clamp(take, 1, 200);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var query = db.Messages.Where(m => m.ChatId == chatId);
        if (beforeMessageId is { } before)
            query = query.Where(m => m.Id < before);

        var rows = await query.OrderByDescending(m => m.Id).Take(take).ToListAsync();
        rows.Reverse();

        return await ToDtosAsync(db, rows, me);
    }

    // Full-text (substring) search within one chat's history. Case-insensitive via ToLower() on
    // both sides rather than EF.Functions.Like, so behavior is identical whether this runs
    // against the real SQLite provider or the InMemory provider used in tests. Deleted messages
    // are excluded - their body is already blanked out everywhere else, so they'd never usefully
    // match anyway.
    public async Task<List<ChatMessageDto>> SearchMessages(string chatId, string query, int take = 50)
    {
        var me = RequireUserId();
        RequireMembership(chatId, me);
        take = Math.Clamp(take, 1, 200);

        query = (query ?? string.Empty).Trim();
        if (query.Length == 0) return new List<ChatMessageDto>();

        var needle = query.ToLowerInvariant();

        await using var db = await _dbFactory.CreateDbContextAsync();
        var rows = await db.Messages
            .Where(m => m.ChatId == chatId && !m.IsDeleted && m.Body.ToLower().Contains(needle))
            .OrderByDescending(m => m.Id)
            .Take(take)
            .ToListAsync();
        rows.Reverse();

        return await ToDtosAsync(db, rows, me);
    }

    // Fetches title/description/image metadata for a URL found in a message body (see
    // Services/LinkPreviewFetcher for the actual fetch + SSRF guard + caching). Just requires
    // being authenticated, not chat membership - a preview isn't scoped to a particular chat, and
    // the client only ever calls this for a URL it already legitimately saw in a message body.
    public async Task<LinkPreviewDto?> GetLinkPreview(string url)
    {
        RequireUserId();
        return await _linkPreviewFetcher.GetOrFetchAsync(url);
    }

    // Shared by GetChatHistory/SearchMessages: attaches each row's reactions and maps to the DTO
    // shape sent to clients.
    private async Task<List<ChatMessageDto>> ToDtosAsync(ChatDbContext db, List<ChatMessageEntity> rows, string me)
    {
        var ids = rows.Select(r => r.Id).ToList();
        var reactionRows = await db.Reactions.Where(r => ids.Contains(r.MessageId)).ToListAsync();
        var reactionsByMessage = reactionRows
            .GroupBy(r => r.MessageId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<ReactionDto>)g.GroupBy(r => r.Emoji)
                    .Select(eg => new ReactionDto(
                        eg.Key,
                        eg.Count(),
                        eg.Any(r => Ci.Equals(r.UserId, me)),
                        eg.Select(r => DisplayNameOf(r.UserId)).ToList()))
                    .ToList());

        var replyToIds = rows.Where(r => r.ReplyToMessageId is not null).Select(r => r.ReplyToMessageId!.Value).Distinct().ToList();
        var replyTargets = replyToIds.Count == 0
            ? new List<ChatMessageEntity>()
            : await db.Messages.Where(m => replyToIds.Contains(m.Id)).ToListAsync();
        var replyPreviewById = replyTargets.ToDictionary(m => m.Id, BuildReplyPreview);

        return rows.Select(m => new ChatMessageDto(
            m.Id,
            m.SenderDisplayName,
            m.IsDeleted ? string.Empty : m.Body,
            m.SentAtUtc,
            m.EditedAtUtc,
            m.IsDeleted,
            reactionsByMessage.TryGetValue(m.Id, out var reactions) ? reactions : Array.Empty<ReactionDto>(),
            m.AttachmentContentType is null ? null
                : new AttachmentMetaDto(m.AttachmentFileName ?? "attachment", m.AttachmentContentType,
                    m.AttachmentSizeBytes ?? 0, m.AttachmentDurationSeconds),
            m.IsForwarded,
            m.ReplyToMessageId is { } replyId && replyPreviewById.TryGetValue(replyId, out var preview) ? preview : null,
            m.IsPinned
        )).ToList();
    }

    // -------------------------------------------------------
    // Typing / recording indicators
    // -------------------------------------------------------
    public async Task Typing(string channelId, bool isTyping)
    {
        var me = RequireUserId();

        // Low-stakes and high-frequency: silently drop excess calls instead of throwing.
        if (!TryConsumeRateLimit("typing", maxPerWindow: 10, window: TimeSpan.FromSeconds(2)))
            return;

        await Clients.OthersInGroup(channelId).SendAsync("Typing", channelId, DisplayNameOf(me), isTyping);
    }

    // Same idea as Typing, but for "recording a voice message" - a separate event so the client
    // can show a distinct label instead of conflating it with plain text typing.
    public async Task SetRecordingVoiceMessage(string channelId, bool isRecording)
    {
        var me = RequireUserId();

        if (!TryConsumeRateLimit("recording", maxPerWindow: 10, window: TimeSpan.FromSeconds(2)))
            return;

        await Clients.OthersInGroup(channelId).SendAsync("RecordingVoiceMessage", channelId, DisplayNameOf(me), isRecording);
    }

    // -------------------------------------------------------
    // Call signaling (1:1 DMs only - see README for why: mesh signaling for a group call is a
    // meaningfully bigger problem, out of scope here)
    // -------------------------------------------------------
    //
    // The server never looks at an SDP offer/answer or ICE candidate's contents - it's a dumb
    // relay between exactly the two DM participants, identical in spirit to how a message is
    // delivered. Media itself flows peer-to-peer once signaling completes; this app deliberately
    // ships no STUN/TURN server, so a call between two peers behind restrictive NATs (common on
    // mobile data, some home routers) may fail to connect - see README's "Known simplifications".
    private string OtherDmParticipantOrThrow(string chatId, string me)
    {
        if (!_dmParticipants.TryGetValue(chatId, out var pair))
            throw new HubException("Calls are only supported in direct messages.");
        return Ci.Equals(pair.User1, me) ? pair.User2 : pair.User1;
    }

    public async Task CallInvite(string chatId, string kind, string sdpOffer)
    {
        var me = RequireUserId();
        RequireMembership(chatId, me);
        EnforceRateLimit("callInvite", maxPerWindow: 5, window: TimeSpan.FromSeconds(30));

        var otherId = OtherDmParticipantOrThrow(chatId, me);
        if (IsBlockedEitherWay(me, otherId))
            throw new HubException("You can't call this user.");

        var conns = ConnectionsFor(otherId).ToList();
        if (conns.Count == 0)
            throw new HubException($"{DisplayNameOf(otherId)} isn't online right now.");

        await Clients.Clients(conns).SendAsync("IncomingCall", chatId, DisplayNameOf(me), kind, sdpOffer);
    }

    public async Task CallAnswer(string chatId, string sdpAnswer)
    {
        var me = RequireUserId();
        RequireMembership(chatId, me);
        var otherId = OtherDmParticipantOrThrow(chatId, me);
        await Clients.Clients(ConnectionsFor(otherId)).SendAsync("CallAnswered", chatId, sdpAnswer);
    }

    public async Task CallDecline(string chatId)
    {
        var me = RequireUserId();
        RequireMembership(chatId, me);
        var otherId = OtherDmParticipantOrThrow(chatId, me);
        await Clients.Clients(ConnectionsFor(otherId)).SendAsync("CallDeclined", chatId);
    }

    // Trickled ICE candidates can arrive dozens of times per call as both sides probe network
    // paths - a much higher-frequency bucket than the other call methods, which each happen once
    // or twice per call.
    public async Task CallIceCandidate(string chatId, string candidateJson)
    {
        var me = RequireUserId();
        RequireMembership(chatId, me);
        if (!TryConsumeRateLimit("callIceCandidate", maxPerWindow: 40, window: TimeSpan.FromSeconds(5)))
            return;

        var otherId = OtherDmParticipantOrThrow(chatId, me);
        await Clients.Clients(ConnectionsFor(otherId)).SendAsync("CallIceCandidate", chatId, candidateJson);
    }

    public async Task CallHangup(string chatId)
    {
        var me = RequireUserId();
        RequireMembership(chatId, me);
        var otherId = OtherDmParticipantOrThrow(chatId, me);
        await Clients.Clients(ConnectionsFor(otherId)).SendAsync("CallEnded", chatId);
    }

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

    private async Task SendChatsToCallerAsync(string userId) =>
        await Clients.Caller.SendAsync("ChatsForMe", await BuildChatSummariesAsync(userId));

    // Unread counts come from the caller's own persisted read receipt (ChatHub.MarkRead) -
    // "last message id I've read in this chat" - versus how many messages from other people
    // exist past that point. That's what makes the badge survive an app restart instead of
    // only reflecting whatever arrived live during the current session.
    private async Task<List<ChatSummary>> BuildChatSummariesAsync(string userId)
    {
        var ids = _chatMembers
            .Where(kv => kv.Value.Contains(userId))
            .Select(kv => kv.Key)
            .Concat(new[] { LobbyId })
            .Distinct(Ci)
            .ToList();

        await using var db = await _dbFactory.CreateDbContextAsync();

        var lastReadByChat = await db.ReadReceipts
            .Where(r => r.UserId == userId && ids.Contains(r.ChatId))
            .ToDictionaryAsync(r => r.ChatId, r => r.LastReadMessageId);

        var mutedIds = new HashSet<string>(
            await db.MutedChats.Where(m => m.UserId == userId && ids.Contains(m.ChatId)).Select(m => m.ChatId).ToListAsync(),
            Ci);

        var pinnedIds = new HashSet<string>(
            await db.PinnedChats.Where(p => p.UserId == userId && ids.Contains(p.ChatId)).Select(p => p.ChatId).ToListAsync(),
            Ci);

        var list = new List<ChatSummary>();
        foreach (var id in ids)
        {
            var lastRead = lastReadByChat.TryGetValue(id, out var lr) ? lr : 0L;
            var unread = await db.Messages.CountAsync(m => m.ChatId == id && m.Id > lastRead && m.SenderUserId != userId);
            list.Add(new ChatSummary(id, ComputeChatLabelForUser(id, userId), unread, mutedIds.Contains(id), pinnedIds.Contains(id)));
        }

        // Stable sort: pinned chats float to the top (in their existing relative order), then
        // Lobby is forced to the very top regardless - it's always-there, not a "chat" you pin.
        list = list.OrderByDescending(c => c.IsPinned).ToList();

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

        if (_groupChatNames.TryGetValue(chatId, out var groupName)) return groupName;

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
    public static void PreloadDisplayNames(IEnumerable<ApplicationUser> users)
    {
        foreach (var u in users)
            ApplyDisplayName(u.Id, u.DisplayName);
    }

    // Same idea as PreloadDisplayNames, but for group chat metadata + membership.
    public static void PreloadGroupChats(IEnumerable<ChatEntity> chats, IEnumerable<ChatMemberEntity> members)
    {
        foreach (var c in chats)
        {
            _groupChatNames[c.ChatId] = c.Name;
            _groupChatCreator[c.ChatId] = c.CreatedByUserId;
        }

        foreach (var group in members.GroupBy(m => m.ChatId))
        {
            _chatMembers[group.Key] = new HashSet<string>(group.Select(m => m.UserId));
            _groupChatAdmins[group.Key] = new HashSet<string>(group.Where(m => m.IsAdmin).Select(m => m.UserId));
        }
    }

    public static void PreloadBlocks(IEnumerable<BlockedUserEntity> blocks)
    {
        foreach (var group in blocks.GroupBy(b => b.BlockerUserId))
            _blockedByUser[group.Key] = new HashSet<string>(group.Select(b => b.BlockedUserId));
    }

    public static void PreloadMutedChats(IEnumerable<MutedChatEntity> muted)
    {
        foreach (var m in muted)
            _mutedChats[(m.UserId, m.ChatId)] = 1;
    }

    public static void PreloadPinnedChats(IEnumerable<PinnedChatEntity> pinned)
    {
        foreach (var p in pinned)
            _pinnedChats[(p.UserId, p.ChatId)] = 1;
    }

    private async Task PersistGroupChatAsync(string chatId, string name, string createdByUserId, IEnumerable<string> memberIds)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            db.Chats.Add(new ChatEntity
            {
                ChatId = chatId,
                Name = name,
                CreatedByUserId = createdByUserId,
                CreatedAtUtc = DateTime.UtcNow
            });
            foreach (var uid in memberIds)
                db.ChatMembers.Add(new ChatMemberEntity { ChatId = chatId, UserId = uid, IsAdmin = Ci.Equals(uid, createdByUserId) });
            await db.SaveChangesAsync();
        }
        catch
        {
            // Best-effort, same as the other persistence helpers: the in-memory group still
            // works for this run even if it doesn't survive a restart.
        }
    }

    private async Task PersistLastSeenAsync(string userId)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var user = await db.Users.FindAsync(userId);
            if (user is not null)
            {
                user.LastSeenUtc = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }
        }
        catch
        {
            // Best-effort, same as the other persistence helpers.
        }
    }

    private async Task PersistDisplayNameAsync(string userId, string displayName)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var user = await db.Users.FindAsync(userId);
            if (user is not null)
            {
                user.DisplayName = displayName;
                await db.SaveChangesAsync();
            }
        }
        catch
        {
            // Best-effort: in-memory state (already applied by the caller) still
            // works for the rest of this run even if the write fails.
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

        var timestamps = _rateBuckets.GetOrAdd(key, _ => new ConcurrentQueue<long>());
        timestamps.Enqueue(now);

        while (timestamps.TryPeek(out var oldest) && now - oldest > windowTicks)
            timestamps.TryDequeue(out _);

        return timestamps.Count <= maxPerWindow;
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

    // Also re-checks current membership, not just admin status: leaving a group drops admin
    // rights along with everything else membership grants (see LeaveChat/RemoveGroupMember,
    // which both call SucceedAdminIfNoneRemain to keep a group from being left without one).
    private void RequireGroupAdmin(string chatId, string userId)
    {
        if (!_groupChatAdmins.TryGetValue(chatId, out var admins) || !admins.Contains(userId))
            throw new HubException("Only a group admin can do that.");

        RequireMembership(chatId, userId);
    }

    private bool IsGroupAdmin(string chatId, string userId) =>
        _groupChatAdmins.TryGetValue(chatId, out var admins) && admins.Contains(userId);

    // If a group's admin set is empty but it still has members, promotes the earliest-joined
    // remaining member (by insertion order isn't tracked, so this just picks a deterministic one)
    // so the group is never left without anyone able to manage it. Called after removing/losing
    // an admin. Persists the promotion and returns the promoted userId, or null if there was
    // already an admin (or no members left at all).
    private async Task<string?> SucceedAdminIfNoneRemainAsync(string chatId)
    {
        if (_groupChatAdmins.TryGetValue(chatId, out var admins) && admins.Count > 0) return null;
        if (!_chatMembers.TryGetValue(chatId, out var members) || members.Count == 0) return null;

        var successor = members.OrderBy(u => u, StringComparer.Ordinal).First();
        var set = _groupChatAdmins.GetOrAdd(chatId, _ => new HashSet<string>());
        lock (set) set.Add(successor);

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var row = await db.ChatMembers.FindAsync(chatId, successor);
            if (row is not null)
            {
                row.IsAdmin = true;
                await db.SaveChangesAsync();
            }
        }
        catch { }

        return successor;
    }

    public async Task PromoteGroupAdmin(string chatId, string displayName)
    {
        var me = RequireUserId();
        RequireGroupAdmin(chatId, me);

        if (!_userIdByDisplayName.TryGetValue((displayName ?? string.Empty).Trim(), out var targetId))
            throw new HubException($"Could not find a user named '{displayName}'.");
        if (!_chatMembers.TryGetValue(chatId, out var members) || !members.Contains(targetId))
            throw new HubException("That user isn't a member of this group.");

        var set = _groupChatAdmins.GetOrAdd(chatId, _ => new HashSet<string>());
        bool added;
        lock (set) added = set.Add(targetId);
        if (!added) return; // already an admin

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var row = await db.ChatMembers.FindAsync(chatId, targetId);
            if (row is not null)
            {
                row.IsAdmin = true;
                await db.SaveChangesAsync();
            }
        }
        catch { }

        await Clients.Group(chatId).SendAsync("ChatSystemMessage", chatId, $"{DisplayNameOf(targetId)} is now an admin of this group.");
    }

    public async Task DemoteGroupAdmin(string chatId, string displayName)
    {
        var me = RequireUserId();
        RequireGroupAdmin(chatId, me);

        if (!_userIdByDisplayName.TryGetValue((displayName ?? string.Empty).Trim(), out var targetId))
            throw new HubException($"Could not find a user named '{displayName}'.");

        if (_groupChatAdmins.TryGetValue(chatId, out var set) && set.Count <= 1 && set.Contains(targetId))
            throw new HubException("Can't remove the last admin - promote someone else first.");

        bool removed = false;
        if (set is not null) lock (set) removed = set.Remove(targetId);
        if (!removed) return; // wasn't an admin

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var row = await db.ChatMembers.FindAsync(chatId, targetId);
            if (row is not null)
            {
                row.IsAdmin = false;
                await db.SaveChangesAsync();
            }
        }
        catch { }

        await Clients.Group(chatId).SendAsync("ChatSystemMessage", chatId, $"{DisplayNameOf(targetId)} is no longer an admin of this group.");
    }

    public Task<List<string>> GetGroupAdmins(string chatId)
    {
        var me = RequireUserId();
        RequireMembership(chatId, me);

        var names = _groupChatAdmins.TryGetValue(chatId, out var admins)
            ? admins.Select(DisplayNameOf).OrderBy(n => n, Ci).ToList()
            : new List<string>();
        return Task.FromResult(names);
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

    // The "sub" claim in the server-issued JWT is the stable, non-spoofable identity of the caller.
    private string? GetUserId() =>
        Context.User?.FindFirst("sub")?.Value
        ?? Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    // The server issues its own tokens now (see Auth/JwtIssuer), so the display name chosen at
    // registration always arrives as a plain "display_name" claim - no nested metadata to parse.
    private string DeriveDefaultDisplayName()
    {
        var claimed = Context.User?.FindFirst("display_name")?.Value;
        if (!string.IsNullOrWhiteSpace(claimed)) return claimed.Trim();

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
