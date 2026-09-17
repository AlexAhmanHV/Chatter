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
using Chatter.Shared.Models;

namespace Chatter.Client.Services;

public class ChatService
{
    /* Fields & connection state
       Holds the auth provider, SignalR connection handle, and quick helpers like IsConnected.
       The BaseUrl is supplied by the caller (ViewModel) to StartAsync.
    */
    private readonly ApiAuthService _auth;
    private HubConnection? _conn;
    private const string LobbyId = "Lobby";

    public Func<string?>? OnGetCurrentDisplayName { get; set; }
    public bool IsConnected => _conn?.State == HubConnectionState.Connected;

    /* UI-facing events
       These events decouple network events from the ViewModel/UI.
       The ViewModel subscribes to update typing indicators, rosters, message lists, and presence.
    */
    public event EventHandler<(string ChannelId, string User, bool IsTyping)>? TypingChanged;
    public event EventHandler<(string ChannelId, string User, bool IsRecording)>? RecordingChanged;
    public event Action<string, string>? OtherDisplayNameChanged;
    public event Action<IReadOnlyList<string>>? OnlineUsersUpdated;
    public event Action<IReadOnlyList<ChatSummary>>? ChatsForMeUpdated;
    public event Action<IReadOnlyList<string>>? ChatsUpdated;

    // (chatId, messageId, senderDisplayName, body, sentAtUtc, attachment, isForwarded, replyTo).
    // messageId is 0 for synthetic lines that aren't a real persisted message (rename/system
    // notices); attachment/replyTo are null for a plain, non-reply text message.
    public event Action<string, long, string, string, DateTime, AttachmentMetaDto?, bool, ReplyPreviewDto?>? ChatMessageReceived;
    public event Action<string, string>? AddedChat;
    public event Action<string, long, string, string, DateTime, AttachmentMetaDto?, bool, ReplyPreviewDto?>? DmNotify;

    // Server feature: aliases + presence snapshots/deltas
    public event Action<Dictionary<string, string>>? NameAliasesReceived;
    public event Action<Dictionary<string, string>>? StatusesUpdated;
    public event Action<string, string>? StatusChanged;

    // Message editing/deletion, reactions, read receipts
    public event Action<string, long, string, DateTime>? MessageEdited;      // chatId, messageId, newBody, editedAtUtc
    public event Action<string, long>? MessageDeleted;                       // chatId, messageId
    public event Action<string, long, string, int, bool, string>? ReactionChanged; // chatId, messageId, emoji, count, added, byDisplayName
    public event Action<string, string, long>? ReadReceipt;                  // chatId, fromDisplayName, lastReadMessageId

    // Pinning
    public event Action<string, long, string>? MessagePinned;   // chatId, messageId, pinnedByDisplayName
    public event Action<string, long>? MessageUnpinned;         // chatId, messageId

    // Call signaling (1:1 DMs only - see ChatHub's own comment for why). The server never reads
    // an SDP/ICE payload's contents, it's a dumb relay - see CallViewModel for what these mean.
    public event Action<string, string, string, string>? IncomingCall;  // chatId, fromDisplayName, kind, sdpOffer
    public event Action<string, string>? CallAnswered;                  // chatId, sdpAnswer
    public event Action<string>? CallDeclined;                          // chatId
    public event Action<string, string>? CallIceCandidateReceived;      // chatId, candidateJson
    public event Action<string>? CallEnded;                             // chatId

    // Group admin (add/remove member, rename)
    public event Action<string>? RemovedFromChat;               // chatId - you were kicked or the group no longer includes you
    public event Action<string, string>? ChatRenamed;           // chatId, newLabel

    // Someone's profile picture changed - re-resolve their avatar URL (see GetAvatarUrlsAsync).
    public event Action<string, long>? AvatarChanged;           // displayName, version (cache-bust)

    // The caller's own account was just banned by a site admin (ChatHub.BanUser) - see the
    // "Banned" handler above for why this is cooperative rather than a forced disconnect.
    public event Action? Banned;

    /* Constructor
       Stores the auth dependency used to supply an access token when establishing the hub connection.
    */
    public ChatService(ApiAuthService auth) => _auth = auth;

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

        _conn.On<string, string, bool>("RecordingVoiceMessage", (channelId, user, isRecording) =>
        {
            var payload = (ChannelId: channelId, User: user, IsRecording: isRecording);
            RecordingChanged?.Invoke(this, payload);
        });

