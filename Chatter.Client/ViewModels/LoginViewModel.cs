using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Maui.Controls;
using Chatter.Client.Services;

namespace Chatter.Client.ViewModels;

public partial class LoginViewModel : ObservableObject
{
    [ObservableProperty] private string? username;
    [ObservableProperty] private string? password;
    [ObservableProperty] private bool isBusy;

    private readonly SupabaseAuthService _auth;

    // Enables the button (and lets code-behind check validity)
    public bool CanLogin =>
        !string.IsNullOrWhiteSpace(Username) &&
        !string.IsNullOrWhiteSpace(Password) &&
        !IsBusy;

    // Raised when login succeeds so the page can navigate.
    public event Action<string>? LoginSucceeded;

    public IAsyncRelayCommand LoginCommand { get; }

    public LoginViewModel(SupabaseAuthService auth /*, ChatService chat */)
    {
        _auth = auth;

        // Wire CanExecute to CanLogin
        LoginCommand = new AsyncRelayCommand(LoginAsync, () => CanLogin);

        // Keep the command's CanExecute in sync with CanLogin
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CanLogin))
                LoginCommand.NotifyCanExecuteChanged();
        };
    }

    // Make sure changes to these properties notify CanLogin
    partial void OnUsernameChanged(string? value) => OnPropertyChanged(nameof(CanLogin));
    partial void OnPasswordChanged(string? value) => OnPropertyChanged(nameof(CanLogin));
    partial void OnIsBusyChanged(bool value)      => OnPropertyChanged(nameof(CanLogin));

    private async Task LoginAsync()
{
    if (IsBusy) return;

    // ✅ Validate BEFORE setting IsBusy
    if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
    {
        await Application.Current.MainPage.DisplayAlert("Missing info", "Please enter email and password.", "OK");
        return;
    }

    IsBusy = true;

    try
    {
        var session = await _auth.SignInAsync(Username!, Password!);
        if (session is null)
        {
            await Application.Current.MainPage.DisplayAlert("Login failed", "Invalid credentials.", "OK");
            return;
        }

        var display = _auth.CurrentDisplayName;
        if (string.IsNullOrWhiteSpace(display))
        {
            display = Username!.Contains('@') ? Username!.Split('@')[0] : Username!;
        }

        LoginSucceeded?.Invoke(display!);
    }
    catch (Exception ex)
    {
        await Application.Current.MainPage.DisplayAlert("Login failed", ex.Message, "OK");
    }
    finally
    {
        IsBusy = false;
    }
}
}
