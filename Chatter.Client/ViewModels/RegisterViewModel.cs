/*
File: RegisterViewModel.cs

What this does:
- Purpose: Handles the user registration flow (email, password, confirm, optional display name) and navigation to Login.
- How: Validates inputs, calls SupabaseAuthService.SignUpAsync, shows user-friendly alerts, and raises events for the view to react.
*/

using System;
using System.Collections.Generic;
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
    private readonly SupabaseAuthService _auth;

    [ObservableProperty] public partial string? Email { get; set; }
    [ObservableProperty] public partial string? Password { get; set; }
    [ObservableProperty] public partial string? ConfirmPassword { get; set; }
    [ObservableProperty] public partial string? DisplayName { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }

    public IAsyncRelayCommand RegisterCommand { get; }

    public IRelayCommand NavigateToLoginCommand { get; }  // Initialized in ctor

    public event Action? RegistrationSucceeded;

    public event Action? NavigateToLoginRequested;

    public RegisterViewModel(SupabaseAuthService auth)
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
            var metadata = string.IsNullOrWhiteSpace(DisplayName)
                ? null
                : new Dictionary<string, object> { ["display_name"] = DisplayName! };

            var session = await _auth.SignUpAsync(email: email, password: password, metadata: metadata);

            if (session is not null)
            {
                await ShowAlertAsync("Welcome!", "Account created.", "OK");
                RegistrationSucceeded?.Invoke();
            }
            else
            {
                await ShowAlertAsync(
                    "Check your email",
                    "We sent you a verification link. Please confirm your address, then sign in.",
                    "OK");
                RegistrationSucceeded?.Invoke();
            }
        }

        catch (Exception ex)
        {
            string userMessage;

            // Basic Supabase error mapping
            if (ex.Message.Contains("weak_password", StringComparison.OrdinalIgnoreCase))
            {
                userMessage = "Enter 6 or more characters for your password.";
            }
            else if (ex.Message.Contains("email_address_invalid", StringComparison.OrdinalIgnoreCase))
            {
                userMessage = "Enter a valid email address.";
            }
            else if (ex.Message.Contains("email_exists", StringComparison.OrdinalIgnoreCase))
            {
                userMessage = "An account already exists with this email. Please sign in.";
            }
            else
            {
                // Fallback so we avoid JSON-felet helt
                userMessage = "Registration failed. Please try again.";
            }

            await ShowAlertAsync("Registration failed", userMessage, "OK");
        }

        finally
        {
            IsBusy = false;
        }
    }
}
