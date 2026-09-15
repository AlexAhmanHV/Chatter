/*
File: ChatViewModel.cs

What this does:
- Purpose: The main view-model driving the chat screen. It orchestrates connection lifecycle, chat lists, messages,
  presence/roster, typing indicators, and UI commands (send, start DM, delete, emoji help/picker, edit/delete/react,
  group chat creation, paginated history).
- How: Subscribes to ChatService events for real-time updates, maintains UI-facing observable collections/properties,
  and wraps server calls with small helpers (name canonicalization, placeholder logic, etc.).
*/

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Chatter.Client.Messages;
using Chatter.Client.Models;
using Chatter.Client.Services;
using Chatter.Client.Views;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Media;
using Microsoft.Maui.Storage;
using Plugin.Maui.Audio;
using Chatter.Client.Helpers;
using Chatter.Shared.Models;

namespace Chatter.Client.ViewModels;

public partial class ChatViewModel : ObservableObject
{
    /* Core services & constants */
    private readonly ChatService _chat;
    private readonly IAudioManager _audio;

    // De-dup only for synthetic lines (rename/system notices) that have no server-assigned id.
    // Real messages are de-duped by id instead - see ChatMessageReceived/DmNotify below.
    private readonly Dictionary<string, string?> _lastSystemLineByChat = new(StringComparer.OrdinalIgnoreCase);

    // Group-chat "seen by N/M" support: cached membership (fetched once per chat, on selection)
    // and each member's last-read message id (bootstrapped via GetChatReadReceiptsAsync, then
    // kept current from live ReadReceipt events - see the constructor's _chat.ReadReceipt handler).
    private readonly Dictionary<string, List<string>> _groupMembersByChatId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, long>> _lastReadByChatAndUser = new(StringComparer.OrdinalIgnoreCase);

    /* Root page helper to avoid obsolete Application.MainPage */
    private static Page? GetRootPage() => Application.Current?.Windows?.FirstOrDefault()?.Page;

    /* User input & typing indicator */
    [ObservableProperty] public partial string User { get; set; } = string.Empty;
    [ObservableProperty] public partial string? OutgoingMessage { get; set; }
    [ObservableProperty] public partial string MessagePlaceholder { get; set; } = "Type a message…";

    private CancellationTokenSource? _typingCts;
    [ObservableProperty] public partial bool IsPeerTyping { get; set; }
    [ObservableProperty] public partial string? TypingUser { get; set; }
    [ObservableProperty] public partial bool IsPeerRecordingVoiceMessage { get; set; }
    [ObservableProperty] public partial string? RecordingUser { get; set; }

    // Channel used for typing before a chat is selected.
    private string CurrentChannelId => SelectedChat?.Id ?? "Lobby";

    /* People/roster data */
    public ObservableCollection<UserPresenceItem> People { get; } = new();
    private readonly HashSet<string> _knownUsers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _hiddenChats = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PresenceStatus> _statusByName = new(StringComparer.OrdinalIgnoreCase);
    [ObservableProperty] public partial bool IsActive { get; set; }

    /* Chats & messages */
    public ObservableCollection<ChatItem> Chats { get; } = new();
    [ObservableProperty] public partial ChatItem? SelectedChat { get; set; }
    public ObservableCollection<ChatMessageItem> CurrentChatMessages { get; } = new();
    private readonly Dictionary<string, ObservableCollection<ChatMessageItem>> _chatMessages =
        new(StringComparer.OrdinalIgnoreCase);

    /* Paginated history */
    private const int HistoryPageSize = 50;
    private readonly HashSet<string> _historyLoaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _noMoreHistory = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _oldestLoadedMessageId = new(StringComparer.OrdinalIgnoreCase);
    [ObservableProperty] public partial bool CanLoadMoreHistory { get; set; }

    public ObservableCollection<string> OnlineUsers { get; } = new();

    /* Computed properties */
    public bool CanSend => !string.IsNullOrWhiteSpace(OutgoingMessage) && SelectedChat != null;
    public int OfflineCount => Math.Max(0, People.Count - OnlineUsers.Count);

    /* Commands */
    public IAsyncRelayCommand ConnectCommand { get; }
    public IAsyncRelayCommand SendCommand { get; }
    public IAsyncRelayCommand<string> StartDmCommand { get; }
    public IRelayCommand<ChatItem> DeleteChatCommand { get; }
    public IAsyncRelayCommand<PresenceStatus> SetMyStatusCommand { get; }
    public IAsyncRelayCommand<ChatMessageItem> EditMessageCommand { get; }
    public IAsyncRelayCommand<ChatMessageItem> DeleteMessageCommand { get; }
    public IAsyncRelayCommand<ChatMessageItem> ReactCommand { get; }
    public IAsyncRelayCommand<ReactionItem> ToggleReactionCommand { get; }
    public IAsyncRelayCommand CreateGroupChatCommand { get; }
    public IAsyncRelayCommand LoadMoreHistoryCommand { get; }
    public IAsyncRelayCommand ManageChatCommand { get; }
    public IAsyncRelayCommand<ChatMessageItem> ViewAttachmentCommand { get; }
    public IAsyncRelayCommand PickAndSendAttachmentCommand { get; }
    public IAsyncRelayCommand<ChatMessageItem> PlayVoiceMessageCommand { get; }
    public IAsyncRelayCommand RecordVoiceMessageCommand { get; }
    public IAsyncRelayCommand<ChatMessageItem> ForwardMessageCommand { get; }
    public IRelayCommand<ChatMessageItem> ReplyToMessageCommand { get; }
    public IRelayCommand CancelReplyCommand { get; }
    public IAsyncRelayCommand<ChatMessageItem> JumpToRepliedMessageCommand { get; }
    public IAsyncRelayCommand<ChatMessageItem> JumpToMessageCommand { get; }
    public IAsyncRelayCommand<ChatMessageItem> TogglePinCommand { get; }
    public IAsyncRelayCommand<ChatMessageItem> ViewEditHistoryCommand { get; }
    public IAsyncRelayCommand PickAndSendVideoCommand { get; }
    public IAsyncRelayCommand<ChatMessageItem> PlayVideoCommand { get; }

    // Pinned messages for whichever chat is currently selected - refreshed on selection
    // (LoadPinnedMessagesAsync) and kept current from live MessagePinned/MessageUnpinned events.
    public ObservableCollection<ChatMessageItem> PinnedMessages { get; } = new();
    public bool HasPinnedMessages => PinnedMessages.Count > 0;
    public IRelayCommand ToggleSearchCommand { get; }

    // The message the composer will attach as a reply when the next SendCommand/attachment/voice
    // message goes out - null means "not replying to anything". Cleared after every send attempt
    // (successful or not) so a failed send doesn't leave a stale reply banner pointing at the
    // wrong thing after the user's tried something else.
    [ObservableProperty] public partial ChatMessageItem? PendingReply { get; set; }
    public bool IsReplying => PendingReply is not null;
    partial void OnPendingReplyChanged(ChatMessageItem? value) => OnPropertyChanged(nameof(IsReplying));
    public IAsyncRelayCommand<ChatMessageItem> JumpToSearchResultCommand { get; }

    /* In-chat message search */
    [ObservableProperty] public partial bool IsSearching { get; set; }
    [ObservableProperty] public partial string? SearchQuery { get; set; }
    [ObservableProperty] public partial bool IsSearchRunning { get; set; }
    public ObservableCollection<ChatMessageItem> SearchResults { get; } = new();
    private CancellationTokenSource? _searchCts;

    /* Voice message recording state */
    [ObservableProperty] public partial bool IsRecordingVoiceMessage { get; set; }
    private IAudioRecorder? _voiceRecorder;
    private IAudioPlayer? _voicePlayer;
    private ChatMessageItem? _playingVoiceMessageItem;
    private IDispatcherTimer? _voicePlaybackProgressTimer;

    private static readonly string[] QuickReactionEmojis = { "👍", "❤️", "😂", "🎉", "😮", "😢" };

    /* Chat ID helpers */
    private static bool IsDraftId(string id) => id.StartsWith("draft:", StringComparison.OrdinalIgnoreCase);
    private static string DraftOf(string other) => $"draft:{other}";
    private static string DraftLabel(string other) => $"{other} (draft)";
    private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;

    /* Name canonicalization & alias history */
    private readonly Dictionary<string, string> _nameAliases = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _aliasHistoryByCurrent =
        new(StringComparer.OrdinalIgnoreCase);

    private List<string> GetHistoryList(string current)
    {
        if (!_aliasHistoryByCurrent.TryGetValue(current, out var list))
        {
            list = new List<string>();
            _aliasHistoryByCurrent[current] = list;
        }
        return list;
    }

    /* Emoji helper & quick actions */
    [RelayCommand]
    private async Task ShowEmojiHelpAsync()
    {
        var page = GetRootPage();
        if (page is not null)
            await page.Navigation.PushModalAsync(new EmojiHelpPage());
    }

    [RelayCommand]
    private async Task ShowEmojiPickerAsync()
    {
        var page = GetRootPage();
        if (page is not null)
            await page.Navigation.PushModalAsync(new EmojiPickerPage(this));
    }

    /* Presence (self) */
    [ObservableProperty] public partial PresenceStatus MyStatus { get; set; } = PresenceStatus.Online;
    private bool _suppressStatusSend;

    private long _lastRenameTicks;
    private string? _lastRenameValue;
    private int _isHandlingRename;

    partial void OnMyStatusChanged(PresenceStatus value)
    {
        if (_suppressStatusSend) return;
        _ = SetMyStatusAsync(value);
    }

    private void SetMyStatusFromServer(PresenceStatus s)
    {
        _suppressStatusSend = true;
        try { MyStatus = s; }
        finally { _suppressStatusSend = false; }
    }