        // ----- Handlers: Presence -----
        _conn.On<Dictionary<string, string>>("Statuses", dict =>
            StatusesUpdated?.Invoke(dict ?? new()));

        _conn.On<string, string>("StatusChanged", (displayName, status) =>
            StatusChanged?.Invoke(displayName, status));

        // ----- Handlers: Chat metadata (server-owned chat IDs + labels) -----
        _conn.On<List<ChatSummary>>("ChatsForMe", list =>
            ChatsForMeUpdated?.Invoke((list ?? new()).AsReadOnly()));

        // ----- Handlers: Name change notifications -----
        // messageId 0 and a null attachment: these are synthesized locally, not a real
        // persisted ChatMessageDto row.
        _conn.On<string, string>("DisplayNameChanged", (oldName, newName) =>
            ChatMessageReceived?.Invoke(LobbyId, 0, "system",
                $"{oldName} changed their name to “{newName}”.", DateTime.UtcNow, null, false, null));
        _conn.On<string, string>("DisplayNameChanged", (oldName, newName) =>
            OtherDisplayNameChanged?.Invoke(oldName, newName));

        _conn.On<string>("LobbySystemMessage", text =>
            ChatMessageReceived?.Invoke(LobbyId, 0, "system", text, DateTime.UtcNow, null, false, null));

        // Same idea as LobbySystemMessage, but for any chat (used for group membership
        // add/remove notices) - carries its own chatId instead of assuming Lobby.
        _conn.On<string, string>("ChatSystemMessage", (chatId, text) =>
            ChatMessageReceived?.Invoke(chatId, 0, "system", text, DateTime.UtcNow, null, false, null));

        _conn.On<string>("RemovedFromChat", chatId => RemovedFromChat?.Invoke(chatId));
        _conn.On<string, string>("ChatRenamed", (chatId, newLabel) => ChatRenamed?.Invoke(chatId, newLabel));
        _conn.On<string, long>("AvatarChanged", (displayName, version) => AvatarChanged?.Invoke(displayName, version));

        // Sent by ChatHub.BanUser to any of the target's live connections. Cooperative only - see
        // BanUser's own comment on why this can't forcibly sever the connection from the server
        // side - so a well-behaved client (this one) reacts by disconnecting and signing out.
        _conn.On("Banned", () => Banned?.Invoke());

        // ----- Handlers: Rosters & chat lists -----
        _conn.On<List<string>>("OnlineUsers", list =>
            OnlineUsersUpdated?.Invoke((list ?? new()).AsReadOnly()));

        _conn.On<List<string>>("ChatsUpdated", list =>
            ChatsUpdated?.Invoke((list ?? new()).AsReadOnly()));

        // ----- Handlers: Per-chat messages -----
        _conn.On<string, long, string, string, DateTime, AttachmentMetaDto?, bool, ReplyPreviewDto?>("ReceiveChatMessage",
            (chatId, messageId, user, msg, sentAt, attachment, isForwarded, replyTo) =>
                ChatMessageReceived?.Invoke(chatId, messageId, user, msg, sentAt, attachment, isForwarded, replyTo));

        _conn.On<string, long, string, DateTime>("MessageEdited", (chatId, messageId, newBody, editedAt) =>
            MessageEdited?.Invoke(chatId, messageId, newBody, editedAt));

        _conn.On<string, long>("MessageDeleted", (chatId, messageId) =>
            MessageDeleted?.Invoke(chatId, messageId));

        _conn.On<string, long, string>("MessagePinned", (chatId, messageId, pinnedBy) =>
            MessagePinned?.Invoke(chatId, messageId, pinnedBy));

        _conn.On<string, long>("MessageUnpinned", (chatId, messageId) =>
            MessageUnpinned?.Invoke(chatId, messageId));

        _conn.On<string, string, string, string>("IncomingCall", (chatId, fromDisplayName, kind, sdpOffer) =>
            IncomingCall?.Invoke(chatId, fromDisplayName, kind, sdpOffer));

        _conn.On<string, string>("CallAnswered", (chatId, sdpAnswer) =>
            CallAnswered?.Invoke(chatId, sdpAnswer));

        _conn.On<string>("CallDeclined", chatId => CallDeclined?.Invoke(chatId));

        _conn.On<string, string>("CallIceCandidate", (chatId, candidateJson) =>
            CallIceCandidateReceived?.Invoke(chatId, candidateJson));

        _conn.On<string>("CallEnded", chatId => CallEnded?.Invoke(chatId));

