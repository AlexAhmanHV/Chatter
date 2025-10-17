using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Maui.Controls;

namespace Chatter.Client.ViewModels;

public partial class LoginViewModel : ObservableObject
{
    [ObservableProperty] private string? username;
    [ObservableProperty] private string? password;
    [ObservableProperty] private bool isBusy;

    // Raised when login succeeds so the page can navigate.
    public event Action<string>? LoginSucceeded;

    public IAsyncRelayCommand LoginCommand { get; }

    public LoginViewModel()
    {
        LoginCommand = new AsyncRelayCommand(LoginAsync);
    }

    private async Task LoginAsync()
    {
        if (IsBusy) return;
        IsBusy = true;

        try
        {
            // Basic validation
            if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
            {
                await Application.Current.MainPage.DisplayAlert("Missing info", "Please enter username and password.", "OK");
                return;
            }

            // 🔒 Hardcoded credentials for now
            var ok = Username.Equals("admin", StringComparison.OrdinalIgnoreCase)
                     && Password == "password";

            if (!ok)
            {
                await Application.Current.MainPage.DisplayAlert("Login failed", "Invalid username or password.", "OK");
                return;
            }

            // Notify page to navigate
            LoginSucceeded?.Invoke(Username);
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
