// ViewModels/RegisterViewModel.cs
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Maui.Controls;
using Chatter.Client.Services;

namespace Chatter.Client.ViewModels;

public partial class RegisterViewModel : ObservableObject
{
    private readonly SupabaseAuthService _auth;

    [ObservableProperty] private string? email;
    [ObservableProperty] private string? password;
    [ObservableProperty] private string? displayName;
    [ObservableProperty] private bool isBusy;

    public IAsyncRelayCommand RegisterCommand { get; }

    // Tell the page to go back to login after success
    public event Action? RegistrationSucceeded;

    public RegisterViewModel(SupabaseAuthService auth)
    {
        _auth = auth;
        RegisterCommand = new AsyncRelayCommand(RegisterAsync);
    }

    private async Task RegisterAsync()
    {
        if (IsBusy) return;
        IsBusy = true;

        try
        {
            if (string.IsNullOrWhiteSpace(Email) || string.IsNullOrWhiteSpace(Password))
            {
                await Application.Current.MainPage.DisplayAlert("Missing info", "Enter email and password.", "OK");
                return;
            }

            var session = await _auth.SignUpAsync(
                email: Email!,
                password: Password!,
                metadata: string.IsNullOrWhiteSpace(DisplayName)
                    ? null
                    : new Dictionary<string, object> { ["display_name"] = DisplayName! });

            if (session is not null)
            {
                // Email confirmation disabled: already logged in
                await Application.Current.MainPage.DisplayAlert("Welcome!", "Account created and you are signed in.", "OK");
                RegistrationSucceeded?.Invoke();
            }
            else
            {
                // Email confirmation enabled
                await Application.Current.MainPage.DisplayAlert(
                    "Check your email",
                    "We sent you a verification link. Please confirm your address, then sign in.",
                    "OK");
                RegistrationSucceeded?.Invoke();
            }
        }
        catch (Exception ex)
        {
            await Application.Current.MainPage.DisplayAlert("Registration failed", ex.Message, "OK");
        }
        finally
        {
            IsBusy = false;
        }
    }
}