        _conn.On<string, long, string, int, bool, string>("ReactionChanged",
            (chatId, messageId, emoji, count, added, byDisplayName) =>
                ReactionChanged?.Invoke(chatId, messageId, emoji, count, added, byDisplayName));

        _conn.On<string, string, long>("ReadReceipt", (chatId, fromDisplayName, lastReadMessageId) =>
            ReadReceipt?.Invoke(chatId, fromDisplayName, lastReadMessageId));

        // ----- Handlers: DM/group helpers -----
        _conn.On<string, string>("AddedChat", (chatId, label) =>
            AddedChat?.Invoke(chatId, label));

        _conn.On<string, long, string, string, DateTime, AttachmentMetaDto?, bool, ReplyPreviewDto?>("DmNotify",
            (chatId, messageId, fromUser, msg, sentAt, attachment, isForwarded, replyTo) =>
                DmNotify?.Invoke(chatId, messageId, fromUser, msg, sentAt, attachment, isForwarded, replyTo));

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
    public Task SendTypingAsync(string channelId, bool isTyping) =>
        _conn?.InvokeAsync("Typing", channelId, isTyping) ?? Task.CompletedTask;

    public Task SendRecordingAsync(string channelId, bool isRecording) =>
        _conn?.InvokeAsync("SetRecordingVoiceMessage", channelId, isRecording) ?? Task.CompletedTask;

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
    public async Task<IReadOnlyList<ChatSummary>> GetMyChatsAsync()
    {
        if (_conn is null) return Array.Empty<ChatSummary>();
        var list = await _conn.InvokeAsync<List<ChatSummary>>("GetMyChats");
        return (list ?? new()).AsReadOnly();
    }

    public async Task<IReadOnlyList<ChatMessageDto>> GetChatHistoryAsync(string chatId, long? beforeMessageId = null, int take = 50)
    {
        if (_conn is null) return Array.Empty<ChatMessageDto>();
        var list = await _conn.InvokeAsync<List<ChatMessageDto>>("GetChatHistory", chatId, beforeMessageId, take);
        return (list ?? new()).AsReadOnly();
    }

    public async Task<IReadOnlyList<ChatMessageDto>> SearchMessagesAsync(string chatId, string query, int take = 50)
    {
        if (_conn is null) return Array.Empty<ChatMessageDto>();
        var list = await _conn.InvokeAsync<List<ChatMessageDto>>("SearchMessages", chatId, query, take);
        return (list ?? new()).AsReadOnly();
    }

    public async Task<IReadOnlyList<GlobalSearchResultDto>> SearchAllChatsAsync(string query, int take = 50)
    {
        if (_conn is null) return Array.Empty<GlobalSearchResultDto>();
        var list = await _conn.InvokeAsync<List<GlobalSearchResultDto>>("SearchAllChats", query, take);
        return (list ?? new()).AsReadOnly();
    }

    public Task<string?> CreateGroupChatAsync(string name, List<string> memberDisplayNames) =>
        _conn is null
            ? Task.FromResult<string?>(null)
            : _conn.InvokeAsync<string?>("CreateGroupChat", name, memberDisplayNames);

    public Task EditMessageAsync(long messageId, string newBody) =>
        _conn?.SendAsync("EditMessage", messageId, newBody) ?? Task.CompletedTask;

    public async Task<IReadOnlyList<MessageEditHistoryDto>> GetMessageEditHistoryAsync(long messageId)
    {
        if (_conn is null) return Array.Empty<MessageEditHistoryDto>();
        var list = await _conn.InvokeAsync<List<MessageEditHistoryDto>>("GetMessageEditHistory", messageId);
        return (list ?? new()).AsReadOnly();
    }

    public Task DeleteMessageAsync(long messageId) =>
        _conn?.SendAsync("DeleteMessage", messageId) ?? Task.CompletedTask;

    public Task PinMessageAsync(long messageId) =>
        _conn?.SendAsync("PinMessage", messageId) ?? Task.CompletedTask;

    public Task UnpinMessageAsync(long messageId) =>
        _conn?.SendAsync("UnpinMessage", messageId) ?? Task.CompletedTask;

    public async Task<IReadOnlyList<ChatMessageDto>> GetPinnedMessagesAsync(string chatId)
    {
        if (_conn is null) return Array.Empty<ChatMessageDto>();
        var list = await _conn.InvokeAsync<List<ChatMessageDto>>("GetPinnedMessages", chatId);
        return (list ?? new()).AsReadOnly();
    }

