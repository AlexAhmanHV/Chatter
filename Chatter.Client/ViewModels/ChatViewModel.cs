// ViewModels/ChatViewModel.cs
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Chatter.Client.Messages;
using Chatter.Client.Models;     // UserPresenceItem, PresenceStatus
using Chatter.Client.Services;
using Chatter.Client.Views;      // EmojiHelpPage
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.Client.ViewModels;

public partial class ChatViewModel : ObservableObject
{
    private readonly ChatService _chat;
    private readonly Dictionary<string, string?> _lastLineByChat = new(StringComparer.OrdinalIgnoreCase);
    private const string BaseUrl = "http://localhost:5291";

    [ObservableProperty] private string user = string.Empty;
    [ObservableProperty] private string? outgoingMessage;
    [ObservableProperty] private string messagePlaceholder = "Type a message… (try :smile:, :party:)";

    private CancellationTokenSource? _typingCts;

    [ObservableProperty] private bool isPeerTyping;
    [ObservableProperty] private string? typingUser;

    // Route typing/etc. safely before a selection is made
    private string CurrentChannelId => SelectedChat?.Id ?? "Lobby";

    // RIGHT: roster (people + status)
    public ObservableCollection<UserPresenceItem> People { get; } = new();

    private readonly HashSet<string> _knownUsers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _hiddenChats = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PresenceStatus> _statusByName = new(StringComparer.OrdinalIgnoreCase);

    [ObservableProperty] private bool isActive;

    // LEFT: chats
    public ObservableCollection<ChatItem> Chats { get; } = new();
    [ObservableProperty] private ChatItem? selectedChat;

    // MIDDLE: current chat messages
    public ObservableCollection<string> CurrentChatMessages { get; } = new();

    private readonly Dictionary<string, ObservableCollection<string>> _chatMessages =
        new(StringComparer.OrdinalIgnoreCase);

    // Optional legacy broadcast
    public ObservableCollection<string> Messages { get; } = new();

    // Quick legacy list of online names (canonical)
    public ObservableCollection<string> OnlineUsers { get; } = new();

    public bool CanSend => !string.IsNullOrWhiteSpace(OutgoingMessage) && SelectedChat != null;
    public int OfflineCount => Math.Max(0, People.Count - OnlineUsers.Count);

    public IAsyncRelayCommand ConnectCommand { get; }
    public IAsyncRelayCommand SendCommand { get; }
    public IAsyncRelayCommand<string> StartDmCommand { get; }
    public IRelayCommand<ChatItem> DeleteChatCommand { get; }

    private static bool IsDraftId(string id) => id.StartsWith("draft:", StringComparison.OrdinalIgnoreCase);
    private static string DraftOf(string other) => $"draft:{other}";
    private static string DraftLabel(string other) => $"{other} (draft)";

    private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;

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

    [RelayCommand]
    private async Task ShowEmojiHelpAsync()
    {
        await Application.Current!.MainPage!.Navigation.PushModalAsync(new EmojiHelpPage());
    }

    // ===== Presence (self) =====
    [ObservableProperty] private PresenceStatus myStatus = PresenceStatus.Online;
    private bool _suppressStatusSend;

    // Dedup rename: ignore if a second rename arrives within this window or with same value
    private long _lastRenameTicks;
    private string? _lastRenameValue;
    private int _isHandlingRename; // 0/1 interlocked reentrancy guard

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

    public IAsyncRelayCommand<PresenceStatus> SetMyStatusCommand { get; }

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

        foreach (var chat in Chats)
            if (chat.Id.StartsWith("dm:", StringComparison.OrdinalIgnoreCase))
                chat.Label = ComputeChatLabel(chat.Id);

