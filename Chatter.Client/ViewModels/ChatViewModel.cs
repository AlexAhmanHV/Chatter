using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Chatter.Client.Services;
using Microsoft.Maui.ApplicationModel; // MainThread
using Microsoft.Maui.Controls;         // Application (DisplayAlert)
using CommunityToolkit.Mvvm.Messaging;
using Chatter.Client.Messages;

namespace Chatter.Client.ViewModels;

public partial class ChatViewModel : ObservableObject
{
    private readonly ChatService _chat;

    private const string BaseUrl = "http://localhost:5291";

    [ObservableProperty] private string user = string.Empty;
    [ObservableProperty] private string? outgoingMessage;

    public ObservableCollection<string> Messages { get; } = new();

    public IAsyncRelayCommand ConnectCommand { get; }
    public IAsyncRelayCommand SendCommand { get; }

    public ChatViewModel(ChatService chat)
    {
        _chat = chat;

        _chat.MessageReceived += (u, m) =>
            MainThread.BeginInvokeOnMainThread(() => Messages.Add($"{u}: {m}"));

        // 🔔 Update User when settings change the display name
        WeakReferenceMessenger.Default.Register<DisplayNameChangedMessage>(this, (_, msg) =>
        {
            User = msg.Value;
            Messages.Add($"📝 Display name updated to '{User}'.");
        });

        ConnectCommand = new AsyncRelayCommand(ConnectAsync);
        SendCommand = new AsyncRelayCommand(SendAsync);
    }

    private async Task ConnectAsync()
    {
        try
        {
            await _chat.StartAsync(BaseUrl);
            Messages.Add("📶 Connected to server.");
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
