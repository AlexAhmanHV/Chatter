// ViewModels/ChatViewModel.cs
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Chatter.Client.Messages;
using Chatter.Client.Models;     // UserPresenceItem, PresenceStatus
using Chatter.Client.Services;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Chatter.Client.ViewModels;

public partial class ChatViewModel : ObservableObject
{
    private readonly ChatService _chat;
    private readonly Dictionary<string, string?> _lastLineByChat = new(StringComparer.OrdinalIgnoreCase);
    private const string BaseUrl = "http://localhost:5291";

    [ObservableProperty] private string user = string.Empty;
    [ObservableProperty] private string? outgoingMessage;
    [ObservableProperty] private string messagePlaceholder = "Type a message…";

    // RIGHT: roster (people + status)
    public ObservableCollection<UserPresenceItem> People { get; } = new();

    // Track directory of everyone we've encountered (DMs / roster), case-insensitive
    private readonly HashSet<string> _knownUsers = new(StringComparer.OrdinalIgnoreCase);

    // Locally hidden chats (UI-only remove)
    private readonly HashSet<string> _hiddenChats = new(StringComparer.OrdinalIgnoreCase);

    // Presence cache: canonical name -> status
    private readonly Dictionary<string, PresenceStatus> _statusByName = new(StringComparer.OrdinalIgnoreCase);

    // whether ChatPage is currently visible
    [ObservableProperty] private bool isActive;

    // LEFT: chats
    public ObservableCollection<ChatItem> Chats { get; } = new();
    [ObservableProperty] private ChatItem? selectedChat;

    // MIDDLE: current chat messages
    public ObservableCollection<string> CurrentChatMessages { get; } = new();

    // Per-chat message buffers
    private readonly Dictionary<string, ObservableCollection<string>> _chatMessages =
        new(StringComparer.OrdinalIgnoreCase);

    // Optional legacy broadcast
    public ObservableCollection<string> Messages { get; } = new();

    // Quick legacy list of online names (canonical)
    public ObservableCollection<string> OnlineUsers { get; } = new();

    // UI helpers
    public bool CanSend => !string.IsNullOrWhiteSpace(OutgoingMessage) && SelectedChat != null;
    public int OfflineCount => Math.Max(0, People.Count - OnlineUsers.Count);

    public IAsyncRelayCommand ConnectCommand { get; }
    public IAsyncRelayCommand SendCommand { get; }
    public IAsyncRelayCommand<string> StartDmCommand { get; }
    public IRelayCommand<ChatItem> DeleteChatCommand { get; }

    // ---- Draft helpers ----
    private static bool IsDraftId(string id) => id.StartsWith("draft:", StringComparison.OrdinalIgnoreCase);
    private static string DraftOf(string other) => $"draft:{other}";
    private static string DraftLabel(string other) => $"{other} (draft)";

    // ---- Name aliasing & history ----
    private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;

    // old -> newer (may chain)
    private readonly Dictionary<string, string> _nameAliases = new(StringComparer.OrdinalIgnoreCase);

    // canonical current name -> list of previous names (for "Alex (Alexander, Alexa)")
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

    // If you expose status control in UI
    [ObservableProperty] private PresenceStatus myStatus = PresenceStatus.Online;
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

    // Canonical resolver with path compression
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

        // Merge history buckets (+include old canonical)
        var oldHist = _aliasHistoryByCurrent.TryGetValue(oldCur, out var oh) ? oh : new List<string>();
        var newHist = _aliasHistoryByCurrent.TryGetValue(newCur, out var nh) ? nh : new List<string>();
        foreach (var s in oldHist) if (!newHist.Any(x => Ci.Equals(x, s))) newHist.Add(s);
        if (!newHist.Any(x => Ci.Equals(x, oldCur))) newHist.Add(oldCur);
        _aliasHistoryByCurrent[newCur] = newHist;
        _aliasHistoryByCurrent.Remove(oldCur);

        // Directory swap
        if (_knownUsers.RemoveWhere(n => Ci.Equals(Canon(n), oldCur)) > 0)
            _knownUsers.Add(newCur);

        // Remove any People entries backed by the old canonical; they’ll get re-added
        for (int i = People.Count - 1; i >= 0; i--)
            if (Ci.Equals(Canon(People[i].Name), oldCur))
                People.RemoveAt(i);

        // Move status
        if (_statusByName.Remove(oldCur, out var st))
            _statusByName[newCur] = st;

        NormalizeKnownUsers();
        RecomputePeople(OnlineUsers);

