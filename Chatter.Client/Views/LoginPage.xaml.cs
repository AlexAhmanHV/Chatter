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
        var chatPage = _services.GetRequiredService<ChatPage>();

        if (chatPage.BindingContext is ViewModels.ChatViewModel chatVm)
        {
            chatVm.User = username;                    // set chat username
        }

        // Replace login with chat in the navigation stack
        Navigation.InsertPageBefore(chatPage, this);
        await Navigation.PopAsync();                   // navigates to ChatPage

        // Auto-connect after landing on ChatPage
        if (chatPage.BindingContext is ViewModels.ChatViewModel vm)
        {
            await vm.ConnectCommand.ExecuteAsync(null);
        }
    }
}
