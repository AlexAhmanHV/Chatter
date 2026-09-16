/*
File: RegisterPage.xaml.cs

What this does:
- Purpose: Code-behind for the RegisterPage. It wires the page’s BindingContext to RegisterViewModel and listens for
  RegistrationSucceeded (after successful sign-up) and NavigateToLoginRequested (tap on “Log in”) to navigate back.
- How: The constructor receives RegisterViewModel via DI, sets BindingContext, and subscribes to the VM events.
  When either event fires, it calls Navigation.PopAsync() to return to the previous page.
*/

using System.Threading.Tasks;
using Chatter.Client.Helpers;
using Chatter.Client.ViewModels;

namespace Chatter.Client.Views;
public partial class RegisterPage : ContentPage, IAlertHost
{
    public RegisterPage(RegisterViewModel vm)
    {
      InitializeComponent(); BindingContext = vm;
      vm.RegistrationSucceeded += OnRegistrationSucceeded;
      vm.NavigateToLoginRequested += OnNavigateBack;
    }

    private async void OnRegistrationSucceeded()
    {
        SuccessOverlay.IsVisible = true;
        await Task.Delay(1200);
        await Navigation.PopAsync();
    }

    private void OnNavigateBack() => _ = Navigation.PopAsync();

    public Task ShowAlertAsync(string title, string message, string accept = "OK") =>
        AlertOverlay.ShowAlertAsync(title, message, accept);

    public Task<bool> ShowConfirmAsync(string title, string message, string accept, string cancel) =>
        AlertOverlay.ShowConfirmAsync(title, message, accept, cancel);
}
