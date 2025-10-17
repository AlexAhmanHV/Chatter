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

    private async void OnLoginSucceeded(string username)
    {
        // Resolve a fresh ChatPage (and its VM) via DI
        var chatPage = _services.GetRequiredService<ChatPage>();

        if (chatPage.BindingContext is ViewModels.ChatViewModel chatVm)
        {
            chatVm.User = username; // prefill the username in chat
        }

        await Navigation.PushAsync(chatPage);
    }
}
