/*
File: RegisterViewModel.cs

What this does:
- Purpose: Handles the user registration flow (email, password, confirm, optional display name) and navigation to Login.
- How: Validates inputs, calls ApiAuthService.SignUpAsync (this app's own /auth/register endpoint), shows
  user-friendly alerts, and raises events for the view to react.
*/

using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Maui.Controls;
using Microsoft.Maui.ApplicationModel;
using Chatter.Client.Services;

namespace Chatter.Client.ViewModels;

public partial class RegisterViewModel : ObservableObject
{
    private readonly ApiAuthService _auth;

    [ObservableProperty] public partial string? Email { get; set; }
    [ObservableProperty] public partial string? Password { get; set; }
    [ObservableProperty] public partial string? ConfirmPassword { get; set; }
    [ObservableProperty] public partial string? DisplayName { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }

    public IAsyncRelayCommand RegisterCommand { get; }

    public IRelayCommand NavigateToLoginCommand { get; }  // Initialized in ctor

    public event Action? RegistrationSucceeded;

    public event Action? NavigateToLoginRequested;

    public RegisterViewModel(ApiAuthService auth)
    {
        _auth = auth;
        RegisterCommand = new AsyncRelayCommand(RegisterAsync);
        NavigateToLoginCommand = new RelayCommand(() => NavigateToLoginRequested?.Invoke());
    }

    // Window-aware root page (avoids obsolete Application.MainPage)
    private static Page? GetRootPage() => Application.Current?.Windows?.FirstOrDefault()?.Page;

    private static Task ShowAlertAsync(string title, string message, string cancel = "OK")
    {
        var page = GetRootPage();
        if (page is null) return Task.CompletedTask;
        return MainThread.InvokeOnMainThreadAsync(() => page.DisplayAlert(title, message, cancel));
    }

    private async Task RegisterAsync()
    {
        if (IsBusy) return;
        IsBusy = true;

        try
        {
            if (string.IsNullOrWhiteSpace(Email) || string.IsNullOrWhiteSpace(Password))
            {
                await ShowAlertAsync("Missing info", "Enter email and password.", "OK");
                return;
            }

            if (!string.Equals(Password, ConfirmPassword, StringComparison.Ordinal))
            {
                await ShowAlertAsync("Password mismatch", "Passwords do not match.", "OK");
                return;
            }

            var email = Email!;
            var password = Password!;

            await _auth.SignUpAsync(email: email, password: password, displayName: DisplayName);

            RegistrationSucceeded?.Invoke();
        }
        catch (System.Net.Http.HttpRequestException)
        {
            await ShowAlertAsync("Registration failed", "Network error. Please check your connection and try again.", "OK");
        }
        catch (Exception ex)
        {
            await ShowAlertAsync("Registration failed", ex.Message, "OK");
        }
        finally
        {
            IsBusy = false;
        }
    }
}
