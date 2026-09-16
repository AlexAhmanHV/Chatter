/*
File: LoginViewModel.cs

What this does:
- Purpose: ViewModel for the login screen. Captures username/password, manages busy state, validates inputs,
  calls ApiAuthService to sign in against this app's own /auth/login endpoint, and raises a success event
  so the page can navigate.
- How: Exposes bindable properties (Username, Password, IsBusy), a computed CanLogin, and an AsyncRelayCommand (LoginCommand).
  On success, emits LoginSucceeded(displayName) using the display name the server returns.
*/

using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Maui.Controls;
using Microsoft.Maui.ApplicationModel;
using Chatter.Client.Helpers;
using Chatter.Client.Services;

namespace Chatter.Client.ViewModels;

public partial class LoginViewModel : ObservableObject
{
    // Bindable state (username, password, busy) and derived CanLogin
    [ObservableProperty] public partial string? Username { get; set; }
    [ObservableProperty] public partial string? Password { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }

    // Editable server address - collapsed behind ShowServerSettings by default so it doesn't
    // clutter the login screen for the common case (same machine, or an emulator, where the
    // guessed default already works). Persisted via ServerConfig as soon as it's changed, so a
    // physical device can be pointed at a real server without editing source code and rebuilding.
    [ObservableProperty] public partial string? ServerUrl { get; set; }
    [ObservableProperty] public partial bool ShowServerSettings { get; set; }

    private readonly ApiAuthService _auth;

    // Computed: enables the login button only when inputs are present and not busy.
    public bool CanLogin =>
        !string.IsNullOrWhiteSpace(Username) &&
        !string.IsNullOrWhiteSpace(Password) &&
        !IsBusy;

    // Outputs / events (navigation trigger on success)
    public event Action<string>? LoginSucceeded;


    public IAsyncRelayCommand LoginCommand { get; }
    public IRelayCommand ToggleServerSettingsCommand { get; }

    public LoginViewModel(ApiAuthService auth)
    {
        _auth = auth;
        ServerUrl = ServerConfig.BaseUrl;
        ShowServerSettings = ServerConfig.HasCustomBaseUrl;

        LoginCommand = new AsyncRelayCommand(LoginAsync, () => CanLogin);
        ToggleServerSettingsCommand = new RelayCommand(() => ShowServerSettings = !ShowServerSettings);

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

    // Persists on every keystroke rather than only on save/login - simple, and the value is only
    // ever used for the next network call anyway, so there's no "unsaved changes" state to track.
    partial void OnServerUrlChanged(string? value) => ServerConfig.SetBaseUrl(value);

    private static Task ShowAlertAsync(string title, string message, string cancel = "OK") =>
        MainThread.InvokeOnMainThreadAsync(() => Ui.DisplayAlert(title, message, cancel));

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
            var email = Username!;
            var pwd = Password!;

            var display = await _auth.SignInAsync(email, pwd);
            LoginSucceeded?.Invoke(display);
        }
        catch (System.Net.Http.HttpRequestException)
        {
            await ShowAlertAsync("Login failed", "Network error. Please check your connection and try again.", "OK");
        }
        catch (Exception ex)
        {
            await ShowAlertAsync("Login failed", ex.Message, "OK");
        }
        finally
        {
            IsBusy = false;
        }
    }
}