        UpdateMessagePlaceholder();
    }

    // Typing indicator + CanSend refresh
    partial void OnOutgoingMessageChanged(string? value)
    {
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
        try { await _chat.SendTypingAsync(CurrentChannelId, User, isTyping); }
        catch { }
    }
    private async Task DelayedStopTypingAsync(CancellationToken ct)
    {
        try { await Task.Delay(1500, ct); await NotifyTypingAsync(false); }
        catch (TaskCanceledException) { }
    }

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
                foreach (var chat in Chats)
                    if (chat.Id.StartsWith("dm:", StringComparison.OrdinalIgnoreCase))
                        chat.Label = ComputeChatLabel(chat.Id);
            });
        };

        // Presence snapshots / deltas
        try
        {
            _chat.StatusesUpdated += dict =>
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    foreach (var kv in dict)
                        _statusByName[Canon(kv.Key)] = ParseStatus(kv.Value);

                    // Ensure "me" reflects the picker (MyStatus) even if the snapshot is stale
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

                    // If the change was about me, align caches with the UI picker
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

                EnsureSelfKnownUser();     // make sure I’m in the directory
                HarmonizeSelfPresence();   // keep my caches consistent
                RecomputePeople(onlineList);
            });

        _chat.ChatsForMeUpdated += list =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                var shouldHave = new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);

                foreach (var id in list)
                {
                    if (_hiddenChats.Contains(id)) continue;
                    var item = EnsureChatItem(id);
                    item.Label = ComputeChatLabel(id);
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

                UpdateMessagePlaceholder();
            });

        _chat.ChatMessageReceived += (chatId, u, m) =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (_hiddenChats.Remove(chatId))
                {
                    var unhidden = EnsureChatItem(chatId);
                    unhidden.Label = ComputeChatLabel(chatId);
                }

                var line = string.Equals(u, "system", StringComparison.OrdinalIgnoreCase) ? m : $"{u}: {m}";

                if (_lastLineByChat.TryGetValue(chatId, out var last) && last == line) return;
                _lastLineByChat[chatId] = line;

                if (!_chatMessages.TryGetValue(chatId, out var list))
                    _chatMessages[chatId] = list = new ObservableCollection<string>();
                list.Add(line);

                bool isViewingThis = IsActive && SelectedChat?.Id == chatId;

                if (isViewingThis) CurrentChatMessages.Add(line);
                else EnsureChatItem(chatId).Unread++;
            });

        // ===== RENAME HANDLER (de-duped + HARD RETURN + presence harmonization) =====
        WeakReferenceMessenger.Default.Register<DisplayNameChangedMessage>(this, async (_, msg) =>
        {
            var newName = msg.Value?.Trim() ?? string.Empty;
            if (Ci.Equals(newName, User)) return;

            var nowTicks = DateTime.UtcNow.Ticks;
            if (Ci.Equals(newName, _lastRenameValue) && nowTicks - _lastRenameTicks < TimeSpan.FromSeconds(2).Ticks)
                return;

            if (Interlocked.Exchange(ref _isHandlingRename, 1) == 1) return; // already handling
            _lastRenameTicks = nowTicks;
            _lastRenameValue = newName;

            try
            {
                var oldName = User;
                User = newName;

                await SafeSetDisplayNameOnServerAsync(User);

                foreach (var chatItem in Chats)
                    chatItem.Label = ComputeChatLabel(chatItem.Id);

                if (!string.IsNullOrWhiteSpace(oldName) && !Ci.Equals(oldName, User))
                    RenameKnownUser(oldName, User);

                // Make sure I'm present in all caches and Online
                EnsureSelfKnownUser();
                HarmonizeSelfPresence();

                // Hard return to Chat + select Lobby
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
    }

    private string ComputeChatLabel(string chatId)
    {
        if (IsDraftId(chatId))
        {
            var other = ResolveName(chatId.Substring("draft:".Length));
            return DraftLabel(other);
        }

        if (string.Equals(chatId, "Lobby", StringComparison.OrdinalIgnoreCase))
            return "Lobby";

        if (chatId.StartsWith("dm:", StringComparison.OrdinalIgnoreCase))
        {
            var body = chatId.Substring(3);
            var parts = body.Split('|');
            var meCanon = Canon(User);
            var other = parts.FirstOrDefault(p => !Ci.Equals(Canon(p), meCanon));
            return other is null ? chatId : FormatNameWithAliases(other);
        }

        return chatId;
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

    private void SwapDraftToReal(string draftId, string realChatId)
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
            var newItem = new ChatItem(realChatId) { Label = ComputeChatLabel(realChatId) };
            Chats.Insert(idx, newItem);
            SelectedChat = newItem;
        }
        else
        {
            var newItem = EnsureChatItem(realChatId);
            newItem.Label = ComputeChatLabel(realChatId);
            SelectedChat = newItem;
        }

        RefreshVisibleChat();
    }

    partial void OnSelectedChatChanged(ChatItem? value)
    {
        RefreshVisibleChat();
        UpdateMessagePlaceholder();

        OnPropertyChanged(nameof(CanSend));

        if (value is not null && !value.Id.StartsWith("draft:", StringComparison.OrdinalIgnoreCase))
        {
            _ = _chat.JoinChatAsync(value.Id);
        }
    }

    private void UpdateMessagePlaceholder()
    {
        if (SelectedChat is null)
        {
            MessagePlaceholder = "Type a message… (try :smile:, :party:)";
            return;
        }

        var id = SelectedChat.Id;

        if (string.Equals(id, "Lobby", StringComparison.OrdinalIgnoreCase))
        {
            MessagePlaceholder = "Write a message in the lobby... (try :smile:, :party:)";
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
            var body = id.Substring(3);
            var parts = body.Split('|');
            var meCanon = Canon(User);
            var other = parts.FirstOrDefault(p => !Ci.Equals(Canon(p), meCanon));
            MessagePlaceholder = $"Type a message to {FormatNameWithAliases(other ?? "this chat")} (try :smile:, :party:)";
            return;
        }

        MessagePlaceholder = $"Type a message to {ComputeChatLabel(id)} (try :smile:, :party:)";
    }

    public void RefreshVisibleChat()
    {
        CurrentChatMessages.Clear();
        if (SelectedChat is null) return;

        if (_chatMessages.TryGetValue(SelectedChat.Id, out var list))
            foreach (var line in list)
                CurrentChatMessages.Add(line);

        SelectedChat.Unread = 0;
    }

    private async Task ConnectAsync()
    {
        try
        {
            await _chat.StartAsync(BaseUrl);
            Messages.Add("📶 Connected to server.");

            if (!string.IsNullOrWhiteSpace(User))
                await SafeSetDisplayNameOnServerAsync(User);

            EnsureSelfKnownUser();
            HarmonizeSelfPresence();

            try
            {
                var aliasMethod = _chat.GetType().GetMethod("GetNameAliasesAsync", Type.EmptyTypes);
                if (aliasMethod is not null)
                {
                    var task = (Task<Dictionary<string, string>>)aliasMethod.Invoke(_chat, null)!;
                    var dict = await task.ConfigureAwait(false);
                    if (dict is not null)
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
                        foreach (var chat in Chats)
                            if (chat.Id.StartsWith("dm:", StringComparison.OrdinalIgnoreCase))
                                chat.Label = ComputeChatLabel(chat.Id);
                    }
                }

                var getStatuses = _chat.GetType().GetMethod("GetStatusesAsync", Type.EmptyTypes);
                if (getStatuses is not null)
                {
                    var stTask = (Task<Dictionary<string, string>>)getStatuses.Invoke(_chat, null)!;
                    var statuses = await stTask.ConfigureAwait(false);
                    if (statuses is not null)
                    {
                        foreach (var kv in statuses)
                            _statusByName[Canon(kv.Key)] = ParseStatus(kv.Value);

                        HarmonizeSelfPresence(); // keep my caches consistent after snapshot
                        RecomputePeople(OnlineUsers);

                        var meCanon = Canon(User);
                        if (statuses.TryGetValue(meCanon, out var mine) && !string.IsNullOrWhiteSpace(mine))
                            SetMyStatusFromServer(ParseStatus(mine));
                    }
                }
            }
            catch { }

            await HardReturnToLobbyUIAsync();
            _ = _chat.JoinChatAsync("Lobby");
        }
        catch (Exception ex)
        {
            await Application.Current.MainPage.DisplayAlert("Connect failed", ex.Message, "OK");
        }
    }

    private async Task SendAsync()
    {
        if (string.IsNullOrWhiteSpace(User))
        {
            await Application.Current.MainPage.DisplayAlert("Pick a username", "Please enter a username first.", "OK");
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

            var realChatId = await TrySendDmFirstAsync(other, User, msg);

            if (string.IsNullOrWhiteSpace(realChatId))
            {
                realChatId = await _chat.CreateDmAsync(other);
                if (!string.IsNullOrWhiteSpace(realChatId))
                    await _chat.SendToChatAsync(realChatId, User, msg);
            }

            if (!string.IsNullOrWhiteSpace(realChatId))
            {
                SwapDraftToReal(SelectedChat.Id, realChatId);
                _ = _chat.JoinChatAsync(realChatId);
            }

            return;
        }

        await _chat.SendToChatAsync(SelectedChat.Id, User, msg);
    }

    private async Task<string?> TrySendDmFirstAsync(string otherDisplayName, string fromUser, string message)
    {
        var m = _chat.GetType().GetMethod("SendDmFirstAsync", new[] { typeof(string), typeof(string), typeof(string) });
        if (m is null) return null;

        var task = (Task<string>)m.Invoke(_chat, new object[] { otherDisplayName, fromUser, message })!;
        var chatId = await task.ConfigureAwait(false);
        return chatId;
    }

    private async Task StartDmAsync(string otherDisplayName)
    {
        if (string.IsNullOrWhiteSpace(otherDisplayName) ||
            otherDisplayName.Equals(User, StringComparison.OrdinalIgnoreCase))
            return;

        var draftId = DraftOf(otherDisplayName);
        var item = EnsureChatItem(draftId);
        item.Label = DraftLabel(otherDisplayName);
        SelectedChat = item;

        _knownUsers.Add(ResolveName(otherDisplayName));
        RecomputePeople(OnlineUsers);
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

        try
        {
            var m = _chat.GetType().GetMethod("SetStatusAsync", new[] { typeof(string) });
            if (m is not null)
            {
                var t = (Task)m.Invoke(_chat, new object[] { StatusToWire(status) })!;
                await t.ConfigureAwait(false);
            }
        }
        catch { }
    }

    public IReadOnlyList<PresenceStatus> StatusOptions { get; } =
        new[] { PresenceStatus.Online, PresenceStatus.Away, PresenceStatus.Busy };

    private void NormalizeKnownUsers()
    {
        var canon = _knownUsers.Select(Canon).Distinct(Ci).ToList();
        _knownUsers.Clear();
        foreach (var n in canon) _knownUsers.Add(n);
    }

    private void RecomputePeople(IEnumerable<string> onlineNow)
    {
        var onlineSet = new HashSet<string>(onlineNow.Select(Canon), Ci);

        // Seed from DM chats (partners appear even if offline). Skip myself.
        foreach (var chat in Chats)
        {
            if (chat.Id.StartsWith("dm:", StringComparison.OrdinalIgnoreCase))
            {
                var body = chat.Id.Substring(3);
                foreach (var p in body.Split('|'))
                {
                    if (!Ci.Equals(p, User))
                        _knownUsers.Add(Canon(p));
                }
            }
        }

        // Ensure I am in the directory
        EnsureSelfKnownUser();

        // Build canonical list, sorted: online first, then by name
        var names = _knownUsers.Select(Canon).Distinct(Ci).ToList();
        names.Sort((a, b) =>
        {
            var aOn = onlineSet.Contains(a);
            var bOn = onlineSet.Contains(b);
            var cmp = bOn.CompareTo(aOn);
            return cmp != 0 ? cmp : StringComparer.OrdinalIgnoreCase.Compare(a, b);
        });

        // Remove stale / non-canonical entries
        for (int i = People.Count - 1; i >= 0; i--)
        {
            var name = People[i].Name;
            var isCanonical = Ci.Equals(name, Canon(name));
            var stillExists = names.Contains(name, Ci);
            if (!isCanonical || !stillExists)
                People.RemoveAt(i);
        }

        // Add/update with computed status: server value if present, else onlineSet, else Offline
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

        // Reorder to match sorted list
        for (int i = 0; i < names.Count; i++)
        {
            var idx = People.IndexOf(People.First(p => Ci.Equals(p.Name, names[i])));
            if (idx != i) People.Move(idx, i);
        }

        // Keep quick list in sync with the input set
        OnlineUsers.Clear();
        foreach (var n in onlineSet) OnlineUsers.Add(n);
    }

    private void DeleteChat(ChatItem? item)
    {
        if (item is null) return;
        var id = item.Id;

        if (string.Equals(id, "Lobby", StringComparison.OrdinalIgnoreCase))
            return;

        _hiddenChats.Add(id);

        var existing = Chats.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
        if (existing != null) Chats.Remove(existing);

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

    // ===== Helpers =====

    private async Task SafeSetDisplayNameOnServerAsync(string newName)
    {
        try
        {
            var change = _chat.GetType().GetMethod("ChangeDisplayNameAsync", new[] { typeof(string) });
            if (change is not null)
            {
                var t = (Task)change.Invoke(_chat, new object[] { newName })!;
                await t.ConfigureAwait(false);
                return;
            }

            var set = _chat.GetType().GetMethod("SetDisplayNameAsync", new[] { typeof(string) });
            if (set is not null)
            {
                var t = (Task)set.Invoke(_chat, new object[] { newName })!;
                await t.ConfigureAwait(false);
            }
        }
        catch { }
    }

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

        // Keep caches consistent with the picker
        _statusByName[me] = MyStatus;
        EnsureSelfInOnlineListFor(MyStatus);
    }

    // HARD return: close modals, purge settings from back stack, navigate to Chat, select Lobby
    private async Task HardReturnToLobbyUIAsync()
    {
        await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            try
            {
                var nav = Application.Current?.MainPage?.Navigation;
                if (nav != null)
                {
                    // Close all modals
                    while (nav.ModalStack.Count > 0)
                        await nav.PopModalAsync(animated: false);

                    // Purge any Settings pages from the back stack
                    for (int i = nav.NavigationStack.Count - 1; i >= 0; i--)
                    {
                        var p = nav.NavigationStack[i];
                        var name = p?.GetType()?.Name ?? string.Empty;
                        if (name.IndexOf("Settings", StringComparison.OrdinalIgnoreCase) >= 0)
                            nav.RemovePage(p);
                    }
                }

                if (Application.Current?.MainPage is Shell shell)
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

    [RelayCommand]
private async Task ShowEmojiPickerAsync()
{
    await Application.Current!.MainPage!.Navigation.PushModalAsync(new EmojiPickerPage(this));
}
}
