using Chatter.Client.ViewModels;

namespace Chatter.Client.Views;

public partial class RegisterPage : ContentPage
{
    public RegisterPage(RegisterViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;

        vm.RegistrationSucceeded += () =>
        {
            // Pop back to Login page
            _ = Navigation.PopAsync();
        };
    }
}
