using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Chatter.Client.Services;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using CommunityToolkit.Mvvm.Messaging;
using Chatter.Client.Messages;
using System.Collections.ObjectModel;

namespace Chatter.Client.ViewModels;


public partial class ChatViewModel : ObservableObject
{
    private readonly ChatService _chat;
    private const string BaseUrl = "http://localhost:5291";

    [ObservableProperty] private string user = string.Empty;
    [ObservableProperty] private string? outgoingMessage;

    // NEW: roster bound to the sidebar UI
    public ObservableCollection<string> OnlineUsers { get; } = new();

    public ObservableCollection<string> Messages { get; } = new();

    public IAsyncRelayCommand ConnectCommand { get; }
    public IAsyncRelayCommand SendCommand { get; }

    public ChatViewModel(ChatService chat)
    {
        _chat = chat;

        _chat.MessageReceived += (u, m) =>
            MainThread.BeginInvokeOnMainThread(() => Messages.Add($"{u}: {m}"));

        _chat.DisplayNameChanged += (oldName, newName) =>
            MainThread.BeginInvokeOnMainThread(() =>
                Messages.Add($"🔔 {oldName} changed their name to “{newName}”."));

        // NEW: update roster when the server pushes it
        _chat.OnlineUsersUpdated += list =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                OnlineUsers.Clear();
                foreach (var n in list)
                    OnlineUsers.Add(n);
            });

        WeakReferenceMessenger.Default.Register<DisplayNameChangedMessage>(this, async (_, msg) =>
        {
            User = msg.Value;
            try { await _chat.ChangeDisplayNameAsync(User); } catch { }
        });

        _chat.OnGetCurrentDisplayName = () => User;

        ConnectCommand = new AsyncRelayCommand(ConnectAsync);
        SendCommand = new AsyncRelayCommand(SendAsync);
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
            await Application.Current.MainPage.DisplayAlert(
                "Pick a username", "Please enter a username first.", "OK");
            return;
        }

        if (string.IsNullOrWhiteSpace(OutgoingMessage)) return;

        var msg = OutgoingMessage!;
        OutgoingMessage = string.Empty;

        await _chat.SendAsync(User, msg);
    }
}
