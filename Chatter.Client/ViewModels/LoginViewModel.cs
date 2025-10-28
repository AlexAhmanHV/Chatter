/*
File: LoginViewModel.cs

What this does:
- Purpose: ViewModel for the login screen. Captures username/password, manages busy state, validates inputs,
  calls SupabaseAuthService to sign in, and raises a success event so the page can navigate.
- How: Exposes bindable properties (Username, Password, IsBusy), a computed CanLogin, and an AsyncRelayCommand (LoginCommand).
  On success, derives a friendly display name (falls back to email prefix) and emits LoginSucceeded(displayName).
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

public partial class LoginViewModel : ObservableObject
{
    // Bindable state (username, password, busy) and derived CanLogin
    [ObservableProperty] public partial string? Username { get; set; }
    [ObservableProperty] public partial string? Password { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }

    private readonly SupabaseAuthService _auth;

    // Computed: enables the login button only when inputs are present and not busy.
    public bool CanLogin =>
        !string.IsNullOrWhiteSpace(Username) &&
        !string.IsNullOrWhiteSpace(Password) &&
        !IsBusy;

    // Outputs / events (navigation trigger on success) 
    public event Action<string>? LoginSucceeded;


    public IAsyncRelayCommand LoginCommand { get; }

    public LoginViewModel(SupabaseAuthService auth)
    {
        _auth = auth;

        LoginCommand = new AsyncRelayCommand(LoginAsync, () => CanLogin);

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CanLogin))
                LoginCommand.NotifyCanExecuteChanged();
        };
    }

    // Notify CanLogin whenever inputs/busy change.
    partial void OnUsernameChanged(string? value) => OnPropertyChanged(nameof(CanLogin));
    partial void OnPasswordChanged(string? value) => OnPropertyChanged(nameof(CanLogin));
    partial void OnIsBusyChanged(bool value)      => OnPropertyChanged(nameof(CanLogin));

    private static Page? GetRootPage() => Application.Current?.Windows?.FirstOrDefault()?.Page;

    private static Task ShowAlertAsync(string title, string message, string cancel = "OK")
    {
        var page = GetRootPage();
        if (page is null) return Task.CompletedTask;
        return MainThread.InvokeOnMainThreadAsync(() => page.DisplayAlert(title, message, cancel));
    }

    // Command handler: login flow with validation, auth call, and success event
    private async Task LoginAsync()
    {
        if (IsBusy) return;

        // Validate before setting IsBusy so the UI stays responsive for simple mistakes.
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
        {
            await ShowAlertAsync("Missing info", "Please enter email and password.", "OK");
            return;
        }

        IsBusy = true;

        try
        {
            // Local copies satisfy nullability after validation
            var email = Username!;
            var pwd   = Password!;

            var session = await _auth.SignInAsync(email, pwd);
            if (session is null)
            {
                await ShowAlertAsync("Login failed", "Invalid credentials.", "OK");
                return;
            }

            // Prefer stored display name; otherwise derive from email prefix
            var display = _auth.CurrentDisplayName;
            if (string.IsNullOrWhiteSpace(display))
                display = email.Contains('@') ? email.Split('@')[0] : email;

            LoginSucceeded?.Invoke(display);
        }
        catch (Exception ex)
        {
            string userMessage;

            if (ex.Message.Contains("invalid_credentials", StringComparison.OrdinalIgnoreCase))
            {
                userMessage = "Invalid email or password.";
            }
            else if (ex.Message.Contains("email_not_confirmed", StringComparison.OrdinalIgnoreCase))
            {
                userMessage = "Please verify your email before signing in.";
            }
            else if (ex is System.Net.Http.HttpRequestException)
            {
                userMessage = "Network error. Please check your connection and try again.";
            }
            else
            {
                userMessage = "Login failed. Please try again.";
            }

            await ShowAlertAsync("Login failed", userMessage, "OK");
        }

        finally
        {
            IsBusy = false;
        }
    }
}
