using System;
using Microsoft.Extensions.DependencyInjection;
using Chatter.Client.ViewModels;
using Chatter.Client.Views;
using Chatter.Client.Helpers; // if you created ServiceHelper

namespace Chatter.Client.Views;

public partial class LoginPage : ContentPage
{
    private readonly IServiceProvider _services;

    // Optional: parameterless ctor for XAML/WinUI (resolves from DI)
    public LoginPage()
        : this(ServiceHelper.Services.GetRequiredService<LoginViewModel>(),
               ServiceHelper.Services)
    {
    }

    public LoginPage(LoginViewModel vm, IServiceProvider services)
    {
        InitializeComponent();
        _services = services;

        BindingContext = vm;
        vm.LoginSucceeded += OnLoginSucceeded;

        // 🔗 Wire Enter + button click in code (since XAML handlers were removed)
        EmailEntry.Completed += OnUsernameCompleted;   // Enter in email -> focus password
        PasswordEntry.Completed += OnLoginCompleted;   // Enter in password -> login
        CreateAccountBtn.Clicked += OnCreateAccountClicked;
    }

    private void OnUsernameCompleted(object? sender, EventArgs e)
    {
        PasswordEntry?.Focus();
    }

    private void OnLoginCompleted(object? sender, EventArgs e)
    {
        if (BindingContext is LoginViewModel vm && vm.CanLogin && !vm.IsBusy)
            vm.LoginCommand.Execute(null);
    }

    private async void OnLoginSucceeded(string displayName)
    {
        var chatPage = _services.GetRequiredService<ChatPage>();

        if (chatPage.BindingContext is ChatViewModel chatVm)
            chatVm.User = displayName; // show display name everywhere

        Navigation.InsertPageBefore(chatPage, this);
        await Navigation.PopAsync();

        if (chatPage.BindingContext is ChatViewModel vm)
            await vm.ConnectCommand.ExecuteAsync(null);
    }

    private async void OnCreateAccountClicked(object? sender, EventArgs e)
    {
        var registerPage = _services.GetRequiredService<RegisterPage>();
        await Navigation.PushAsync(registerPage);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        EmailEntry?.Focus();
    }
}