        // Fix DM labels
        foreach (var chat in Chats)
            if (chat.Id.StartsWith("dm:", StringComparison.OrdinalIgnoreCase))
                chat.Label = ComputeChatLabel(chat.Id);

        UpdateMessagePlaceholder();
    }

    // Keep CanSend reactive to text changes
    partial void OnOutgoingMessageChanged(string? value) => OnPropertyChanged(nameof(CanSend));

    public ChatViewModel(ChatService chat)
    {
        _chat = chat;

        People.CollectionChanged += (_, __) => OnPropertyChanged(nameof(OfflineCount));
        OnlineUsers.CollectionChanged += (_, __) => OnPropertyChanged(nameof(OfflineCount));

        // Others rename → collapse aliases
        _chat.OtherDisplayNameChanged += (oldName, newName) =>
            MainThread.BeginInvokeOnMainThread(() => RenameKnownUser(oldName, newName));

        // Optional: server sends alias map snapshot
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

        // Statuses broadcast (name -> "online"/"away"/"busy"/"offline")
        // If your ChatService exposes 'StatusesUpdated'
        try
        {
            _chat.StatusesUpdated += dict =>
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    _statusByName.Clear();
                    foreach (var kv in dict)
                        _statusByName[Canon(kv.Key)] = ParseStatus(kv.Value);
                    RecomputePeople(OnlineUsers);
                });
        }
        catch { /* if not present, no-op */ }

        // Legacy broadcast
        _chat.MessageReceived += (u, m) =>
            MainThread.BeginInvokeOnMainThread(() => Messages.Add($"{u}: {m}"));

        // Roster
        _chat.OnlineUsersUpdated += onlineList =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                foreach (var n in onlineList)
                    _knownUsers.Add(Canon(n));
                RecomputePeople(onlineList);
            });

        // Chats list
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

                // drop chats that server no longer lists (keep drafts)
                for (int i = Chats.Count - 1; i >= 0; i--)
                {
                    var cid = Chats[i].Id;
                    if (IsDraftId(cid)) continue;
                    if (!shouldHave.Contains(cid))
                        Chats.RemoveAt(i);
                }

                // keep Lobby on top
                var lobby = Chats.FirstOrDefault(c => c.Id.Equals("Lobby", StringComparison.OrdinalIgnoreCase));
                if (lobby is not null && Chats.IndexOf(lobby) != 0)
                    Chats.Move(Chats.IndexOf(lobby), 0);

                if (SelectedChat is null && Chats.Count > 0)
                    SelectedChat = Chats[0];

                UpdateMessagePlaceholder();
            });

        // Messages from joined chats
        _chat.ChatMessageReceived += (chatId, u, m) =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                // Unhide on activity
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

        // New chat created (recipient)
        _chat.AddedChat += (chatId, fromUser) =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (Ci.Equals(fromUser, User)) return;
                if (_hiddenChats.Contains(chatId)) return;

                var existing = Chats.FirstOrDefault(c => c.Id.Equals(chatId, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                {
                    existing.Label = ComputeChatLabel(chatId);
                    return;
                }

                var item = EnsureChatItem(chatId);
                item.Label = ComputeChatLabel(chatId);
            });

        // DM notify (recipient not in group)
        _chat.DmNotify += (chatId, fromUser, msg) =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (_hiddenChats.Remove(chatId))
                {
                    var unhidden = EnsureChatItem(chatId);
                    unhidden.Label = ComputeChatLabel(chatId);
                }

                var line = $"{fromUser}: {msg}";

                if (_lastLineByChat.TryGetValue(chatId, out var last) && last == line) return;
                _lastLineByChat[chatId] = line;

                if (!_chatMessages.TryGetValue(chatId, out var list))
                    _chatMessages[chatId] = list = new ObservableCollection<string>();
                list.Add(line);

                bool isViewingThis = IsActive && SelectedChat?.Id == chatId;

                if (isViewingThis) CurrentChatMessages.Add(line);
                else EnsureChatItem(chatId).Unread++;
            });

        // Self: display name changed
        WeakReferenceMessenger.Default.Register<DisplayNameChangedMessage>(this, async (_, msg) =>
        {
            var oldName = User;
            User = msg.Value;

            try { await _chat.ChangeDisplayNameAsync(User); } catch { /* ignore */ }

            foreach (var chatItem in Chats)
                chatItem.Label = ComputeChatLabel(chatItem.Id);

            if (!string.IsNullOrWhiteSpace(oldName) && !Ci.Equals(oldName, User))
                RenameKnownUser(oldName, User);

            UpdateMessagePlaceholder();
        });

        _chat.OnGetCurrentDisplayName = () => User;

        ConnectCommand = new AsyncRelayCommand(ConnectAsync);
        SendCommand = new AsyncRelayCommand(SendAsync);
        StartDmCommand = new AsyncRelayCommand<string>(StartDmAsync);
        DeleteChatCommand = new RelayCommand<ChatItem>(DeleteChat);
        SetMyStatusCommand = new AsyncRelayCommand<PresenceStatus>(SetMyStatusAsync);
    }

    // Labels for chats
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
        var item = Chats.FirstOrDefault(c => c.Id.Equals(chatId, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            item = new ChatItem(chatId) { Label = ComputeChatLabel(chatId) };
            Chats.Add(item);
        }
        return item;
    }

    // Swap draft -> real chat
    private void SwapDraftToReal(string draftId, string realChatId)
    {
        if (_chatMessages.TryGetValue(draftId, out var draftMsgs))
        {
            _chatMessages[realChatId] = draftMsgs;
            _chatMessages.Remove(draftId);
        }

        var draftItem = Chats.FirstOrDefault(c => c.Id.Equals(draftId, StringComparison.OrdinalIgnoreCase));
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

    // When selection changes
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
            MessagePlaceholder = $"Type a message to {other}";
            return;
        }

        if (id.StartsWith("dm:", StringComparison.OrdinalIgnoreCase))
        {
            var body = id.Substring(3);
            var parts = body.Split('|');
            var meCanon = Canon(User);
            var other = parts.FirstOrDefault(p => !Ci.Equals(Canon(p), meCanon));
            MessagePlaceholder = $"Type a message to {FormatNameWithAliases(other ?? "this chat")}";
            return;
        }

        MessagePlaceholder = $"Type a message to {ComputeChatLabel(id)}";
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
                await _chat.SetDisplayNameAsync(User);

            // (Best-effort) pull initial alias & status snapshots if ChatService offers them
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
                        _statusByName.Clear();
                        foreach (var kv in statuses)
                            _statusByName[Canon(kv.Key)] = ParseStatus(kv.Value);
                        RecomputePeople(OnlineUsers);
                    }
                }
            }
            catch { /* snapshot is optional */ }
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

        // Ensure the draft target shows in People (offline until online)
        _knownUsers.Add(ResolveName(otherDisplayName));
        RecomputePeople(OnlineUsers);
    }

    private async Task SetMyStatusAsync(PresenceStatus status)
    {
        MyStatus = status;

        // Optimistically reflect locally
        var me = Canon(User);
        _statusByName[me] = status;
        RecomputePeople(OnlineUsers);

        // Ask server (if API available)
        try
        {
            var m = _chat.GetType().GetMethod("SetStatusAsync", new[] { typeof(string) });
            if (m is not null)
            {
                var t = (Task)m.Invoke(_chat, new object[] { StatusToWire(status) })!;
                await t.ConfigureAwait(false);
            }
        }
        catch { /* ignore */ }
    }

    private void NormalizeKnownUsers()
    {
        var canon = _knownUsers.Select(Canon).Distinct(Ci).ToList();
        _knownUsers.Clear();
        foreach (var n in canon) _knownUsers.Add(n);
    }

    // -------- Presence helper (ONLINE + OFFLINE) --------
    private void RecomputePeople(IEnumerable<string> onlineNow)
    {
        var onlineSet = new HashSet<string>(onlineNow.Select(Canon), Ci);

        // Seed from DM chats (so partners appear even if offline). Skip myself.
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

        // Add/update with computed status:
        // prefer server-provided status; fall back to online/offline by presence list
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
                // mutable Status property → triggers UI color update
                item.Status = status;
            }
        }

        // Reorder to match sorted list
        for (int i = 0; i < names.Count; i++)
        {
            var idx = People.IndexOf(People.First(p => Ci.Equals(p.Name, names[i])));
            if (idx != i) People.Move(idx, i);
        }

        // Keep quick list in sync
        OnlineUsers.Clear();
        foreach (var n in onlineSet) OnlineUsers.Add(n);
    }

    // ---- Local UI "Delete" (hide) a chat; auto-unhide on activity ----
    private void DeleteChat(ChatItem? item)
    {
        if (item is null) return;
        var id = item.Id;

        if (string.Equals(id, "Lobby", StringComparison.OrdinalIgnoreCase))
            return;

        _hiddenChats.Add(id);

        var existing = Chats.FirstOrDefault(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
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
}
