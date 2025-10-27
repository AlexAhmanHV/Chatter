/*
File: SettingsViewModel.cs

What this does:
- Purpose: Lets the user update their display name in Settings and returns to the previous page.
- How: Validates input, calls SupabaseAuthService.UpdateDisplayNameAsync, shows alerts, and broadcasts a DisplayNameChangedMessage.
*/

using System;
using System.Linq;                  
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Maui.Controls;
using Microsoft.Maui.ApplicationModel; 
using Chatter.Client.Services;
using Chatter.Client.Messages;

namespace Chatter.Client.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    [ObservableProperty] public partial string DisplayName { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsBusy { get; set; }

    private readonly SupabaseAuthService _auth;

    /// Saves the updated display name.
    public IAsyncRelayCommand SaveCommand { get; }

    public SettingsViewModel(SupabaseAuthService auth)
    {
        _auth = auth;
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        DisplayName = _auth.CurrentDisplayName ?? string.Empty;
    }

    private static Page? GetRootPage() => Application.Current?.Windows?.FirstOrDefault()?.Page;

    private static Task ShowAlertAsync(string title, string message, string cancel = "OK")
    {
        var page = GetRootPage();
        if (page is null) return Task.CompletedTask;
        return MainThread.InvokeOnMainThreadAsync(() => page.DisplayAlert(title, message, cancel));
    }

    private static Task NavigateBackAsync()
    {
        var nav = GetRootPage()?.Navigation;
        if (nav is null) return Task.CompletedTask;
        return MainThread.InvokeOnMainThreadAsync(() => nav.PopAsync());
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
            var updated = await _auth.UpdateDisplayNameAsync(name);
            if (string.IsNullOrWhiteSpace(updated))
            {
                await ShowAlertAsync("Update failed", "Could not update your name.", "OK");
                return;
            }

            WeakReferenceMessenger.Default.Send(new DisplayNameChangedMessage(updated));
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
