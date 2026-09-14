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
using Microsoft.Maui.Controls;
using Chatter.Client.Helpers;
using Chatter.Shared.Models;

namespace Chatter.Client.ViewModels;

public partial class ChatViewModel : ObservableObject
{
    /* Core services & constants */
    private readonly ChatService _chat;

    // De-dup only for synthetic lines (rename/system notices) that have no server-assigned id.
    // Real messages are de-duped by id instead - see ChatMessageReceived/DmNotify below.
    private readonly Dictionary<string, string?> _lastSystemLineByChat = new(StringComparer.OrdinalIgnoreCase);

    /* Root page helper to avoid obsolete Application.MainPage */
    private static Page? GetRootPage() => Application.Current?.Windows?.FirstOrDefault()?.Page;

    /* User input & typing indicator */
    [ObservableProperty] public partial string User { get; set; } = string.Empty;
    [ObservableProperty] public partial string? OutgoingMessage { get; set; }
    [ObservableProperty] public partial string MessagePlaceholder { get; set; } = "Type a message…";

    private CancellationTokenSource? _typingCts;
    [ObservableProperty] public partial bool IsPeerTyping { get; set; }
    [ObservableProperty] public partial string? TypingUser { get; set; }

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

    /* Legacy/global lists */
    public ObservableCollection<string> Messages { get; } = new();
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
    public ChatViewModel(ChatService chat)
    {
        _chat = chat;

        People.CollectionChanged += (_, __) => OnPropertyChanged(nameof(OfflineCount));
        OnlineUsers.CollectionChanged += (_, __) => OnPropertyChanged(nameof(OfflineCount));

        _chat.TypingChanged += (_, e) =>
        {
            if (e.ChannelId == CurrentChannelId && e.User != User)
            {
                IsPeerTyping = e.IsTyping;
                TypingUser = e.IsTyping ? e.User : null;
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

        _chat.MessageReceived += (u, m) =>
            MainThread.BeginInvokeOnMainThread(() => Messages.Add($"{u}: {m}"));

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
                    EnsureChatItemWithLabel(summary.Id, summary.Label);

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

        _chat.DmNotify += (chatId, messageId, fromUser, msg, sentAtUtc) =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                _knownUsers.Add(Canon(fromUser));
                var chatItem = EnsureChatItemWithLabel(chatId, fromUser);

                if (!_chatMessages.TryGetValue(chatId, out var list))
                    _chatMessages[chatId] = list = new ObservableCollection<ChatMessageItem>();

                if (messageId > 0 && list.Any(x => x.Id == messageId)) return;

                var msgItem = new ChatMessageItem(messageId, fromUser, msg, sentAtUtc, isMine: false, isSystem: false);
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

        _chat.ChatMessageReceived += (chatId, messageId, u, m, sentAtUtc) =>
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

                var item = new ChatMessageItem(messageId, u, m, sentAtUtc, isMine: Ci.Equals(u, User), isSystem: isSystem);
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
                    msg.Reactions.Add(new ReactionItem(messageId, emoji, count, reactedByMe));
                else
                {
                    existing.Count = count;
                    existing.ReactedByMe = reactedByMe;
                }
            });

        _chat.ReadReceipt += (chatId, fromDisplayName, lastReadMessageId) =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (!_chatMessages.TryGetValue(chatId, out var list)) return;

                var latestMine = list.Where(m => m.IsMine && m.Id > 0).OrderByDescending(m => m.Id).FirstOrDefault();
                foreach (var m in list.Where(m => m.IsMine)) m.SeenByOther = false;
                if (latestMine is not null && latestMine.Id <= lastReadMessageId)
                    latestMine.SeenByOther = true;
            });

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

    private ChatItem EnsureChatItemWithLabel(string chatId, string label)
    {
        var item = Chats.FirstOrDefault(c => Ci.Equals(c.Id, chatId));
        if (item is null)
        {
            item = new ChatItem(chatId) { Label = label };
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

        if (value is not null && !value.Id.StartsWith("draft:", StringComparison.OrdinalIgnoreCase))
        {
            _ = _chat.JoinChatAsync(value.Id);
            _ = LoadHistoryIfNeededAsync(value.Id);
        }
    }

    private ChatMessageItem ToItem(ChatMessageDto dto)
    {
        var item = new ChatMessageItem(dto.Id, dto.Sender, dto.Body, dto.SentAtUtc,
            isMine: Ci.Equals(dto.Sender, User), isSystem: false)
        {
            EditedAtUtc = dto.EditedAtUtc,
            IsDeleted = dto.IsDeleted,
        };

        foreach (var r in dto.Reactions)
            item.Reactions.Add(new ReactionItem(dto.Id, r.Emoji, r.Count, r.ReactedByMe));

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
            Messages.Add("📶 Connected to server.");

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

        await _chat.SendToChatAsync(SelectedChat.Id, msg);
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