    private static PresenceStatus ParseStatus(string s) => (s ?? "").ToLowerInvariant() switch
    {
        "busy" => PresenceStatus.Busy,
        "away" => PresenceStatus.Away,
        "online" => PresenceStatus.Online,
        _ => PresenceStatus.Offline
    };
    private static string StatusToWire(PresenceStatus s) => s switch
    {
        PresenceStatus.Busy => "busy",
        PresenceStatus.Away => "away",
        PresenceStatus.Online => "online",
        _ => "offline"
    };

    /* Canonicalization & display name formatting */
    private string Canon(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return name;

        string cur = name;
        var seen = new HashSet<string>(Ci) { cur };

        while (_nameAliases.TryGetValue(cur, out var next) && !Ci.Equals(next, cur) && !seen.Contains(next))
        {
            seen.Add(next);
            cur = next;
        }
        foreach (var s in seen) _nameAliases[s] = cur;
        return cur;
    }

    private string ResolveName(string name) => Canon(name);

    private string FormatNameWithAliases(string anyName)
    {
        var current = Canon(anyName);
        if (_aliasHistoryByCurrent.TryGetValue(current, out var hist) && hist.Count > 0)
        {
            var previous = hist.Where(n => !Ci.Equals(n, current)).Distinct(Ci).ToList();
            return previous.Count > 0 ? $"{current} ({string.Join(", ", previous)})" : current;
        }
        return current;
    }

    private void RenameKnownUser(string oldName, string newName)
    {
        if (Ci.Equals(oldName, newName)) return;

        var oldCur = Canon(oldName);
        var newCur = Canon(newName);
        if (Ci.Equals(oldCur, newCur)) return;

        _nameAliases[oldCur] = newCur;

        var oldHist = _aliasHistoryByCurrent.TryGetValue(oldCur, out var oh) ? oh : new List<string>();
        var newHist = _aliasHistoryByCurrent.TryGetValue(newCur, out var nh) ? nh : new List<string>();
        foreach (var s in oldHist) if (!newHist.Any(x => Ci.Equals(x, s))) newHist.Add(s);
        if (!newHist.Any(x => Ci.Equals(x, oldCur))) newHist.Add(oldCur);
        _aliasHistoryByCurrent[newCur] = newHist;
        _aliasHistoryByCurrent.Remove(oldCur);

        if (_statusByName.Remove(oldCur, out var st))
            _statusByName[newCur] = st;

        NormalizeKnownUsers();
        RecomputePeople(OnlineUsers);
        RelabelDmChatsFor(oldCur, newCur);

        UpdateMessagePlaceholder();
    }


    /* Typing indicator & CanSend updates */

    [ObservableProperty] private int charactersUsed;
    public string CharacterCounterText => $"Characters used {CharactersUsed}";

    partial void OnOutgoingMessageChanged(string? value)
    {
            CharactersUsed = value?.Length ?? 0;
            OnPropertyChanged(nameof(CharacterCounterText));

        if (string.IsNullOrWhiteSpace(value))
        {
            _ = NotifyTypingAsync(false);
            _typingCts?.Cancel();
            OnPropertyChanged(nameof(CanSend));
            return;
        }
        _ = NotifyTypingAsync(true);
        _typingCts?.Cancel();
        _typingCts = new CancellationTokenSource();
        _ = DelayedStopTypingAsync(_typingCts.Token);
        OnPropertyChanged(nameof(CanSend));
    }

    private async Task NotifyTypingAsync(bool isTyping)
    {
        try { await _chat.SendTypingAsync(CurrentChannelId, isTyping); }
        catch { }
    }
    private async Task DelayedStopTypingAsync(CancellationToken ct)
    {
        try { await Task.Delay(1500, ct); await NotifyTypingAsync(false); }
        catch (TaskCanceledException) { }
    }

