/*
File: RegisterPage.xaml.cs

What this does:
- Purpose: Code-behind for the RegisterPage. It wires the page’s BindingContext to RegisterViewModel and listens for
  RegistrationSucceeded (after successful sign-up) and NavigateToLoginRequested (tap on “Log in”) to navigate back.
- How: The constructor receives RegisterViewModel via DI, sets BindingContext, and subscribes to the VM events.
  When either event fires, it calls Navigation.PopAsync() to return to the previous page.
*/

using Chatter.Client.ViewModels;

namespace Chatter.Client.Views;

public partial class RegisterPage : ContentPage
{
    public RegisterPage(RegisterViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;

        vm.RegistrationSucceeded += OnNavigateBack;
        vm.NavigateToLoginRequested += OnNavigateBack;
    }

    private void OnNavigateBack() => _ = Navigation.PopAsync();
}