    public Task ToggleReactionAsync(long messageId, string emoji) =>
        _conn?.SendAsync("ToggleReaction", messageId, emoji) ?? Task.CompletedTask;

    public Task MarkReadAsync(string chatId, long lastReadMessageId) =>
        _conn?.SendAsync("MarkRead", chatId, lastReadMessageId) ?? Task.CompletedTask;

    public async Task<Dictionary<string, long>> GetChatReadReceiptsAsync(string chatId)
    {
        if (_conn is null) return new();
        var dict = await _conn.InvokeAsync<Dictionary<string, long>>("GetChatReadReceipts", chatId);
        return dict ?? new();
    }

    public Task SetChatMutedAsync(string chatId, bool muted) =>
        _conn?.SendAsync("SetChatMuted", chatId, muted) ?? Task.CompletedTask;

    public Task SetChatPinnedAsync(string chatId, bool pinned) =>
        _conn?.SendAsync("SetChatPinned", chatId, pinned) ?? Task.CompletedTask;

    /* Call signaling */
    public Task CallInviteAsync(string chatId, string kind, string sdpOffer) =>
        _conn?.InvokeAsync("CallInvite", chatId, kind, sdpOffer) ?? Task.CompletedTask;

    public Task CallAnswerAsync(string chatId, string sdpAnswer) =>
        _conn?.SendAsync("CallAnswer", chatId, sdpAnswer) ?? Task.CompletedTask;

    public Task CallDeclineAsync(string chatId) =>
        _conn?.SendAsync("CallDecline", chatId) ?? Task.CompletedTask;

    public Task CallIceCandidateAsync(string chatId, string candidateJson) =>
        _conn?.SendAsync("CallIceCandidate", chatId, candidateJson) ?? Task.CompletedTask;

    public Task CallHangupAsync(string chatId) =>
        _conn?.SendAsync("CallHangup", chatId) ?? Task.CompletedTask;

    /* Blocking */
    public Task BlockUserAsync(string displayName) =>
        _conn?.SendAsync("BlockUser", displayName) ?? Task.CompletedTask;

    public Task UnblockUserAsync(string displayName) =>
        _conn?.SendAsync("UnblockUser", displayName) ?? Task.CompletedTask;

    public async Task<IReadOnlyList<string>> GetBlockedUsersAsync()
    {
        if (_conn is null) return Array.Empty<string>();
        var list = await _conn.InvokeAsync<List<string>>("GetBlockedUsers");
        return (list ?? new()).AsReadOnly();
    }

    /* Moderation (site admin) */
    public Task ReportMessageAsync(long messageId, string reason) =>
        _conn?.SendAsync("ReportMessage", messageId, reason) ?? Task.CompletedTask;

    public async Task<IReadOnlyList<ReportDto>> GetReportsAsync()
    {
        if (_conn is null) return Array.Empty<ReportDto>();
        var list = await _conn.InvokeAsync<List<ReportDto>>("GetReports");
        return (list ?? new()).AsReadOnly();
    }

    public Task DismissReportAsync(long reportId) =>
        _conn?.SendAsync("DismissReport", reportId) ?? Task.CompletedTask;

    public Task BanUserAsync(string displayName) =>
        _conn?.SendAsync("BanUser", displayName) ?? Task.CompletedTask;

    public Task UnbanUserAsync(string displayName) =>
        _conn?.SendAsync("UnbanUser", displayName) ?? Task.CompletedTask;

    /* Calls: ICE server config (STUN + optional TURN - see ChatHub.GetIceServers) */
    public async Task<IReadOnlyList<IceServerDto>> GetIceServersAsync()
    {
        if (_conn is null) return Array.Empty<IceServerDto>();
        var list = await _conn.InvokeAsync<List<IceServerDto>>("GetIceServers");
        return (list ?? new()).AsReadOnly();
    }

    /* Group admin */
    public async Task<IReadOnlyList<string>> GetGroupMembersAsync(string chatId)
    {
        if (_conn is null) return Array.Empty<string>();
        var list = await _conn.InvokeAsync<List<string>>("GetGroupMembers", chatId);
        return (list ?? new()).AsReadOnly();
    }

    public Task AddGroupMemberAsync(string chatId, string displayName) =>
        _conn?.SendAsync("AddGroupMember", chatId, displayName) ?? Task.CompletedTask;

