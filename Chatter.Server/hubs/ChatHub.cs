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
    private const int MaxAttachmentBytes = 5 * 1024 * 1024; // 5 MB
    private const int MaxAvatarBytes = 512 * 1024; // 512 KB

    // Images only, deliberately - this is "share a photo in chat", not general file transfer.
    private static readonly HashSet<string> AllowedImageContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/gif", "image/webp"
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

    // group chatId -> the userId that created it. The only admin concept this app has: whoever
    // created the group can add/remove members and rename it. No delegated admins, no ownership
    // transfer - see RequireGroupAdmin.
    private static readonly ConcurrentDictionary<string, string> _groupChatCreator = new();

    // ===== Blocking & muting =====
    // blocker userId -> set of userIds they've blocked. Enforced both ways (see
    // IsBlockedEitherWay) when starting or posting to a DM.
    private static readonly ConcurrentDictionary<string, HashSet<string>> _blockedByUser = new();

    // (userId, chatId) muted by that user - messages still arrive, this only suppresses their
    // own unread badge/notification. The other party is never told.
    private static readonly ConcurrentDictionary<(string UserId, string ChatId), byte> _mutedChats = new();

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
    // caller never sees it directly).
    public Task<Dictionary<string, string>> GetAvatarUrls(List<string> displayNames)
    {
        var result = new Dictionary<string, string>(Ci);
        foreach (var raw in displayNames ?? new List<string>())
        {
            var name = (raw ?? string.Empty).Trim();
            if (name.Length == 0) continue;
            if (_userIdByDisplayName.TryGetValue(name, out var userId))
                result[name] = $"/avatars/{userId}";
        }
        return Task.FromResult(result);
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
            throw new HubException("Use \"leave\" to remove yourself; the group's creator can't be removed this way.");

        if (_chatMembers.TryGetValue(chatId, out var set)) lock (set) set.Remove(targetId);

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

        // A block that happens after a DM already exists still stops new messages both ways.
        if (_dmParticipants.TryGetValue(chatId, out var dmPair) && IsBlockedEitherWay(dmPair.User1, dmPair.User2))
            throw new HubException("You can't send messages in this chat.");

        message = (message ?? string.Empty).Trim();
        if (message.Length == 0) return;
        if (message.Length > MaxMessageLength) message = message[..MaxMessageLength];

        var name = DisplayNameOf(me);
        var sentAt = DateTime.UtcNow;

        var messageId = await PersistMessageAsync(chatId, me, name, message, sentAt);

        await BroadcastMessageAsync(chatId, messageId, name, message, sentAt,
            attachmentFileName: null, attachmentContentType: null, attachmentSizeBytes: null);
    }

    // Shared by SendToChat and SendAttachment: deliver to everyone currently joined to this
    // chat's SignalR group, then (for a DM) also notify participants who haven't joined the
    // group yet - e.g. a second tab/device that hasn't opened this specific conversation.
    private async Task BroadcastMessageAsync(
        string chatId, long messageId, string senderDisplayName, string body, DateTime sentAt,
        string? attachmentFileName, string? attachmentContentType, int? attachmentSizeBytes)
    {
        await Clients.Group(chatId).SendAsync("ReceiveChatMessage",
            chatId, messageId, senderDisplayName, body, sentAt,
            attachmentFileName, attachmentContentType, attachmentSizeBytes);

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
                chatId, messageId, senderDisplayName, body, sentAt,
                attachmentFileName, attachmentContentType, attachmentSizeBytes);
        }
    }

    // Shares an image in a chat, optionally with a text caption. Images only (see
    // AllowedImageContentTypes) and capped at MaxAttachmentBytes - this is "share a photo",
    // not general file transfer.
    public async Task<long> SendAttachment(string chatId, string fileName, string contentType, byte[] data, string? caption)
    {
        var me = RequireUserId();
        RequireMembership(chatId, me);
        EnforceRateLimit("sendToChat", maxPerWindow: 10, window: TimeSpan.FromSeconds(10));

        if (_dmParticipants.TryGetValue(chatId, out var dmPair) && IsBlockedEitherWay(dmPair.User1, dmPair.User2))
            throw new HubException("You can't send messages in this chat.");

        if (data is null || data.Length == 0)
            throw new HubException("Attachment is empty.");
        if (data.Length > MaxAttachmentBytes)
            throw new HubException($"Attachment too large (max {MaxAttachmentBytes / 1024 / 1024} MB).");
        if (string.IsNullOrWhiteSpace(contentType) || !AllowedImageContentTypes.Contains(contentType))
            throw new HubException("Only PNG, JPEG, GIF, or WebP images are supported.");

        fileName = string.IsNullOrWhiteSpace(fileName) ? "image" : fileName.Trim();
        if (fileName.Length > 200) fileName = fileName[..200];

        var body = (caption ?? string.Empty).Trim();
        if (body.Length > MaxMessageLength) body = body[..MaxMessageLength];

        var name = DisplayNameOf(me);
        var sentAt = DateTime.UtcNow;

        long messageId;
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
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
                AttachmentSizeBytes = data.Length,
            };
            db.Messages.Add(entity);
            await db.SaveChangesAsync();
            messageId = entity.Id;
        }
        catch (Exception ex)
        {
            throw new HubException("Failed to save the attachment. Please try again.", ex);
        }

        await BroadcastMessageAsync(chatId, messageId, name, body, sentAt, fileName, contentType, data.Length);
        return messageId;
    }

    // Fetches an attachment's bytes on demand - GetChatHistory/live broadcasts only ever carry
    // metadata (filename/content type/size), so opening a chat with a long image history doesn't
    // mean downloading every image up front.
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

        msg.Body = newBody;
        msg.EditedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();

        await Clients.Group(msg.ChatId).SendAsync("MessageEdited", msg.ChatId, msg.Id, newBody, msg.EditedAtUtc);
    }

    public async Task DeleteMessage(long messageId)
    {
        var me = RequireUserId();

        await using var db = await _dbFactory.CreateDbContextAsync();
        var msg = await db.Messages.FindAsync(messageId) ?? throw new HubException("Message not found.");
        if (!Ci.Equals(msg.SenderUserId, me)) throw new HubException("You can only delete your own messages.");

        msg.IsDeleted = true;
        msg.Body = string.Empty;
        await db.SaveChangesAsync();

        await Clients.Group(msg.ChatId).SendAsync("MessageDeleted", msg.ChatId, msg.Id);
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

        return rows.Select(m => new ChatMessageDto(
            m.Id,
            m.SenderDisplayName,
            m.IsDeleted ? string.Empty : m.Body,
            m.SentAtUtc,
            m.EditedAtUtc,
            m.IsDeleted,
            reactionsByMessage.TryGetValue(m.Id, out var reactions) ? reactions : Array.Empty<ReactionDto>(),
            m.AttachmentFileName,
            m.AttachmentContentType,
            m.AttachmentSizeBytes
        )).ToList();
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

        var list = new List<ChatSummary>();
        foreach (var id in ids)
        {
            var lastRead = lastReadByChat.TryGetValue(id, out var lr) ? lr : 0L;
            var unread = await db.Messages.CountAsync(m => m.ChatId == id && m.Id > lastRead && m.SenderUserId != userId);
            list.Add(new ChatSummary(id, ComputeChatLabelForUser(id, userId), unread, mutedIds.Contains(id)));
        }

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
            _chatMembers[group.Key] = new HashSet<string>(group.Select(m => m.UserId));
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
                db.ChatMembers.Add(new ChatMemberEntity { ChatId = chatId, UserId = uid });
            await db.SaveChangesAsync();
        }
        catch
        {
            // Best-effort, same as the other persistence helpers: the in-memory group still
            // works for this run even if it doesn't survive a restart.
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

    // Returns the new row's id (needed so the live broadcast can carry an id clients can later
    // use to edit/delete/react to this exact message), or 0 if persistence failed - the message
    // still gets delivered live in that case, it just won't be editable/reactable this run.
    private async Task<long> PersistMessageAsync(string chatId, string senderUserId, string senderDisplayName, string body, DateTime sentAtUtc)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var entity = new ChatMessageEntity
            {
                ChatId = chatId,
                SenderUserId = senderUserId,
                SenderDisplayName = senderDisplayName,
                Body = body,
                SentAtUtc = sentAtUtc
            };
            db.Messages.Add(entity);
            await db.SaveChangesAsync();
            return entity.Id;
        }
        catch
        {
            return 0;
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

    // Also re-checks current membership, not just "were they the original creator": if the
    // creator left the group, they lose admin rights along with everything else membership grants.
    private void RequireGroupAdmin(string chatId, string userId)
    {
        if (!_groupChatCreator.TryGetValue(chatId, out var creator) || !Ci.Equals(creator, userId))
            throw new HubException("Only the group's creator can do that.");

        RequireMembership(chatId, userId);
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
