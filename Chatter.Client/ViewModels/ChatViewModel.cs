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
using System.Collections.Generic;
using System.Linq;

namespace Chatter.Client.ViewModels;

public partial class ChatViewModel : ObservableObject
{
    private readonly ChatService _chat;
    private readonly Dictionary<string, string?> _lastLineByChat = new(StringComparer.OrdinalIgnoreCase);
    private const string BaseUrl = "http://localhost:5291";

    [ObservableProperty] private string user = string.Empty;
    [ObservableProperty] private string? outgoingMessage;

    // whether ChatPage is currently visible
    [ObservableProperty] private bool isActive;

    // RIGHT: roster
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

    public ChatViewModel(ChatService chat)
    {
        _chat = chat;

        // ===== Legacy broadcasts =====
        _chat.MessageReceived += (u, m) =>
            MainThread.BeginInvokeOnMainThread(() => Messages.Add($"{u}: {m}"));

        // ===== Roster =====
        _chat.OnlineUsersUpdated += list =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                OnlineUsers.Clear();
                foreach (var n in list) OnlineUsers.Add(n);
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

        for (int i = Chats.Count - 1; i >= 0; i--)
            if (!shouldHave.Contains(Chats[i].Id))
                Chats.RemoveAt(i);

        // 🔝 keep Lobby at index 0
        var lobby = Chats.FirstOrDefault(c => c.Id.Equals("Lobby", StringComparison.OrdinalIgnoreCase));
        if (lobby is not null && Chats.IndexOf(lobby) != 0)
            Chats.Move(Chats.IndexOf(lobby), 0);

        if (SelectedChat is null && Chats.Count > 0)
            SelectedChat = Chats[0];
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
        _chat.AddedChat += (chatId, fromUser) =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                var item = EnsureChatItem(chatId);
                item.Label = fromUser; // use sender as label for DM
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
            foreach (var c in Chats)
                c.Label = ComputeChatLabel(c.Id);
        });

        _chat.OnGetCurrentDisplayName = () => User;

        ConnectCommand = new AsyncRelayCommand(ConnectAsync);
        SendCommand = new AsyncRelayCommand(SendAsync);
        StartDmCommand = new AsyncRelayCommand<string>(StartDmAsync);
    }

    // Compute a nice label for each chat (Lobby or the "other user" for DMs)
    private string ComputeChatLabel(string chatId)
    {
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

    // Called when user selects a chat
    partial void OnSelectedChatChanged(ChatItem? value)
    {
        RefreshVisibleChat();
        if (value is not null)
        {
            // join the chat if we haven’t yet
            _ = _chat.JoinChatAsync(value.Id);
        }
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

        await _chat.SendToChatAsync(SelectedChat.Id, User, msg);
    }

    private async Task StartDmAsync(string otherDisplayName)
    {
        if (string.IsNullOrWhiteSpace(otherDisplayName) ||
            otherDisplayName.Equals(User, StringComparison.OrdinalIgnoreCase))
            return;

        var chatId = await _chat.CreateDmAsync(otherDisplayName);
        if (string.IsNullOrWhiteSpace(chatId)) return;

        var item = EnsureChatItem(chatId);
        item.Label = otherDisplayName; // set nice label
        SelectedChat = item;
        await _chat.JoinChatAsync(chatId);
    }
}
