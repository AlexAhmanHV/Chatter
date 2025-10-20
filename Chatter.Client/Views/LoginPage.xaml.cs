using Chatter.Client.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Chatter.Client.Views;

public partial class LoginPage : ContentPage
{
    private readonly IServiceProvider _services;

    public LoginPage(LoginViewModel vm, IServiceProvider services)
    {
        InitializeComponent();
        _services = services;

        BindingContext = vm;
        vm.LoginSucceeded += OnLoginSucceeded;
    }

    private async void OnLoginSucceeded(string displayName)
    {
        var chatPage = _services.GetRequiredService<ChatPage>();

        if (chatPage.BindingContext is ViewModels.ChatViewModel chatVm)
        {
            chatVm.User = displayName;   // ✅ show display name everywhere
        }

        Navigation.InsertPageBefore(chatPage, this);
        await Navigation.PopAsync();

        if (chatPage.BindingContext is ViewModels.ChatViewModel vm)
        {
            await vm.ConnectCommand.ExecuteAsync(null);
        }
    }
    private async void OnCreateAccountClicked(object sender, EventArgs e)
    {
        var registerPage = _services.GetRequiredService<RegisterPage>();
        await Navigation.PushAsync(registerPage);
    }
}