    /* Constructor: event wiring & initial command setup */
    public ChatViewModel(ChatService chat, IAudioManager audio)
    {
        _chat = chat;
        _audio = audio;

        People.CollectionChanged += (_, __) => OnPropertyChanged(nameof(OfflineCount));
        PinnedMessages.CollectionChanged += (_, __) => OnPropertyChanged(nameof(HasPinnedMessages));
        OnlineUsers.CollectionChanged += (_, __) => OnPropertyChanged(nameof(OfflineCount));

        _chat.TypingChanged += (_, e) =>
        {
            if (e.ChannelId == CurrentChannelId && e.User != User)
            {
                IsPeerTyping = e.IsTyping;
                TypingUser = e.IsTyping ? e.User : null;
            }
        };

        _chat.RecordingChanged += (_, e) =>
        {
            if (e.ChannelId == CurrentChannelId && e.User != User)
            {
                IsPeerRecordingVoiceMessage = e.IsRecording;
                RecordingUser = e.IsRecording ? e.User : null;
            }
        };

        _chat.OtherDisplayNameChanged += (oldName, newName) =>
            MainThread.BeginInvokeOnMainThread(() => RenameKnownUser(oldName, newName));

        _chat.NameAliasesReceived += dict =>
        {
            if (dict is null || dict.Count == 0) return;
            MainThread.BeginInvokeOnMainThread(() =>
            {
                foreach (var kv in dict)
                {
                    var oldCur = Canon(kv.Key);
                    var newCur = Canon(kv.Value);
                    if (!Ci.Equals(oldCur, newCur))
                        _nameAliases[oldCur] = newCur;
                }
                NormalizeKnownUsers();
                RecomputePeople(OnlineUsers);
                foreach (var kv in dict)
                    RelabelDmChatsFor(Canon(kv.Key), Canon(kv.Value));
            });
        };

        try
        {
            _chat.StatusesUpdated += dict =>
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    foreach (var kv in dict)
                        _statusByName[Canon(kv.Key)] = ParseStatus(kv.Value);

                    HarmonizeSelfPresence();
                    RecomputePeople(OnlineUsers);
                });
        }
        catch { }

        try
        {
            _chat.StatusChanged += (name, status) =>
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    var canon = Canon(name);
                    _statusByName[canon] = ParseStatus(status);

                    if (Ci.Equals(canon, Canon(User)))
                        HarmonizeSelfPresence();

                    RecomputePeople(OnlineUsers);
                });
        }
        catch { }

        _chat.OnlineUsersUpdated += onlineList =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                foreach (var n in onlineList)
                    _knownUsers.Add(Canon(n));

                EnsureSelfKnownUser();
                HarmonizeSelfPresence();
                RecomputePeople(onlineList);
            });

        _chat.ChatsForMeUpdated += list =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                var shouldHave = new HashSet<string>(list.Select(c => c.Id), StringComparer.OrdinalIgnoreCase);

                foreach (var summary in list)
                {
                    if (_hiddenChats.Contains(summary.Id)) continue;
                    var chatItem = EnsureChatItemWithLabel(summary.Id, summary.Label, summary.UnreadCount);
                    chatItem.IsMuted = summary.IsMuted; // always synced, unlike Unread which is live-driven once created

                    // The server resolves DM labels to the *other* participant's current
                    // display name, so this is how the client learns about DM partners now
                    // that chat IDs are opaque (no more parsing "dm:name1|name2").
                    if (summary.Id.StartsWith("dm:", StringComparison.OrdinalIgnoreCase))
                        _knownUsers.Add(Canon(summary.Label));
                }

                for (int i = Chats.Count - 1; i >= 0; i--)
                {
                    var cid = Chats[i].Id;
                    if (IsDraftId(cid)) continue;
                    if (!shouldHave.Contains(cid))
                        Chats.RemoveAt(i);
                }

                var lobby = Chats.FirstOrDefault(c => string.Equals(c.Id, "Lobby", StringComparison.OrdinalIgnoreCase));
                if (lobby is not null && Chats.IndexOf(lobby) != 0)
                    Chats.Move(Chats.IndexOf(lobby), 0);

                if (SelectedChat is null && Chats.Count > 0)
                    SelectedChat = Chats[0];

                RecomputePeople(OnlineUsers);
                UpdateMessagePlaceholder();
            });

        _chat.AddedChat += (chatId, label) =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                _hiddenChats.Remove(chatId);
                EnsureChatItemWithLabel(chatId, label);
                if (chatId.StartsWith("dm:", StringComparison.OrdinalIgnoreCase))
                    _knownUsers.Add(Canon(label));
                RecomputePeople(OnlineUsers);
                _ = _chat.JoinChatAsync(chatId);
            });

        _chat.DmNotify += (chatId, messageId, fromUser, msg, sentAtUtc, attachment, isForwarded, replyTo) =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                _knownUsers.Add(Canon(fromUser));
                var chatItem = EnsureChatItemWithLabel(chatId, fromUser);

                if (!_chatMessages.TryGetValue(chatId, out var list))
                    _chatMessages[chatId] = list = new ObservableCollection<ChatMessageItem>();

                if (messageId > 0 && list.Any(x => x.Id == messageId)) return;

                var msgItem = new ChatMessageItem(messageId, fromUser, msg, sentAtUtc, isMine: false, isSystem: false,
                    attachment, isForwarded, replyTo);
                list.Add(msgItem);

                bool isViewingThis = IsActive && SelectedChat?.Id == chatId;
                if (isViewingThis)
                {
                    CurrentChatMessages.Add(msgItem);
                    MarkReadIfViewing(chatId, messageId);
                }
                else chatItem.Unread++;

                RecomputePeople(OnlineUsers);
                _ = _chat.JoinChatAsync(chatId);
            });

        _chat.ChatMessageReceived += (chatId, messageId, u, m, sentAtUtc, attachment, isForwarded, replyTo) =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (_hiddenChats.Remove(chatId))
                {
                    var label = _hiddenChatLabels.TryGetValue(chatId, out var saved) ? saved : u;
                    EnsureChatItemWithLabel(chatId, label);
                }

                var isSystem = string.Equals(u, "system", StringComparison.OrdinalIgnoreCase);

                if (!_chatMessages.TryGetValue(chatId, out var list))
                    _chatMessages[chatId] = list = new ObservableCollection<ChatMessageItem>();

                if (messageId > 0)
                {
                    if (list.Any(x => x.Id == messageId)) return;
                }
                else if (isSystem)
                {
                    if (_lastSystemLineByChat.TryGetValue(chatId, out var lastLine) && lastLine == m) return;
                    _lastSystemLineByChat[chatId] = m;
                }

                var item = new ChatMessageItem(messageId, u, m, sentAtUtc, isMine: Ci.Equals(u, User), isSystem: isSystem,
                    attachment, isForwarded, replyTo);
                list.Add(item);

                bool isViewingThis = IsActive && SelectedChat?.Id == chatId;
                if (isViewingThis)
                {
                    CurrentChatMessages.Add(item);
                    MarkReadIfViewing(chatId, messageId);
                }
                else EnsureChatItem(chatId).Unread++;
            });

        _chat.MessageEdited += (chatId, messageId, newBody, editedAt) =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                var msg = FindMessage(chatId, messageId);
                if (msg is null) return;
                msg.Body = newBody;
                msg.EditedAtUtc = editedAt;
            });

        _chat.MessageDeleted += (chatId, messageId) =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                var msg = FindMessage(chatId, messageId);
                if (msg is null) return;
                msg.IsDeleted = true;
                msg.Body = string.Empty;
                PinnedMessages.Remove(msg); // server auto-unpins a deleted message too
            });

        _chat.MessagePinned += (chatId, messageId, pinnedBy) =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                var msg = FindMessage(chatId, messageId);
                if (msg is null) return;
                msg.IsPinned = true;
                if (SelectedChat?.Id == chatId && !PinnedMessages.Contains(msg))
                    PinnedMessages.Add(msg);
            });

        _chat.MessageUnpinned += (chatId, messageId) =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                var msg = FindMessage(chatId, messageId);
                if (msg is not null) msg.IsPinned = false;
                if (SelectedChat?.Id == chatId)
                {
                    var pinned = PinnedMessages.FirstOrDefault(m => m.Id == messageId);
                    if (pinned is not null) PinnedMessages.Remove(pinned);
                }
            });

        _chat.ReactionChanged += (chatId, messageId, emoji, count, added, byDisplayName) =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                var msg = FindMessage(chatId, messageId);
                if (msg is null) return;

                var existing = msg.Reactions.FirstOrDefault(r => r.Emoji == emoji);
                var reactedByMe = Ci.Equals(byDisplayName, User) ? added : (existing?.ReactedByMe ?? false);

                if (count <= 0)
                {
                    if (existing is not null) msg.Reactions.Remove(existing);
                    return;
                }

                if (existing is null)
                {
                    var initialReactors = added ? new[] { byDisplayName } : Array.Empty<string>();
                    msg.Reactions.Add(new ReactionItem(messageId, emoji, count, reactedByMe, initialReactors));
                }
                else
                {
                    existing.Count = count;
                    existing.ReactedByMe = reactedByMe;
                    if (added) existing.AddReactor(byDisplayName);
                    else existing.RemoveReactor(byDisplayName);
                }
            });

        _chat.ReadReceipt += (chatId, fromDisplayName, lastReadMessageId) =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (!_lastReadByChatAndUser.TryGetValue(chatId, out var byUser))
                    _lastReadByChatAndUser[chatId] = byUser = new(Ci);
                byUser[fromDisplayName] = lastReadMessageId;

                if (!_chatMessages.TryGetValue(chatId, out var list)) return;

                if (chatId.StartsWith("group:", StringComparison.OrdinalIgnoreCase))
                {
                    RecomputeGroupSeenCounts(chatId);
                }
                else
                {
                    var latestMine = list.Where(m => m.IsMine && m.Id > 0).OrderByDescending(m => m.Id).FirstOrDefault();
                    foreach (var m in list.Where(m => m.IsMine)) m.SeenByOther = false;
                    if (latestMine is not null && latestMine.Id <= lastReadMessageId)
                        latestMine.SeenByOther = true;
                }
            });

        _chat.RemovedFromChat += chatId =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                _chatMessages.Remove(chatId);
                _hiddenChatLabels.Remove(chatId);
                var existing = Chats.FirstOrDefault(c => Ci.Equals(c.Id, chatId));
                if (existing is null) return;

                Chats.Remove(existing);
                if (SelectedChat?.Id == chatId)
                    SelectedChat = Chats.FirstOrDefault(c => Ci.Equals(c.Id, "Lobby")) ?? Chats.FirstOrDefault();
            });

        _chat.ChatRenamed += (chatId, newLabel) =>
            MainThread.BeginInvokeOnMainThread(() => EnsureChatItemWithLabel(chatId, newLabel));

        _chat.AvatarChanged += (displayName, version) =>
            MainThread.BeginInvokeOnMainThread(() => _ = RefreshAvatarAsync(displayName, version));

        // De-duped rename handler with hard return + presence harmonization
        WeakReferenceMessenger.Default.Register<DisplayNameChangedMessage>(this, async (_, msg) =>
        {
            var newName = msg.Value?.Trim() ?? string.Empty;
            if (Ci.Equals(newName, User)) return;

            var nowTicks = DateTime.UtcNow.Ticks;
            if (Ci.Equals(newName, _lastRenameValue) && nowTicks - _lastRenameTicks < TimeSpan.FromSeconds(2).Ticks)
                return;

            if (Interlocked.Exchange(ref _isHandlingRename, 1) == 1) return;
            _lastRenameTicks = nowTicks;
            _lastRenameValue = newName;

            try
            {
                var oldName = User;
                User = newName;

                await SafeSetDisplayNameOnServerAsync(User);

                if (!string.IsNullOrWhiteSpace(oldName) && !Ci.Equals(oldName, User))
                    RenameKnownUser(oldName, User);

                EnsureSelfKnownUser();
                HarmonizeSelfPresence();

                await HardReturnToLobbyUIAsync();

                UpdateMessagePlaceholder();
            }
            finally
            {
                Interlocked.Exchange(ref _isHandlingRename, 0);
            }
        });

        _chat.OnGetCurrentDisplayName = () => User;

        ConnectCommand = new AsyncRelayCommand(ConnectAsync);
        SendCommand = new AsyncRelayCommand(SendAsync);
        StartDmCommand = new AsyncRelayCommand<string>(StartDmAsync);
        DeleteChatCommand = new RelayCommand<ChatItem>(DeleteChat);
        SetMyStatusCommand = new AsyncRelayCommand<PresenceStatus>(SetMyStatusAsync);
        EditMessageCommand = new AsyncRelayCommand<ChatMessageItem>(EditMessageAsync);
        DeleteMessageCommand = new AsyncRelayCommand<ChatMessageItem>(DeleteMessageAsync);
        ReactCommand = new AsyncRelayCommand<ChatMessageItem>(ReactAsync);
        ToggleReactionCommand = new AsyncRelayCommand<ReactionItem>(ToggleReactionAsync);
        CreateGroupChatCommand = new AsyncRelayCommand(CreateGroupChatAsync);
        LoadMoreHistoryCommand = new AsyncRelayCommand(LoadMoreHistoryAsync);
        ManageChatCommand = new AsyncRelayCommand(ManageChatAsync);
        ViewAttachmentCommand = new AsyncRelayCommand<ChatMessageItem>(ViewAttachmentAsync);
        PickAndSendAttachmentCommand = new AsyncRelayCommand(PickAndSendAttachmentAsync);
        PlayVoiceMessageCommand = new AsyncRelayCommand<ChatMessageItem>(PlayVoiceMessageAsync);
        RecordVoiceMessageCommand = new AsyncRelayCommand(ToggleRecordVoiceMessageAsync);
        ForwardMessageCommand = new AsyncRelayCommand<ChatMessageItem>(ForwardMessageAsync);
        ReplyToMessageCommand = new RelayCommand<ChatMessageItem>(item => { if (item?.CanReply == true) PendingReply = item; });
        CancelReplyCommand = new RelayCommand(() => PendingReply = null);
        JumpToRepliedMessageCommand = new AsyncRelayCommand<ChatMessageItem>(JumpToRepliedMessageAsync);
        JumpToMessageCommand = new AsyncRelayCommand<ChatMessageItem>(JumpToMessageAsync);
        TogglePinCommand = new AsyncRelayCommand<ChatMessageItem>(TogglePinAsync);
        ViewEditHistoryCommand = new AsyncRelayCommand<ChatMessageItem>(ViewEditHistoryAsync);
        PickAndSendVideoCommand = new AsyncRelayCommand(PickAndSendVideoAsync);
        PlayVideoCommand = new AsyncRelayCommand<ChatMessageItem>(PlayVideoAsync);
        ToggleSearchCommand = new RelayCommand(ToggleSearch);
        JumpToSearchResultCommand = new AsyncRelayCommand<ChatMessageItem>(JumpToSearchResultAsync);
    }

    private ChatMessageItem? FindMessage(string chatId, long messageId) =>
        messageId > 0 && _chatMessages.TryGetValue(chatId, out var list)
            ? list.FirstOrDefault(m => m.Id == messageId)
            : null;

    private void MarkReadIfViewing(string chatId, long lastMessageId)
    {
        if (lastMessageId <= 0) return;
        if (IsActive && SelectedChat?.Id == chatId)
            _ = _chat.MarkReadAsync(chatId, lastMessageId);
    }

    /* Message edit/delete/react commands */
    private async Task EditMessageAsync(ChatMessageItem? item)
    {
        if (item is null || !item.CanModify) return;
        var page = GetRootPage();
        if (page is null) return;

        var newText = await page.DisplayPromptAsync("Edit message", "Update your message:", initialValue: item.Body, maxLength: 2000);
        if (newText is null) return; // cancelled

        newText = newText.Trim();
        if (newText.Length == 0 || newText == item.Body) return;

        try { await _chat.EditMessageAsync(item.Id, newText); }
        catch (Exception ex) { await Ui.DisplayAlert("Couldn't edit message", ex.Message, "OK"); }
    }

    private async Task DeleteMessageAsync(ChatMessageItem? item)
    {
        if (item is null || !item.CanModify) return;
        var page = GetRootPage();
        if (page is null) return;

        var confirmed = await page.DisplayAlert("Delete message", "This can't be undone.", "Delete", "Cancel");
        if (!confirmed) return;

        try { await _chat.DeleteMessageAsync(item.Id); }
        catch (Exception ex) { await Ui.DisplayAlert("Couldn't delete message", ex.Message, "OK"); }
    }

    private async Task ReactAsync(ChatMessageItem? item)
    {
        if (item is null || item.Id <= 0) return;
        var page = GetRootPage();
        if (page is null) return;

        var choice = await page.DisplayActionSheet("React", "Cancel", null, QuickReactionEmojis);
        if (string.IsNullOrEmpty(choice) || choice == "Cancel") return;

        try { await _chat.ToggleReactionAsync(item.Id, choice); }
        catch { }
    }

    // Tapping an existing reaction pill toggles that exact emoji directly (no action sheet).
    private async Task ToggleReactionAsync(ReactionItem? reaction)
    {
        if (reaction is null || reaction.MessageId <= 0) return;
        try { await _chat.ToggleReactionAsync(reaction.MessageId, reaction.Emoji); }
        catch { }
    }

    /* Image attachments - fetched lazily on tap, not pre-loaded with the rest of history */
    private async Task ViewAttachmentAsync(ChatMessageItem? item)
    {
        if (item is null || !item.IsImageAttachment || item.Id <= 0) return;
        if (item.AttachmentImage is not null || item.IsAttachmentLoading) return;

        item.IsAttachmentLoading = true;
        try
        {
            var data = await _chat.GetAttachmentDataAsync(item.Id);
            if (data is null) return;

            var bytes = data.Data;
            item.AttachmentImage = ImageSource.FromStream(() => new MemoryStream(bytes));
        }
        catch (Exception ex)
        {
            await Ui.DisplayAlert("Couldn't load image", ex.Message, "OK");
        }
        finally
        {
            item.IsAttachmentLoading = false;
        }
    }

    // Picks one or more images (FilePicker, not MediaPicker - MediaPicker.PickPhotoAsync only
    // ever returns a single photo, MAUI has no multi-select variant of it) and sends each as its
    // own attachment message, sequentially. A pending reply (see PendingReply) is attached only
    // to the first image sent, not to every one of them.
    private async Task PickAndSendAttachmentAsync()
    {
        if (SelectedChat is null)
        {
            await Ui.DisplayAlert("Pick a chat", "Select a chat before sending an image.", "OK");
            return;
        }
        if (IsDraftId(SelectedChat.Id))
        {
            await Ui.DisplayAlert("Send a message first", "Send a text message to start this conversation before sharing an image.", "OK");
            return;
        }

        IEnumerable<FileResult>? photos;
        try
        {
            photos = await FilePicker.Default.PickMultipleAsync(PickOptions.Images);
        }
        catch (Exception ex)
        {
            await Ui.DisplayAlert("Couldn't open picker", ex.Message, "OK");
            return;
        }

        var photoList = photos?.ToList() ?? new List<FileResult>();
        if (photoList.Count == 0) return; // user cancelled

        var replyToId = PendingReply?.Id;
        PendingReply = null;

        var failures = 0;
        foreach (var photo in photoList)
        {
            byte[] bytes;
            try
            {
                await using var stream = await photo.OpenReadAsync();
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                bytes = ms.ToArray();
            }
            catch
            {
                failures++;
                continue;
            }

            try
            {
                await _chat.SendAttachmentAsync(SelectedChat.Id, photo.FileName, GuessImageContentType(photo.FileName), bytes, caption: null, replyToId);
            }
            catch
            {
                failures++;
            }

            replyToId = null; // only the first image carries the reply
        }

        if (failures > 0)
            await Ui.DisplayAlert("Some images didn't send", $"{failures} of {photoList.Count} image(s) couldn't be sent.", "OK");
    }

    private static string GuessImageContentType(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => "image/jpeg",
        };

    /* Voice messages - record with Plugin.Maui.Audio, upload the same way as an image attachment
       (see SendVoiceMessage on the hub), and play back lazily (same fetch-on-tap idea as
       ViewAttachmentAsync). Only one recording and one playback happen at a time. */
    private async Task ToggleRecordVoiceMessageAsync()
    {
        if (IsRecordingVoiceMessage)
        {
            IsRecordingVoiceMessage = false;
            _ = _chat.SendRecordingAsync(CurrentChannelId, false);
            if (_voiceRecorder is null) return;

            IAudioSource recorded;
            try
            {
                recorded = await _voiceRecorder.StopAsync();
            }
            catch (Exception ex)
            {
                await Ui.DisplayAlert("Couldn't stop recording", ex.Message, "OK");
                return;
            }
            finally
            {
                _voiceRecorder = null;
            }

            await SendRecordedVoiceMessageAsync(recorded);
            return;
        }

        if (SelectedChat is null)
        {
            await Ui.DisplayAlert("Pick a chat", "Select a chat before recording a voice message.", "OK");
            return;
        }
        if (IsDraftId(SelectedChat.Id))
        {
            await Ui.DisplayAlert("Send a message first", "Send a text message to start this conversation before sending a voice message.", "OK");
            return;
        }

        var granted = await Permissions.RequestAsync<Permissions.Microphone>();
        if (granted != PermissionStatus.Granted)
        {
            await Ui.DisplayAlert("Microphone access needed", "Allow microphone access to record a voice message.", "OK");
            return;
        }

        try
        {
            _voiceRecorder = _audio.CreateRecorder();
            await _voiceRecorder.StartAsync();
            IsRecordingVoiceMessage = true;
            _voiceRecordingStartedAt = DateTime.UtcNow;
            _ = _chat.SendRecordingAsync(CurrentChannelId, true);
        }
        catch (Exception ex)
        {
            _voiceRecorder = null;
            await Ui.DisplayAlert("Couldn't start recording", ex.Message, "OK");
        }
    }

    private DateTime _voiceRecordingStartedAt;

    private async Task SendRecordedVoiceMessageAsync(IAudioSource recorded)
    {
        if (SelectedChat is null) return;

        byte[] bytes;
        try
        {
            using var stream = recorded.GetAudioStream();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            bytes = ms.ToArray();
        }
        catch (Exception ex)
        {
            await Ui.DisplayAlert("Couldn't read recording", ex.Message, "OK");
            return;
        }

        if (bytes.Length == 0) return; // e.g. stopped almost immediately

        var duration = (int)Math.Max(1, (DateTime.UtcNow - _voiceRecordingStartedAt).TotalSeconds);
        var replyToId = PendingReply?.Id;
        PendingReply = null;

        try
        {
            await _chat.SendVoiceMessageAsync(SelectedChat.Id, "audio/wav", bytes, duration, replyToId);
        }
        catch (Exception ex)
        {
            await Ui.DisplayAlert("Couldn't send voice message", ex.Message, "OK");
        }
    }

    private async Task PlayVoiceMessageAsync(ChatMessageItem? item)
    {
        if (item is null || !item.IsVoiceMessage || item.Id <= 0) return;

        // Tapping the item that's currently playing stops it instead of restarting it.
        if (_playingVoiceMessageItem == item && item.IsPlayingVoiceMessage)
        {
            StopVoicePlayback();
            return;
        }

        StopVoicePlayback();

        if (item.VoiceMessageData is null)
        {
            if (item.IsAttachmentLoading) return;
            item.IsAttachmentLoading = true;
            try
            {
                var data = await _chat.GetAttachmentDataAsync(item.Id);
                if (data is null) return;
                item.VoiceMessageData = data.Data;
            }
            catch (Exception ex)
            {
                await Ui.DisplayAlert("Couldn't load voice message", ex.Message, "OK");
                return;
            }
            finally
            {
                item.IsAttachmentLoading = false;
            }
        }

        try
        {
            var stream = new MemoryStream(item.VoiceMessageData!);
            _voicePlayer = _audio.CreatePlayer(stream);
            _playingVoiceMessageItem = item;
            item.IsPlayingVoiceMessage = true;
            item.PlaybackPositionSeconds = 0;
            if (_voicePlayer.Duration > 0)
                item.PlaybackDurationSeconds = _voicePlayer.Duration;
            _voicePlayer.PlaybackEnded += (_, __) =>
                MainThread.BeginInvokeOnMainThread(StopVoicePlayback);
            _voicePlayer.Play();
            StartVoicePlaybackProgressTimer();
        }
        catch (Exception ex)
        {
            item.IsPlayingVoiceMessage = false;
            await Ui.DisplayAlert("Couldn't play voice message", ex.Message, "OK");
        }
    }

    // Plugin.Maui.Audio doesn't raise a position-changed event, so playback progress (for the
    // bubble's progress bar / "0:03 / 0:12" label) is polled on a short timer instead, and
    // stopped as soon as nothing is playing.
    private void StartVoicePlaybackProgressTimer()
    {
        _voicePlaybackProgressTimer ??= Application.Current!.Dispatcher.CreateTimer();
        _voicePlaybackProgressTimer.Interval = TimeSpan.FromMilliseconds(200);
        _voicePlaybackProgressTimer.Tick -= OnVoicePlaybackProgressTick;
        _voicePlaybackProgressTimer.Tick += OnVoicePlaybackProgressTick;
        _voicePlaybackProgressTimer.Start();
    }

    private void OnVoicePlaybackProgressTick(object? sender, EventArgs e)
    {
        if (_voicePlayer is null || _playingVoiceMessageItem is null)
        {
            _voicePlaybackProgressTimer?.Stop();
            return;
        }

        _playingVoiceMessageItem.PlaybackPositionSeconds = _voicePlayer.CurrentPosition;
        if (_voicePlayer.Duration > 0)
            _playingVoiceMessageItem.PlaybackDurationSeconds = _voicePlayer.Duration;
    }

    private void StopVoicePlayback()
    {
        _voicePlaybackProgressTimer?.Stop();

        if (_playingVoiceMessageItem is not null)
        {
            _playingVoiceMessageItem.IsPlayingVoiceMessage = false;
            _playingVoiceMessageItem.PlaybackPositionSeconds = 0;
        }
        _playingVoiceMessageItem = null;

        if (_voicePlayer is null) return;
        try { _voicePlayer.Stop(); } catch { }
        _voicePlayer.Dispose();
        _voicePlayer = null;
    }

    /* Forwarding - offers every chat except the one the message already lives in (matching by
       label, since that's all a DisplayActionSheet choice gives back). */
    private async Task ForwardMessageAsync(ChatMessageItem? item)
    {
        if (item is null || !item.CanForward) return;

        var page = GetRootPage();
        if (page is null) return;

        var targets = Chats.Where(c => c != SelectedChat && !IsDraftId(c.Id)).ToList();
        if (targets.Count == 0)
        {
            await Ui.DisplayAlert("Nowhere to forward to", "You need another chat to forward this message to.", "OK");
            return;
        }

        var choice = await page.DisplayActionSheet("Forward to…", "Cancel", null, targets.Select(c => c.Label).ToArray());
        if (string.IsNullOrEmpty(choice) || choice == "Cancel") return;

        var target = targets.FirstOrDefault(c => c.Label == choice);
        if (target is null) return;

        try
        {
            await _chat.ForwardMessageAsync(item.Id, target.Id);
        }
        catch (Exception ex)
        {
            await Ui.DisplayAlert("Couldn't forward message", ex.Message, "OK");
        }
    }

    /* Pinning - any member can pin/unpin (see ChatHub.PinMessage); the live MessagePinned/
       MessageUnpinned events (subscribed in the constructor) are what actually flip
       item.IsPinned and update PinnedMessages, this just calls the server and surfaces errors
       (e.g. hitting the per-chat cap). */
    private async Task TogglePinAsync(ChatMessageItem? item)
    {
        if (item is null || !item.CanPin) return;

        try
        {
            if (item.IsPinned) await _chat.UnpinMessageAsync(item.Id);
            else await _chat.PinMessageAsync(item.Id);
        }
        catch (Exception ex)
        {
            await Ui.DisplayAlert("Couldn't update pin", ex.Message, "OK");
        }
    }

    private async Task ViewEditHistoryAsync(ChatMessageItem? item)
    {
        if (item is null || !item.CanViewEditHistory) return;

        try
        {
            var history = await _chat.GetMessageEditHistoryAsync(item.Id);
            if (history.Count == 0)
            {
                await Ui.DisplayAlert("Edit history", "No earlier versions available.", "OK");
                return;
            }

            var lines = history.Select(h => $"{h.EditedAtUtc.ToLocalTime():g}\n{h.PreviousBody}");
            await Ui.DisplayAlert("Edit history", string.Join("\n\n", lines), "OK");
        }
        catch (Exception ex)
        {
            await Ui.DisplayAlert("Couldn't load edit history", ex.Message, "OK");
        }
    }

    /* Video - picked with MediaPicker (single clip; MAUI has no multi-video picker), uploaded via
       SendVideo, then played externally through the OS's own video player (Launcher.OpenAsync) -
       simplest correct option without pulling in a dedicated media-playback control just for
       this. Duration isn't extracted client-side (no reliable cross-platform API for a FileResult
       without a media library), unlike a voice message where the recorder always knows it. */
    private async Task PickAndSendVideoAsync()
    {
        if (SelectedChat is null)
        {
            await Ui.DisplayAlert("Pick a chat", "Select a chat before sending a video.", "OK");
            return;
        }
        if (IsDraftId(SelectedChat.Id))
        {
            await Ui.DisplayAlert("Send a message first", "Send a text message to start this conversation before sharing a video.", "OK");
            return;
        }

        FileResult? video;
        try
        {
            video = await MediaPicker.Default.PickVideoAsync();
        }
        catch (Exception ex)
        {
            await Ui.DisplayAlert("Couldn't open picker", ex.Message, "OK");
            return;
        }
        if (video is null) return; // user cancelled

        byte[] bytes;
        try
        {
            await using var stream = await video.OpenReadAsync();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            bytes = ms.ToArray();
        }
        catch (Exception ex)
        {
            await Ui.DisplayAlert("Couldn't read video", ex.Message, "OK");
            return;
        }

        var replyToId = PendingReply?.Id;
        PendingReply = null;

        try
        {
            await _chat.SendVideoAsync(SelectedChat.Id, video.FileName, GuessVideoContentType(video.FileName), bytes, durationSeconds: null, replyToId);
        }
        catch (Exception ex)
        {
            await Ui.DisplayAlert("Couldn't send video", ex.Message, "OK");
        }
    }

    private static string GuessVideoContentType(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".mov" => "video/quicktime",
            ".webm" => "video/webm",
            _ => "video/mp4",
        };

    private async Task PlayVideoAsync(ChatMessageItem? item)
    {
        if (item is null || !item.IsVideoAttachment || item.Id <= 0) return;

        if (item.VideoLocalPath is not null)
        {
            try { await Launcher.Default.OpenAsync(new OpenFileRequest("Video", new ReadOnlyFile(item.VideoLocalPath))); }
            catch (Exception ex) { await Ui.DisplayAlert("Couldn't open video", ex.Message, "OK"); }
            return;
        }

        if (item.IsAttachmentLoading) return;
        item.IsAttachmentLoading = true;
        try
        {
            var data = await _chat.GetAttachmentDataAsync(item.Id);
            if (data is null) return;

            var ext = data.ContentType switch
            {
                "video/quicktime" => ".mov",
                "video/webm" => ".webm",
                _ => ".mp4",
            };
            var path = Path.Combine(FileSystem.CacheDirectory, $"chatter-video-{item.Id}{ext}");
            await File.WriteAllBytesAsync(path, data.Data);
            item.VideoLocalPath = path;

            await Launcher.Default.OpenAsync(new OpenFileRequest("Video", new ReadOnlyFile(path)));
        }
        catch (Exception ex)
        {
            await Ui.DisplayAlert("Couldn't play video", ex.Message, "OK");
        }
        finally
        {
            item.IsAttachmentLoading = false;
        }
    }

    /* In-chat search - debounced on SearchQuery changes (same idea as the typing debounce above),
       scoped to whichever chat is currently selected. */
    private void ToggleSearch()
    {
        IsSearching = !IsSearching;
        if (!IsSearching)
        {
            SearchQuery = null;
            SearchResults.Clear();
            _searchCts?.Cancel();
        }
    }

    partial void OnSearchQueryChanged(string? value)
    {
        _searchCts?.Cancel();
        if (!IsSearching || SelectedChat is null || IsDraftId(SelectedChat.Id) || string.IsNullOrWhiteSpace(value))
        {
            SearchResults.Clear();
            return;
        }

        _searchCts = new CancellationTokenSource();
        _ = DelayedSearchAsync(SelectedChat.Id, value, _searchCts.Token);
    }

    private async Task DelayedSearchAsync(string chatId, string query, CancellationToken ct)
    {
        try { await Task.Delay(300, ct); }
        catch (TaskCanceledException) { return; }
        if (ct.IsCancellationRequested) return;

        IsSearchRunning = true;
        try
        {
            var results = await _chat.SearchMessagesAsync(chatId, query);
            if (ct.IsCancellationRequested) return;

            SearchResults.Clear();
            foreach (var dto in results)
                SearchResults.Add(ToItem(dto));
        }
        catch
        {
            // Transient connection hiccup - leave whatever results were already shown.
        }
        finally
        {
            if (!ct.IsCancellationRequested) IsSearchRunning = false;
        }
    }

    // Closes search and, if the message is already among the currently-loaded messages, scrolls
    // to it (handled by the page's code-behind, which owns the CollectionView reference). Older
    // messages that haven't been paged in yet just don't scroll - there's no "load history around
    // this id" API, only "load older from the top" (see LoadMoreHistoryCommand).
    private async Task JumpToSearchResultAsync(ChatMessageItem? item)
    {
        if (item is null) return;

        IsSearching = false;
        SearchQuery = null;
        SearchResults.Clear();

        await JumpToMessageIdAsync(item.Id);
    }

    // Tapping a reply preview inside a bubble scrolls to the original message it points to - same
    // "only works if already loaded" limitation as everything else that jumps to a message id,
    // for the same reason (no "load history around this id" API).
    private Task JumpToRepliedMessageAsync(ChatMessageItem? item) =>
        item?.ReplyTo is { } replyTo ? JumpToMessageIdAsync(replyTo.MessageId) : Task.CompletedTask;

    // Tapping an entry in the pinned-messages bar scrolls to it directly - same idea, but the
    // item passed in (from PinnedMessages) is usually already the live instance itself.
    private Task JumpToMessageAsync(ChatMessageItem? item) =>
        item is not null ? JumpToMessageIdAsync(item.Id) : Task.CompletedTask;

    private async Task JumpToMessageIdAsync(long messageId)
    {
        if (SelectedChat is null) return;

        var target = FindMessage(SelectedChat.Id, messageId);
        if (target is null)
        {
            await Ui.DisplayAlert("Message not loaded", "This message is older than what's currently loaded. Use \"Load earlier messages\" first.", "OK");
            return;
        }

        WeakReferenceMessenger.Default.Send(new ScrollToMessageMessage(target));
    }

    // Re-resolves one person's avatar URL after an AvatarChanged notification, appending
    // `version` as a cache-busting query string so the client actually refetches the new image
    // instead of reusing whatever it had cached for the old URL.
    private async Task RefreshAvatarAsync(string displayName, long version)
    {
        try
        {
            var urls = await _chat.GetAvatarUrlsAsync(new List<string> { displayName });
            if (!urls.TryGetValue(displayName, out var relativeUrl)) return;

            var person = People.FirstOrDefault(p => Ci.Equals(p.Name, Canon(displayName)));
            if (person is not null)
                person.AvatarUrl = $"{ServerConfig.BaseUrl}{relativeUrl}?v={version}";
        }
        catch { }
    }

    /* Group chat creation */
    private async Task CreateGroupChatAsync()
    {
        var page = GetRootPage();
        if (page is null) return;

        var name = await page.DisplayPromptAsync("New group", "Group name:");
        if (string.IsNullOrWhiteSpace(name)) return;

        var membersText = await page.DisplayPromptAsync("New group", "Members (comma-separated display names):");
        if (membersText is null) return;

        var members = membersText
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (members.Count == 0)
        {
            await Ui.DisplayAlert("Add at least one member", "A group needs at least one other member.", "OK");
            return;
        }

        try
        {
            var chatId = await _chat.CreateGroupChatAsync(name.Trim(), members);
            if (!string.IsNullOrWhiteSpace(chatId))
                SelectedChat = EnsureChatItemWithLabel(chatId, name.Trim());
        }
        catch (Exception ex)
        {
            await Ui.DisplayAlert("Couldn't create group", ex.Message, "OK");
        }
    }

    // Single entry point for mute/block/group-admin actions, offered from the chat header.
    // Group-admin options are always offered on a group chat rather than only to the admin -
    // if the caller isn't the creator, the server rejects it and we just show the error; the
    // client has no separate notion of "am I the admin" to hide them proactively.
    private async Task ManageChatAsync()
    {
        var chat = SelectedChat;
        var page = GetRootPage();
        if (chat is null || page is null) return;
        if (Ci.Equals(chat.Id, "Lobby") || IsDraftId(chat.Id)) return;

        var isGroup = chat.IsGroup;
        var isDm = chat.Id.StartsWith("dm:", StringComparison.OrdinalIgnoreCase);

        var options = new List<string> { chat.IsMuted ? "Unmute" : "Mute" };
        if (isGroup) options.AddRange(new[] { "View members", "Add member", "Remove member", "Rename group", "Promote to admin", "Demote from admin" });
        if (isDm) options.AddRange(new[] { "Block user", "Unblock user", "Last seen" });

        var choice = await page.DisplayActionSheet($"Manage \"{chat.Label}\"", "Cancel", null, options.ToArray());
        if (string.IsNullOrEmpty(choice) || choice == "Cancel") return;

        try
        {
            switch (choice)
            {
                case "Mute":
                    await _chat.SetChatMutedAsync(chat.Id, true);
                    chat.IsMuted = true;
                    break;
                case "Unmute":
                    await _chat.SetChatMutedAsync(chat.Id, false);
                    chat.IsMuted = false;
                    break;
                case "View members":
                    var members = await _chat.GetGroupMembersAsync(chat.Id);
                    var admins = new HashSet<string>(await _chat.GetGroupAdminsAsync(chat.Id), Ci);
                    var lines = members.Select(m => admins.Contains(m) ? $"{m} (admin)" : m);
                    await Ui.DisplayAlert("Members", string.Join("\n", lines), "OK");
                    break;
                case "Promote to admin":
                    var toPromote = await page.DisplayPromptAsync("Promote to admin", "Display name:");
                    if (!string.IsNullOrWhiteSpace(toPromote))
                        await _chat.PromoteGroupAdminAsync(chat.Id, toPromote.Trim());
                    break;
                case "Demote from admin":
                    var toDemote = await page.DisplayPromptAsync("Demote from admin", "Display name:");
                    if (!string.IsNullOrWhiteSpace(toDemote))
                        await _chat.DemoteGroupAdminAsync(chat.Id, toDemote.Trim());
                    break;
                case "Add member":
                    var toAdd = await page.DisplayPromptAsync("Add member", "Display name:");
                    if (!string.IsNullOrWhiteSpace(toAdd))
                        await _chat.AddGroupMemberAsync(chat.Id, toAdd.Trim());
                    break;
                case "Remove member":
                    var toRemove = await page.DisplayPromptAsync("Remove member", "Display name:");
                    if (!string.IsNullOrWhiteSpace(toRemove))
                        await _chat.RemoveGroupMemberAsync(chat.Id, toRemove.Trim());
                    break;
                case "Rename group":
                    var newName = await page.DisplayPromptAsync("Rename group", "New name:", initialValue: chat.Label);
                    if (!string.IsNullOrWhiteSpace(newName))
                        await _chat.RenameGroupChatAsync(chat.Id, newName.Trim());
                    break;
                case "Block user":
                    await _chat.BlockUserAsync(chat.Label);
                    break;
                case "Unblock user":
                    await _chat.UnblockUserAsync(chat.Label);
                    break;
                case "Last seen":
                    var lastSeen = await _chat.GetLastSeenAsync(new List<string> { chat.Label });
                    var text = lastSeen.TryGetValue(chat.Label, out var when)
                        ? $"Last seen {when.ToLocalTime():g}"
                        : "Online now, or never seen offline.";
                    await Ui.DisplayAlert("Last seen", text, "OK");
                    break;
            }
        }
        catch (Exception ex)
        {
            await Ui.DisplayAlert("Couldn't complete that action", ex.Message, "OK");
        }
    }

    /* Chat labeling & item management
       Real (non-draft, non-Lobby) chat IDs are opaque — only the server knows who a DM's
       other participant is, so it tells us via ChatSummary.Label / AddedChat / DmNotify.
       This just covers the two cases the client can label on its own. */
    private string ComputeChatLabel(string chatId)
    {
        if (IsDraftId(chatId))
        {
            var other = ResolveName(chatId.Substring("draft:".Length));
            return DraftLabel(other);
        }

        if (string.Equals(chatId, "Lobby", StringComparison.OrdinalIgnoreCase))
            return "Lobby";

        var existing = Chats.FirstOrDefault(c => Ci.Equals(c.Id, chatId));
        return existing?.Label ?? chatId;
    }

    private ChatItem EnsureChatItem(string chatId)
    {
        var item = Chats.FirstOrDefault(c => string.Equals(c.Id, chatId, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            item = new ChatItem(chatId) { Label = ComputeChatLabel(chatId) };
            Chats.Add(item);
        }
        return item;
    }

    // initialUnread is only applied when the item is newly created (e.g. the first ChatsForMe
    // snapshot after connecting) - once a chat exists locally, its Unread count is driven live
    // by incoming messages instead, so a later ChatsForMe refresh can't stomp on that.
    private ChatItem EnsureChatItemWithLabel(string chatId, string label, int? initialUnread = null)
    {
        var item = Chats.FirstOrDefault(c => Ci.Equals(c.Id, chatId));
        if (item is null)
        {
            item = new ChatItem(chatId) { Label = label, Unread = initialUnread ?? 0 };
            Chats.Add(item);
        }
        else
        {
            item.Label = label;
        }
        return item;
    }

    // Best-effort label cache for chats a user deleted locally, so re-showing them
    // (a new message arrives) doesn't fall back to a raw opaque chat ID.
    private readonly Dictionary<string, string> _hiddenChatLabels = new(StringComparer.OrdinalIgnoreCase);

    private void RelabelDmChatsFor(string oldLabel, string newLabel)
    {
        foreach (var chat in Chats)
            if (chat.Id.StartsWith("dm:", StringComparison.OrdinalIgnoreCase) && Ci.Equals(chat.Label, oldLabel))
                chat.Label = newLabel;
    }

    private void SwapDraftToReal(string draftId, string realChatId, string label)
    {
        if (_chatMessages.TryGetValue(draftId, out var draftMsgs))
        {
            _chatMessages[realChatId] = draftMsgs;
            _chatMessages.Remove(draftId);
        }

        var draftItem = Chats.FirstOrDefault(c => string.Equals(c.Id, draftId, StringComparison.OrdinalIgnoreCase));
        if (draftItem != null)
        {
            var idx = Chats.IndexOf(draftItem);
            Chats.RemoveAt(idx);
            var newItem = new ChatItem(realChatId) { Label = label };
            Chats.Insert(idx, newItem);
            SelectedChat = newItem;
        }
        else
        {
            var newItem = EnsureChatItemWithLabel(realChatId, label);
            SelectedChat = newItem;
        }

        RefreshVisibleChat();
    }

    /* Selection & placeholder refresh */
    partial void OnSelectedChatChanged(ChatItem? value)
    {
        RefreshVisibleChat();
        UpdateMessagePlaceholder();

        OnPropertyChanged(nameof(CanSend));
        CanLoadMoreHistory = false;
        PendingReply = null; // a reply to a message in the chat we're leaving wouldn't make sense here
        PinnedMessages.Clear();

        if (value is not null && !value.Id.StartsWith("draft:", StringComparison.OrdinalIgnoreCase))
        {
            _ = _chat.JoinChatAsync(value.Id);
            _ = LoadHistoryIfNeededAsync(value.Id);
            _ = LoadPinnedMessagesAsync(value.Id);

            if (value.Id.StartsWith("group:", StringComparison.OrdinalIgnoreCase))
                _ = LoadGroupSeenStateAsync(value.Id);
        }
    }

    private async Task LoadPinnedMessagesAsync(string chatId)
    {
        try
        {
            var pinned = await _chat.GetPinnedMessagesAsync(chatId);
            if (SelectedChat?.Id != chatId) return; // the user already moved on to another chat

            PinnedMessages.Clear();
            foreach (var dto in pinned)
            {
                // Prefer the live, already-mutable instance from this chat's message list (if
                // loaded) so pin/unpin/edit/delete on it stay in sync with what's shown below.
                var existing = FindMessage(chatId, dto.Id);
                PinnedMessages.Add(existing ?? ToItem(dto));
            }
        }
        catch
        {
            // Best-effort - worst case the pinned bar just doesn't show for this chat.
        }
    }

    // Bootstraps membership + existing read receipts for a group chat once (on first selection),
    // then recomputes the "seen by N/M" indicator on its latest own message, if any.
    private async Task LoadGroupSeenStateAsync(string chatId)
    {
        try
        {
            if (!_groupMembersByChatId.ContainsKey(chatId))
                _groupMembersByChatId[chatId] = (await _chat.GetGroupMembersAsync(chatId)).ToList();

            var receipts = await _chat.GetChatReadReceiptsAsync(chatId);
            var byUser = _lastReadByChatAndUser.TryGetValue(chatId, out var existing) ? existing : new(Ci);
            foreach (var kv in receipts) byUser[kv.Key] = kv.Value;
            _lastReadByChatAndUser[chatId] = byUser;

            RecomputeGroupSeenCounts(chatId);
        }
        catch
        {
            // Best-effort - worst case the "seen by" indicator just doesn't show for this chat.
        }
    }

    // Mirrors the DM "Seen" marker's single-latest-message convention, but as a count instead of
    // a bool: only the caller's most recent real message in the chat shows "Seen by N/M".
    private void RecomputeGroupSeenCounts(string chatId)
    {
        if (!_chatMessages.TryGetValue(chatId, out var list)) return;
        var latestMine = list.Where(m => m.IsMine && m.Id > 0).OrderByDescending(m => m.Id).FirstOrDefault();
        if (latestMine is null) return;

        var members = _groupMembersByChatId.TryGetValue(chatId, out var m) ? m : new List<string>();
        var others = members.Where(n => !Ci.Equals(n, User)).ToList();
        var byUser = _lastReadByChatAndUser.TryGetValue(chatId, out var r) ? r : new Dictionary<string, long>(Ci);

        latestMine.SeenTotal = others.Count;
        latestMine.SeenCount = others.Count(n => byUser.TryGetValue(n, out var last) && last >= latestMine.Id);
    }

    private ChatMessageItem ToItem(ChatMessageDto dto)
    {
        var item = new ChatMessageItem(dto.Id, dto.Sender, dto.Body, dto.SentAtUtc,
            isMine: Ci.Equals(dto.Sender, User), isSystem: false,
            dto.Attachment, dto.IsForwarded, dto.ReplyTo, dto.IsPinned)
        {
            EditedAtUtc = dto.EditedAtUtc,
            IsDeleted = dto.IsDeleted,
        };

        foreach (var r in dto.Reactions)
            item.Reactions.Add(new ReactionItem(dto.Id, r.Emoji, r.Count, r.ReactedByMe, r.ReactedBy));

        return item;
    }

    // Chats only hold whatever arrived live during this app session, so the first time a
    // chat is opened we backfill from the server's persisted history (see ChatHub.GetChatHistory).
    private async Task LoadHistoryIfNeededAsync(string chatId)
    {
        if (!_historyLoaded.Add(chatId)) return;

        try
        {
            var history = await _chat.GetChatHistoryAsync(chatId, beforeMessageId: null, take: HistoryPageSize);

            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (!_chatMessages.TryGetValue(chatId, out var list))
                    _chatMessages[chatId] = list = new ObservableCollection<ChatMessageItem>();

                var insertAt = 0;
                foreach (var dto in history)
                {
                    if (list.Any(x => x.Id == dto.Id)) continue;
                    list.Insert(insertAt++, ToItem(dto));
                }

                if (history.Count > 0)
                    _oldestLoadedMessageId[chatId] = history[0].Id;

                if (history.Count < HistoryPageSize) _noMoreHistory.Add(chatId);
                else _noMoreHistory.Remove(chatId);

                if (SelectedChat?.Id == chatId)
                {
                    RefreshVisibleChat();
                    CanLoadMoreHistory = !_noMoreHistory.Contains(chatId);
                }
            });
        }
        catch { }
    }

    private async Task LoadMoreHistoryAsync()
    {
        var chat = SelectedChat;
        if (chat is null || IsDraftId(chat.Id)) return;
        if (!_oldestLoadedMessageId.TryGetValue(chat.Id, out var before)) return;

        try
        {
            var older = await _chat.GetChatHistoryAsync(chat.Id, beforeMessageId: before, take: HistoryPageSize);

            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (!_chatMessages.TryGetValue(chat.Id, out var list))
                    _chatMessages[chat.Id] = list = new ObservableCollection<ChatMessageItem>();

                var insertAt = 0;
                foreach (var dto in older)
                {
                    if (list.Any(x => x.Id == dto.Id)) continue;
                    list.Insert(insertAt++, ToItem(dto));
                }

                if (older.Count > 0)
                    _oldestLoadedMessageId[chat.Id] = older[0].Id;

                if (older.Count < HistoryPageSize) _noMoreHistory.Add(chat.Id);

                if (SelectedChat?.Id == chat.Id)
                {
                    RefreshVisibleChat();
                    CanLoadMoreHistory = !_noMoreHistory.Contains(chat.Id);
                }
            });
        }
        catch { }
    }

    private void UpdateMessagePlaceholder()
    {
        if (SelectedChat is null)
        {
            MessagePlaceholder = "Type a message…";
            return;
        }

        var id = SelectedChat.Id;

        if (string.Equals(id, "Lobby", StringComparison.OrdinalIgnoreCase))
        {
            MessagePlaceholder = "Write a message in the lobby...";
            return;
        }

        if (id.StartsWith("draft:", StringComparison.OrdinalIgnoreCase))
        {
            var other = FormatNameWithAliases(id.Substring("draft:".Length));
            MessagePlaceholder = $"Type a message to {other} (try :smile:, :party:)";
            return;
        }

        if (id.StartsWith("dm:", StringComparison.OrdinalIgnoreCase))
        {
            var other = SelectedChat?.Label;
            MessagePlaceholder = $"Type a message to {FormatNameWithAliases(string.IsNullOrWhiteSpace(other) ? "this chat" : other)} ";
            return;
        }

        MessagePlaceholder = $"Type a message to {ComputeChatLabel(id)} ";
    }

    /* Visible message refresh */
    public void RefreshVisibleChat()
    {
        CurrentChatMessages.Clear();
        if (SelectedChat is null) return;

        if (_chatMessages.TryGetValue(SelectedChat.Id, out var list))
        {
            foreach (var item in list)
                CurrentChatMessages.Add(item);

            var lastRealId = list.Where(m => m.Id > 0).Select(m => m.Id).DefaultIfEmpty(0).Max();
            MarkReadIfViewing(SelectedChat.Id, lastRealId);
        }

        SelectedChat.Unread = 0;
    }

    /* Connect workflow */
    private async Task ConnectAsync()
    {
        try
        {
            await _chat.StartAsync(ServerConfig.BaseUrl);

            if (!string.IsNullOrWhiteSpace(User))
                await SafeSetDisplayNameOnServerAsync(User);

            EnsureSelfKnownUser();
            HarmonizeSelfPresence();

            try
            {
                var dict = await _chat.GetNameAliasesAsync();
                foreach (var kv in dict)
                {
                    var oldCur = Canon(kv.Key);
                    var newCur = Canon(kv.Value);
                    if (!Ci.Equals(oldCur, newCur))
                    {
                        _nameAliases[oldCur] = newCur;
                        RelabelDmChatsFor(oldCur, newCur);
                    }
                }
                NormalizeKnownUsers();
                RecomputePeople(OnlineUsers);

                var statuses = await _chat.GetStatusesAsync();
                foreach (var kv in statuses)
                    _statusByName[Canon(kv.Key)] = ParseStatus(kv.Value);

                HarmonizeSelfPresence();
                RecomputePeople(OnlineUsers);

                var meCanon = Canon(User);
                if (statuses.TryGetValue(meCanon, out var mine) && !string.IsNullOrWhiteSpace(mine))
                    SetMyStatusFromServer(ParseStatus(mine));
            }
            catch { }

            await HardReturnToLobbyUIAsync();
            _ = _chat.JoinChatAsync("Lobby");
        }
        catch (Exception ex)
        {
            await Ui.DisplayAlert("Connect failed", ex.Message ?? "Unknown error", "OK");
        }
    }

    /* Send workflow */
    private async Task SendAsync()
    {
        if (string.IsNullOrWhiteSpace(User))
        {
            await Ui.DisplayAlert("Pick a username", "Please enter a username first.", "OK");
            return;
        }

        if (string.IsNullOrWhiteSpace(OutgoingMessage) || SelectedChat is null)
            return;

        var msg = OutgoingMessage!;
        OutgoingMessage = string.Empty;

        if (IsDraftId(SelectedChat.Id))
        {
            var other = SelectedChat.Label?.Replace(" (draft)", "")
                        ?? SelectedChat.Id.Substring("draft:".Length);

            string? realChatId;
            try { realChatId = await _chat.SendDmFirstAsync(other, msg); }
            catch { realChatId = null; }

            if (string.IsNullOrWhiteSpace(realChatId))
            {
                realChatId = await _chat.CreateDmAsync(other);
                if (!string.IsNullOrWhiteSpace(realChatId))
                    await _chat.SendToChatAsync(realChatId, msg);
            }

            if (!string.IsNullOrWhiteSpace(realChatId))
            {
                SwapDraftToReal(SelectedChat.Id, realChatId, other);
                _ = _chat.JoinChatAsync(realChatId);
            }

            return;
        }

        var replyToId = PendingReply?.Id;
        PendingReply = null;
        await _chat.SendToChatAsync(SelectedChat.Id, msg, replyToId);
    }

    /* Start DM & presence setter */
    private Task StartDmAsync(string? otherDisplayName)
    {
        if (string.IsNullOrWhiteSpace(otherDisplayName) ||
            otherDisplayName.Equals(User, StringComparison.OrdinalIgnoreCase))
            return Task.CompletedTask;

        var draftId = DraftOf(otherDisplayName);
        var item = EnsureChatItem(draftId);
        item.Label = DraftLabel(otherDisplayName);
        SelectedChat = item;

        _knownUsers.Add(ResolveName(otherDisplayName));
        RecomputePeople(OnlineUsers);

        return Task.CompletedTask;
    }

    private async Task SetMyStatusAsync(PresenceStatus status)
    {
        var me = Canon(User);
        if (_statusByName.TryGetValue(me, out var current) && current == status)
        {
            if (!EqualityComparer<PresenceStatus>.Default.Equals(MyStatus, status))
                SetMyStatusFromServer(status);
            return;
        }

        _statusByName[me] = status;
        if (!EqualityComparer<PresenceStatus>.Default.Equals(MyStatus, status))
            SetMyStatusFromServer(status);

        EnsureSelfInOnlineListFor(status);
        RecomputePeople(OnlineUsers);

        try { await _chat.SetStatusAsync(StatusToWire(status)); }
        catch { }
    }

    /* Status options & name normalization */
    public IReadOnlyList<PresenceStatus> StatusOptions { get; } =
        new[] { PresenceStatus.Online, PresenceStatus.Away, PresenceStatus.Busy };

    private void NormalizeKnownUsers()
    {
        var canon = _knownUsers.Select(Canon).Distinct(Ci).ToList();
        _knownUsers.Clear();
        foreach (var n in canon) _knownUsers.Add(n);
    }

    /* People recompute */
    private void RecomputePeople(IEnumerable<string> onlineNow)
    {
        var onlineSet = new HashSet<string>(onlineNow.Select(Canon), Ci);

        // DM partners (including offline ones) are seeded into _knownUsers as their chats
        // arrive from the server (ChatsForMe / AddedChat / DmNotify all carry the label),
        // since chat IDs are opaque and can't be parsed for a display name here.
        EnsureSelfKnownUser();

        var names = _knownUsers.Select(Canon).Distinct(Ci).ToList();
        names.Sort((a, b) =>
        {
            var aOn = onlineSet.Contains(a);
            var bOn = onlineSet.Contains(b);
            var cmp = bOn.CompareTo(aOn);
            return cmp != 0 ? cmp : StringComparer.OrdinalIgnoreCase.Compare(a, b);
        });

        for (int i = People.Count - 1; i >= 0; i--)
        {
            var name = People[i].Name;
            var isCanonical = Ci.Equals(name, Canon(name));
            var stillExists = names.Contains(name, Ci);
            if (!isCanonical || !stillExists)
                People.RemoveAt(i);
        }

        foreach (var n in names)
        {
            var status = _statusByName.TryGetValue(n, out var st)
                ? st
                : (onlineSet.Contains(n) ? PresenceStatus.Online : PresenceStatus.Offline);

            var item = People.FirstOrDefault(p => Ci.Equals(p.Name, n));
            if (item is null)
            {
                People.Add(new UserPresenceItem(n, status));
            }
            else
            {
                item.Status = status;
            }
        }

        for (int i = 0; i < names.Count; i++)
        {
            var idx = People.IndexOf(People.First(p => Ci.Equals(p.Name, names[i])));
            if (idx != i) People.Move(idx, i);
        }

        OnlineUsers.Clear();
        foreach (var n in onlineSet) OnlineUsers.Add(n);

        ResolveAvatarsIfNeeded(names);
    }

    // Fetches avatar URLs once per display name (cached in _avatarResolved) rather than
    // re-fetching every time RecomputePeople runs, which happens frequently.
    private readonly HashSet<string> _avatarResolved = new(StringComparer.OrdinalIgnoreCase);

    private void ResolveAvatarsIfNeeded(IEnumerable<string> names)
    {
        var toResolve = names.Where(n => _avatarResolved.Add(n)).ToList();
        if (toResolve.Count > 0) _ = ResolveAvatarsAsync(toResolve);
    }

    private async Task ResolveAvatarsAsync(List<string> names)
    {
        try
        {
            var urls = await _chat.GetAvatarUrlsAsync(names);
            MainThread.BeginInvokeOnMainThread(() =>
            {
                foreach (var kv in urls)
                {
                    var person = People.FirstOrDefault(p => Ci.Equals(p.Name, Canon(kv.Key)));
                    if (person is not null)
                        person.AvatarUrl = $"{ServerConfig.BaseUrl}{kv.Value}";
                }
            });
        }
        catch { }
    }

    /* Delete chat */
    private void DeleteChat(ChatItem? item)
    {
        if (item is null) return;
        var id = item.Id;

        if (string.Equals(id, "Lobby", StringComparison.OrdinalIgnoreCase))
            return;

        _hiddenChats.Add(id);

        var existing = Chats.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            if (!string.IsNullOrWhiteSpace(existing.Label))
                _hiddenChatLabels[id] = existing.Label;
            Chats.Remove(existing);
        }

        if (SelectedChat?.Id == id)
        {
            var next = Chats.FirstOrDefault(c => !_hiddenChats.Contains(c.Id))
                       ?? Chats.FirstOrDefault(c => string.Equals(c.Id, "Lobby", StringComparison.OrdinalIgnoreCase))
                       ?? Chats.FirstOrDefault();
            SelectedChat = next;
        }

        UpdateMessagePlaceholder();
        OnPropertyChanged(nameof(CanSend));
    }

    /* Server name update helper */
    private async Task SafeSetDisplayNameOnServerAsync(string newName)
    {
        try { await _chat.ChangeDisplayNameAsync(newName); }
        catch { }
    }

    /* Self presence & known user helpers */
    private void EnsureSelfPresenceOnline()
    {
        var me = Canon(User);
        if (string.IsNullOrWhiteSpace(me)) return;

        _statusByName[me] = PresenceStatus.Online;
        EnsureSelfInOnlineListFor(PresenceStatus.Online);
        RecomputePeople(OnlineUsers);
    }

    private void EnsureSelfInOnlineListFor(PresenceStatus status)
    {
        var me = Canon(User);
        if (string.IsNullOrWhiteSpace(me)) return;

        var shouldBeOnline = status == PresenceStatus.Online || status == PresenceStatus.Busy || status == PresenceStatus.Away;

        var exists = OnlineUsers.Any(x => Ci.Equals(x, me));
        if (shouldBeOnline && !exists) OnlineUsers.Add(me);
        if (!shouldBeOnline && exists)
        {
            var match = OnlineUsers.FirstOrDefault(x => Ci.Equals(x, me));
            if (match != null) OnlineUsers.Remove(match);
        }
    }

    private void EnsureSelfKnownUser()
    {
        var me = Canon(User);
        if (string.IsNullOrWhiteSpace(me)) return;
        _knownUsers.Add(me);
    }

    private void HarmonizeSelfPresence()
    {
        var me = Canon(User);
        if (string.IsNullOrWhiteSpace(me)) return;

        _statusByName[me] = MyStatus;
        EnsureSelfInOnlineListFor(MyStatus);
    }

    /* Navigation helpers */
    private async Task HardReturnToLobbyUIAsync()
    {
        await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            try
            {
                var nav = GetRootPage()?.Navigation;
                if (nav != null)
                {
                    while (nav.ModalStack.Count > 0)
                        await nav.PopModalAsync(animated: false);

                    for (int i = nav.NavigationStack.Count - 1; i >= 0; i--)
                    {
                        var p = nav.NavigationStack[i];
                        var name = p?.GetType()?.Name ?? string.Empty;
                        if (name.IndexOf("Settings", StringComparison.OrdinalIgnoreCase) >= 0)
                            nav.RemovePage(p);
                    }
                }

                if (GetRootPage() is Shell shell)
                {
                    try { await shell.GoToAsync("///ChatPage", false); } catch { }
                    try { await shell.GoToAsync("//ChatPage", false); } catch { }
                    try { await shell.GoToAsync("//Main/ChatPage", false); } catch { }
                }
            }
            catch { }

            GoToLobby();
        });
    }

    private void GoToLobby()
    {
        var lobby = Chats.FirstOrDefault(c => string.Equals(c.Id, "Lobby", StringComparison.OrdinalIgnoreCase));
        if (lobby is null)
        {
            lobby = new ChatItem("Lobby") { Label = "Lobby" };
            Chats.Insert(0, lobby);
        }
        else
        {
            if (Chats.IndexOf(lobby) != 0)
                Chats.Move(Chats.IndexOf(lobby), 0);
        }

        SelectedChat = lobby;
    }
}