    public Task RemoveGroupMemberAsync(string chatId, string displayName) =>
        _conn?.SendAsync("RemoveGroupMember", chatId, displayName) ?? Task.CompletedTask;

    public Task RenameGroupChatAsync(string chatId, string newName) =>
        _conn?.SendAsync("RenameGroupChat", chatId, newName) ?? Task.CompletedTask;

    public Task PromoteGroupAdminAsync(string chatId, string displayName) =>
        _conn?.SendAsync("PromoteGroupAdmin", chatId, displayName) ?? Task.CompletedTask;

    public Task DemoteGroupAdminAsync(string chatId, string displayName) =>
        _conn?.SendAsync("DemoteGroupAdmin", chatId, displayName) ?? Task.CompletedTask;

    public async Task<IReadOnlyList<string>> GetGroupAdminsAsync(string chatId)
    {
        if (_conn is null) return Array.Empty<string>();
        var list = await _conn.InvokeAsync<List<string>>("GetGroupAdmins", chatId);
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

    public Task<string?> SendDmFirstAsync(string otherDisplayName, string message) =>
        _conn is null
            ? Task.FromResult<string?>(null)
            : _conn.InvokeAsync<string?>("SendDmFirst", otherDisplayName, message);

    public Task SendToChatAsync(string chatId, string message, long? replyToMessageId = null) =>
        _conn?.SendAsync("SendToChat", chatId, message, replyToMessageId) ?? Task.CompletedTask;

    /* Attachments (images, video) and voice messages */
    public Task<long> SendAttachmentAsync(string chatId, string fileName, string contentType, byte[] data, string? caption, long? replyToMessageId = null) =>
        _conn is null
            ? Task.FromResult(0L)
            : _conn.InvokeAsync<long>("SendAttachment", chatId, fileName, contentType, data, caption, replyToMessageId);

    public Task<long> SendVoiceMessageAsync(string chatId, string contentType, byte[] data, int durationSeconds, long? replyToMessageId = null) =>
        _conn is null
            ? Task.FromResult(0L)
            : _conn.InvokeAsync<long>("SendVoiceMessage", chatId, contentType, data, durationSeconds, replyToMessageId);

    public Task<long> SendVideoAsync(string chatId, string fileName, string contentType, byte[] data, int? durationSeconds = null, long? replyToMessageId = null) =>
        _conn is null
            ? Task.FromResult(0L)
            : _conn.InvokeAsync<long>("SendVideo", chatId, fileName, contentType, data, durationSeconds, replyToMessageId);

    public Task<long> SendFileAsync(string chatId, string fileName, string contentType, byte[] data, long? replyToMessageId = null) =>
        _conn is null
            ? Task.FromResult(0L)
            : _conn.InvokeAsync<long>("SendFile", chatId, fileName, contentType, data, replyToMessageId);

    public async Task<AttachmentDataDto?> GetAttachmentDataAsync(long messageId)
    {
        if (_conn is null) return null;
        return await _conn.InvokeAsync<AttachmentDataDto>("GetAttachmentData", messageId);
    }

    public async Task<LinkPreviewDto?> GetLinkPreviewAsync(string url)
    {
        if (_conn is null) return null;
        try { return await _conn.InvokeAsync<LinkPreviewDto?>("GetLinkPreview", url); }
        catch { return null; } // best-effort - a failed preview just means no card shows
    }

    public Task<long> ForwardMessageAsync(long messageId, string targetChatId) =>
        _conn is null
            ? Task.FromResult(0L)
            : _conn.InvokeAsync<long>("ForwardMessage", messageId, targetChatId);

    /* Last seen (offline users only - an online user has no entry, see GetLastSeen on the hub) */
    public async Task<Dictionary<string, DateTime>> GetLastSeenAsync(List<string> displayNames)
    {
        if (_conn is null) return new();
        var dict = await _conn.InvokeAsync<Dictionary<string, DateTime>>("GetLastSeen", displayNames);
        return dict ?? new();
    }

    /* Avatars */
    public Task UpdateAvatarAsync(byte[] data, string contentType) =>
        _conn?.SendAsync("UpdateAvatar", data, contentType) ?? Task.CompletedTask;

    public async Task<Dictionary<string, string>> GetAvatarUrlsAsync(List<string> displayNames)
    {
        if (_conn is null) return new();
        var dict = await _conn.InvokeAsync<Dictionary<string, string>>("GetAvatarUrls", displayNames);
        return dict ?? new();
    }

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
