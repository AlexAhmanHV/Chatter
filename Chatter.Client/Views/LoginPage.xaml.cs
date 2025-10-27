/*
File: Views/LoginPage.xaml.cs

What this does:
- Purpose: Code-behind for the Login page. Resolves its LoginViewModel from DI, wires UI events 
- (Enter in fields, Create account click), and reacts to a successful login by navigating to ChatPage and starting the chat connection.
- How: Supports both parameterless construction (for XAML/WinUI) and DI construction. 
- Subscribes to LoginViewModel.LoginSucceeded to receive the chosen display name, forwards it into ChatViewModel.User, 
- replaces the page in the nav stack, and triggers ConnectCommand. Also handles simple UX flows 
- (focus password on Enter, attempt login on Enter in password, push RegisterPage on “Create account”).
*/

using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls;
using Chatter.Client.ViewModels;
using Chatter.Client.Helpers;

namespace Chatter.Client.Views;

public partial class LoginPage : ContentPage
{
    private readonly IServiceProvider _services;

    public LoginPage()
        : this(ServiceHelper.Services.GetRequiredService<LoginViewModel>(),
               ServiceHelper.Services)
    {
    }

    // DI ctor
    public LoginPage(LoginViewModel vm, IServiceProvider services)
    {
        InitializeComponent();
        _services = services;

        BindingContext = vm;
        vm.LoginSucceeded += OnLoginSucceeded;

        // Wire Enter + button click in code (since XAML handlers were removed)
        EmailEntry.Completed += OnUsernameCompleted; 
        PasswordEntry.Completed += OnLoginCompleted;
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
            chatVm.User = displayName;

        // Replace LoginPage with ChatPage in the back stack
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
