// ViewModels/SettingsViewModel.cs
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Maui.Controls;
using Chatter.Client.Services;
using Chatter.Client.Messages;

namespace Chatter.Client.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly SupabaseAuthService _auth;

    [ObservableProperty] private string displayName = string.Empty;
    [ObservableProperty] private bool isBusy;

    public IAsyncRelayCommand SaveCommand { get; }

    public SettingsViewModel(SupabaseAuthService auth)
    {
        _auth = auth;
        SaveCommand = new AsyncRelayCommand(SaveAsync);

        // Initialize field from auth
        DisplayName = _auth.CurrentDisplayName ?? string.Empty;
    }

    private async Task SaveAsync()
    {
        if (IsBusy) return;
        IsBusy = true;

        try
        {
            if (string.IsNullOrWhiteSpace(DisplayName))
            {
                await Application.Current.MainPage.DisplayAlert("Missing info", "Display name cannot be empty.", "OK");
                return;
            }

            var updated = await _auth.UpdateDisplayNameAsync(DisplayName.Trim());
            if (string.IsNullOrWhiteSpace(updated))
            {
                await Application.Current.MainPage.DisplayAlert("Update failed", "Could not update your name.", "OK");
                return;
            }

            // Notify the app so any VM can react (e.g., ChatViewModel)
            WeakReferenceMessenger.Default.Send(new DisplayNameChangedMessage(updated));

            await Application.Current.MainPage.DisplayAlert("Saved", "Display name updated.", "OK");
            await Application.Current.MainPage.Navigation.PopAsync(); // go back
        }
        catch (Exception ex)
        {
            await Application.Current.MainPage.DisplayAlert("Error", ex.Message, "OK");
        }
        finally
        {
            IsBusy = false;
        }
    }
}
