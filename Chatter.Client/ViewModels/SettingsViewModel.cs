/*
File: SettingsViewModel.cs

What this does:
- Purpose: Lets the user update their display name and avatar in Settings, and returns to the
  previous page.
- How: Display name: broadcasts a DisplayNameChangedMessage; ChatViewModel is what actually
  persists the new name server-side (via ChatHub.ChangeDisplayName) and updates the local
  ApiAuthService cache. Avatar: picks an image and uploads it directly via ChatHub.UpdateAvatar.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Maui.Controls;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Media;
using Microsoft.Maui.Storage;
using Chatter.Client.Helpers;
using Chatter.Client.Services;
using Chatter.Client.Messages;

namespace Chatter.Client.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    [ObservableProperty] public partial string DisplayName { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial ImageSource? AvatarPreview { get; set; }

    private readonly ApiAuthService _auth;
    private readonly ChatService _chat;

    /// Saves the updated display name.
    public IAsyncRelayCommand SaveCommand { get; }

    /// Picks an image and uploads it as the profile picture.
    public IAsyncRelayCommand PickAvatarCommand { get; }

    /// Signs out and returns to the Login page.
    public IAsyncRelayCommand LogoutCommand { get; }

    // Raised once sign-out has actually happened (token cleared, hub disconnected) so the View
    // can reset the navigation stack back to Login - see SettingsPage.xaml.cs.
    public event Action? LoggedOut;

    public SettingsViewModel(ApiAuthService auth, ChatService chat)
    {
        _auth = auth;
        _chat = chat;
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        PickAvatarCommand = new AsyncRelayCommand(PickAvatarAsync);
        LogoutCommand = new AsyncRelayCommand(LogoutAsync);
        DisplayName = _auth.CurrentDisplayName ?? string.Empty;

        _ = LoadAvatarPreviewAsync();
    }

    private static Page? GetRootPage() => Application.Current?.Windows?.FirstOrDefault()?.Page;

    private static Task ShowAlertAsync(string title, string message, string cancel = "OK") =>
        MainThread.InvokeOnMainThreadAsync(() => Ui.DisplayAlert(title, message, cancel));

    private static Task<bool> ShowConfirmAsync(string title, string message, string accept, string cancel) =>
        MainThread.InvokeOnMainThreadAsync(() => Ui.DisplayAlert(title, message, accept, cancel));

    private static Task NavigateBackAsync()
    {
        var nav = GetRootPage()?.Navigation;
        if (nav is null) return Task.CompletedTask;
        return MainThread.InvokeOnMainThreadAsync(() => nav.PopAsync());
    }

    private async Task LoadAvatarPreviewAsync()
    {
        if (string.IsNullOrWhiteSpace(DisplayName)) return;
        try
        {
            var urls = await _chat.GetAvatarUrlsAsync(new List<string> { DisplayName });
            if (urls.TryGetValue(DisplayName, out var relativeUrl))
                AvatarPreview = ImageSource.FromUri(new Uri($"{ServerConfig.BaseUrl}{relativeUrl}"));
        }
        catch
        {
            // No avatar set yet, or not connected - leave the preview blank.
        }
    }

    private async Task PickAvatarAsync()
    {
        FileResult? photo;
        try
        {
            photo = await MediaPicker.Default.PickPhotoAsync();
        }
        catch (Exception ex)
        {
            await ShowAlertAsync("Couldn't open picker", ex.Message, "OK");
            return;
        }
        if (photo is null) return; // user cancelled

        byte[] bytes;
        try
        {
            await using var stream = await photo.OpenReadAsync();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            bytes = ms.ToArray();
        }
        catch (Exception ex)
        {
            await ShowAlertAsync("Couldn't read image", ex.Message, "OK");
            return;
        }

        var contentType = Path.GetExtension(photo.FileName).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => "image/jpeg",
        };

        try
        {
            await _chat.UpdateAvatarAsync(bytes, contentType);
            AvatarPreview = ImageSource.FromStream(() => new MemoryStream(bytes));
            await ShowAlertAsync("Saved", "Avatar updated.", "OK");
        }
        catch (Exception ex)
        {
            await ShowAlertAsync("Couldn't update avatar", ex.Message, "OK");
        }
    }

    private async Task LogoutAsync()
    {
        var confirmed = await ShowConfirmAsync("Log out?", "You'll need to log in again to use Chatter.", "Log out", "Cancel");
        if (!confirmed) return;

        try { await _chat.StopAsync(); } catch { }
        _auth.SignOut();

        LoggedOut?.Invoke();
    }

    private async Task SaveAsync()
    {
        if (IsBusy) return;
        IsBusy = true;

        try
        {
            if (string.IsNullOrWhiteSpace(DisplayName))
            {
                await ShowAlertAsync("Missing info", "Display name cannot be empty.", "OK");
                return;
            }

            var name = DisplayName.Trim();
            _auth.UpdateLocalDisplayName(name);

            WeakReferenceMessenger.Default.Send(new DisplayNameChangedMessage(name));
            await ShowAlertAsync("Saved", "Display name updated.", "OK");
            await NavigateBackAsync();
        }
        catch (Exception ex)
        {
            await ShowAlertAsync("Error", ex.Message, "OK");
        }
        finally
        {
            IsBusy = false;
        }
    }
}
