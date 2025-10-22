// ViewModels/ChatViewModel.cs
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Chatter.Client.Messages;
using Chatter.Client.Models;
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

    // All known users with online/offline status
    public ObservableCollection<UserPresenceItem> People { get; } = new();

    // Tracks every user we've ever seen (online or via chats), case-insensitive
    private readonly HashSet<string> _knownUsers = new(StringComparer.OrdinalIgnoreCase);

    public bool CanSend => !string.IsNullOrWhiteSpace(OutgoingMessage) && SelectedChat != null;

    // Handy for the right panel counters (no XAML converter needed)
    public int OfflineCount => Math.Max(0, People.Count - OnlineUsers.Count);

    // whether ChatPage is currently visible
    [ObservableProperty] private bool isActive;

    // RIGHT: roster (legacy list you still reference)
    public ObservableCollection<string> OnlineUsers { get; } = new();

    // LEFT: chats list + selected chat (ChatItem for unread badges)
    public ObservableCollection<ChatItem> Chats { get; } = new();
    [ObservableProperty] private ChatItem? selectedChat;

    // MIDDLE: messages for selected chat
    public ObservableCollection<string> CurrentChatMessages { get; } = new();

    // Per-chat message buffers
    private readonly Dictionary<string, ObservableCollection<string>> _chatMessages =
        new(StringComparer.OrdinalIgnoreCase);

    // Legacy global broadcast (optional)
    public ObservableCollection<string> Messages { get; } = new();

    public IAsyncRelayCommand ConnectCommand { get; }
    public IAsyncRelayCommand SendCommand { get; }
    public IAsyncRelayCommand<string> StartDmCommand { get; }

    // ---- Draft helpers ----
    private static bool IsDraftId(string id) => id.StartsWith("draft:", StringComparison.OrdinalIgnoreCase);
    private static string DraftOf(string other) => $"draft:{other}";
    private static string DraftLabel(string other) => $"{other} (draft)";

    // Keep CanSend in sync with text changes
    partial void OnOutgoingMessageChanged(string? value) => OnPropertyChanged(nameof(CanSend));

    public ChatViewModel(ChatService chat)
    {
        _chat = chat;

        // keep OfflineCount in sync with the collections
        People.CollectionChanged += (_, __) => OnPropertyChanged(nameof(OfflineCount));
        OnlineUsers.CollectionChanged += (_, __) => OnPropertyChanged(nameof(OfflineCount));

        // ===== Legacy broadcasts =====
        _chat.MessageReceived += (u, m) =>
            MainThread.BeginInvokeOnMainThread(() => Messages.Add($"{u}: {m}"));

        // ===== Roster (online + offline) =====
        _chat.OnlineUsersUpdated += onlineList =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                // Track all we've seen
                foreach (var n in onlineList)
                    _knownUsers.Add(n);

                RecomputePeople(onlineList);
            });

        // ===== Chats list =====
        _chat.ChatsForMeUpdated += list =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                var shouldHave = new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);

                foreach (var id in list)
                {
                    var item = EnsureChatItem(id);
                    item.Label = ComputeChatLabel(id);
                }

                // keep local drafts even if server doesn't know them yet
                for (int i = Chats.Count - 1; i >= 0; i--)
                {
                    var cid = Chats[i].Id;
                    if (IsDraftId(cid)) continue;
                    if (!shouldHave.Contains(cid))
                        Chats.RemoveAt(i);
                }

                // 🔝 keep Lobby at index 0
                var lobby = Chats.FirstOrDefault(c => c.Id.Equals("Lobby", StringComparison.OrdinalIgnoreCase));
                if (lobby is not null && Chats.IndexOf(lobby) != 0)
                    Chats.Move(Chats.IndexOf(lobby), 0);

                if (SelectedChat is null && Chats.Count > 0)
                    SelectedChat = Chats[0];

                UpdateMessagePlaceholder();
            });

        // ===== Messages from joined groups =====
        _chat.ChatMessageReceived += (chatId, u, m) =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                var line = string.Equals(u, "system", StringComparison.OrdinalIgnoreCase) ? m : $"{u}: {m}";

                // de-dupe guard
                if (_lastLineByChat.TryGetValue(chatId, out var last) && last == line)
                    return;
                _lastLineByChat[chatId] = line;

                if (!_chatMessages.TryGetValue(chatId, out var list))
                    _chatMessages[chatId] = list = new ObservableCollection<string>();
                list.Add(line);

                bool isViewingThis = IsActive && SelectedChat?.Id == chatId;

                if (isViewingThis)
                {
                    CurrentChatMessages.Add(line);
                }
                else
                {
                    EnsureChatItem(chatId).Unread++;
                }
            });

        // ===== Recipient gets a new chat created but not opened =====
        // Only relevant for the *recipient*. The sender already has a draft swapped to real.
        _chat.AddedChat += (chatId, fromUser) =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                // 1) Ignore the echo on the sender's client
                if (string.Equals(fromUser, User, StringComparison.OrdinalIgnoreCase))
                    return;

                // 2) If we already have this chat (e.g., draft was swapped), don't create/overwrite labels
                var existing = Chats.FirstOrDefault(c => c.Id.Equals(chatId, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                {
                    // Ensure label is correct (other participant), not 'fromUser'
                    existing.Label = ComputeChatLabel(chatId);
                    return;
                }

                // 3) Create the item for the recipient, label via chat id logic
                var item = EnsureChatItem(chatId);
                item.Label = ComputeChatLabel(chatId);
                // no unread bump (no message yet)
            });

        // ===== Recipient gets a DM message (not in group yet) =====
        _chat.DmNotify += (chatId, fromUser, msg) =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                var line = $"{fromUser}: {msg}";

                // de-dupe guard
                if (_lastLineByChat.TryGetValue(chatId, out var last) && last == line)
                    return;
                _lastLineByChat[chatId] = line;

                if (!_chatMessages.TryGetValue(chatId, out var list))
                    _chatMessages[chatId] = list = new ObservableCollection<string>();
                list.Add(line);

                bool isViewingThis = IsActive && SelectedChat?.Id == chatId;

                if (isViewingThis)
                {
                    CurrentChatMessages.Add(line);
                }
                else
                {
                    EnsureChatItem(chatId).Unread++;
                }
            });

        // ===== Settings → display name changed =====
        WeakReferenceMessenger.Default.Register<DisplayNameChangedMessage>(this, async (_, msg) =>
        {
            User = msg.Value;
            try { await _chat.ChangeDisplayNameAsync(User); } catch { /* noop */ }

            // Recompute labels for all chats (our identity changed)
            foreach (var chatItem in Chats)
            {
                chatItem.Label = ComputeChatLabel(chatItem.Id);
            }

            // And refresh the input placeholder once
            UpdateMessagePlaceholder();
        });

        _chat.OnGetCurrentDisplayName = () => User;

        ConnectCommand = new AsyncRelayCommand(ConnectAsync);
        SendCommand = new AsyncRelayCommand(SendAsync);
        StartDmCommand = new AsyncRelayCommand<string>(StartDmAsync);
    }

    // Compute a nice label for each chat (Lobby or the "other user" for DMs)
    private string ComputeChatLabel(string chatId)
    {
        if (IsDraftId(chatId))
        {
            var other = chatId.Substring("draft:".Length);
            return DraftLabel(other);
        }

        if (string.Equals(chatId, "Lobby", StringComparison.OrdinalIgnoreCase))
            return "Lobby";

        if (chatId.StartsWith("dm:", StringComparison.OrdinalIgnoreCase))
        {
            // dm:Alice|Bob → show the other participant (not me)
            var body = chatId.Substring(3);
            var parts = body.Split('|');
            var other = parts.FirstOrDefault(p => !p.Equals(User, StringComparison.OrdinalIgnoreCase));
            return other ?? chatId;
        }

        return chatId; // fallback
    }

    private ChatItem EnsureChatItem(string chatId)
    {
        var item = Chats.FirstOrDefault(c => c.Id.Equals(chatId, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            item = new ChatItem(chatId);
            item.Label = ComputeChatLabel(chatId);
            Chats.Add(item);
        }
        return item;
    }

    // swap a local draft item for a real channel id (and migrate buffers)
    private void SwapDraftToReal(string draftId, string realChatId)
    {
        // Move message buffer (if any)
        if (_chatMessages.TryGetValue(draftId, out var draftMsgs))
        {
            _chatMessages[realChatId] = draftMsgs;
            _chatMessages.Remove(draftId);
        }

        // Replace item in Chats (assume ChatItem.Id is read-only)
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
            // If somehow missing, just ensure and select
            var newItem = EnsureChatItem(realChatId);
            newItem.Label = ComputeChatLabel(realChatId);
            SelectedChat = newItem;
        }

        RefreshVisibleChat();
    }

    // Called when user selects a chat
    partial void OnSelectedChatChanged(ChatItem? value)
    {
        RefreshVisibleChat();
        UpdateMessagePlaceholder();

        // Let the UI re-check CanSend whenever selection changes
        OnPropertyChanged(nameof(CanSend));

        if (value is not null)
        {
            // Do not join drafts; there's no server channel yet
            if (!value.Id.StartsWith("draft:", StringComparison.OrdinalIgnoreCase))
            {
                _ = _chat.JoinChatAsync(value.Id);
            }
        }
    }

    private void UpdateMessagePlaceholder()
    {
        // Default when nothing is selected
        if (SelectedChat is null)
        {
            MessagePlaceholder = "Type a message…";
            return;
        }

        var id = SelectedChat.Id;

        // LOBBY (custom phrasing)
        if (string.Equals(id, "Lobby", StringComparison.OrdinalIgnoreCase))
        {
            MessagePlaceholder = "Write a message in the lobby...";
            return;
        }

        // DRAFT DM: draft:Other
        if (id.StartsWith("draft:", StringComparison.OrdinalIgnoreCase))
        {
            var other = id.Substring("draft:".Length);
            MessagePlaceholder = $"Type a message to {other}";
            return;
        }

        // REAL DM: dm:Alice|Bob → pick the one that's not me
        if (id.StartsWith("dm:", StringComparison.OrdinalIgnoreCase))
        {
            var body = id.Substring(3);
            var parts = body.Split('|');
            var other = parts.FirstOrDefault(p => !p.Equals(User, StringComparison.OrdinalIgnoreCase));
            MessagePlaceholder = $"Type a message to {other ?? "this chat"}";
            return;
        }

        // Groups or anything else
        MessagePlaceholder = $"Type a message to {ComputeChatLabel(id)}";
    }

    public void RefreshVisibleChat()
    {
        CurrentChatMessages.Clear();
        if (SelectedChat is null) return;

        if (_chatMessages.TryGetValue(SelectedChat.Id, out var list))
            foreach (var line in list)
                CurrentChatMessages.Add(line);

        // Now that the chat is visible, clear unread
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

        // If it's a draft DM, create-on-send
        if (IsDraftId(SelectedChat.Id))
        {
            var other = SelectedChat.Label?.Replace(" (draft)", "")
                        ?? SelectedChat.Id.Substring("draft:".Length);

            // Preferred path: call ChatService.SendDmFirstAsync (server should create + send atomically)
            var realChatId = await TrySendDmFirstAsync(other, User, msg);

            // Fallback (compiles today): use existing endpoints if SendDmFirstAsync is not yet implemented.
            if (string.IsNullOrWhiteSpace(realChatId))
            {
                realChatId = await _chat.CreateDmAsync(other);
                if (!string.IsNullOrWhiteSpace(realChatId))
                {
                    await _chat.SendToChatAsync(realChatId, User, msg);
                }
            }

            if (!string.IsNullOrWhiteSpace(realChatId))
            {
                SwapDraftToReal(SelectedChat.Id, realChatId);
                // join the real chat so subsequent messages flow normally
                _ = _chat.JoinChatAsync(realChatId);
            }

            return;
        }

        await _chat.SendToChatAsync(SelectedChat.Id, User, msg);
    }

    private async Task<string?> TrySendDmFirstAsync(string otherDisplayName, string fromUser, string message)
    {
        // Use reflection so this file compiles even if ChatService doesn't have SendDmFirstAsync yet.
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

        // Do NOT hit the server here. Just open a local draft.
        var draftId = DraftOf(otherDisplayName);
        var item = EnsureChatItem(draftId);
        item.Label = DraftLabel(otherDisplayName);
        SelectedChat = item;

        // no JoinChatAsync, no server notifications yet
    }

    // -------- Presence helper (ONLINE + OFFLINE) --------
    private void RecomputePeople(IEnumerable<string> onlineNow)
    {
        var onlineSet = new HashSet<string>(onlineNow, StringComparer.OrdinalIgnoreCase);

        // Seed known users with people from chats (so DM partners show up even when offline)
        foreach (var chat in Chats)
        {
            // dm:Alice|Bob → both; Lobby ignored
            if (chat.Id.StartsWith("dm:", StringComparison.OrdinalIgnoreCase))
            {
                var body = chat.Id.Substring(3);
                foreach (var p in body.Split('|'))
                    _knownUsers.Add(p);
            }
        }

        // Build a sorted list: online first, then offline; then by name
        var names = _knownUsers.ToList();
        names.Sort((a, b) =>
        {
            var aOnline = onlineSet.Contains(a);
            var bOnline = onlineSet.Contains(b);
            var onlineCmp = bOnline.CompareTo(aOnline); // true before false
            return onlineCmp != 0 ? onlineCmp : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        });

        // Sync People collection
        // 1) Update / add
        foreach (var name in names)
        {
            var item = People.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (item is null)
            {
                People.Add(new UserPresenceItem(name, onlineSet.Contains(name)));
            }
            else
            {
                item.IsOnline = onlineSet.Contains(name);
            }
        }

        // 2) Remove entries no longer known (rare)
        for (int i = People.Count - 1; i >= 0; i--)
        {
            if (!names.Contains(People[i].Name, StringComparer.OrdinalIgnoreCase))
                People.RemoveAt(i);
        }

        // 3) Reorder collection to match our sorted "names"
        // (CollectionView doesn't sort by itself)
        for (int i = 0; i < names.Count; i++)
        {
            var idx = People.IndexOf(People.First(p => p.Name.Equals(names[i], StringComparison.OrdinalIgnoreCase)));
            if (idx != i) People.Move(idx, i);
        }

        // Keep the quick "OnlineUsers" list in sync if you still use it elsewhere
        OnlineUsers.Clear();
        foreach (var n in onlineSet) OnlineUsers.Add(n);
    }
}
