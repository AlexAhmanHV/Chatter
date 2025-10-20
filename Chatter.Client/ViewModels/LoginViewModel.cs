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

    // Raised when login succeeds so the page can navigate.
    public event Action<string>? LoginSucceeded;

    public IAsyncRelayCommand LoginCommand { get; }

    public LoginViewModel(SupabaseAuthService auth /*, ChatService chat */)
    {
        _auth = auth;
        LoginCommand = new AsyncRelayCommand(LoginAsync);
    }

    private async Task LoginAsync()
    {
        if (IsBusy) return;
        IsBusy = true;

        try
        {
            if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
            {
                await Application.Current.MainPage.DisplayAlert("Missing info", "Please enter email and password.", "OK");
                return;
            }

            var session = await _auth.SignInAsync(Username!, Password!);
            if (session is null)
            {
                await Application.Current.MainPage.DisplayAlert("Login failed", "Invalid credentials.", "OK");
                return;
            }

            // Prefer display_name from auth.user.user_metadata
            var display = _auth.CurrentDisplayName;

            // Fallback: email local-part (before '@') or the whole email if no '@'
            if (string.IsNullOrWhiteSpace(display))
            {
                display = Username!.Contains('@')
                    ? Username!.Split('@')[0]
                    : Username!;
            }

            // success → notify page to navigate and set ChatViewModel.User
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
