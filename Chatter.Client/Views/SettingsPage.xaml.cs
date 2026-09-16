/*
File: SettingsPage.xaml.cs

What this does:
- Purpose: Code-behind for the Settings screen. It wires the page’s BindingContext to the injected SettingsViewModel so XAML bindings
  (DisplayName, SaveCommand, IsBusy, etc.) work immediately.
- How: Uses constructor injection (from your DI container) and calls InitializeComponent() before assigning the ViewModel.
*/

using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Chatter.Client.Helpers;
using Chatter.Client.ViewModels;

namespace Chatter.Client.Views;

public partial class SettingsPage : ContentPage, IAlertHost
{
    private readonly IServiceProvider _services;

    public SettingsPage(SettingsViewModel vm, IServiceProvider services)
    {
        InitializeComponent();
        BindingContext = vm;
        _services = services;
        vm.LoggedOut += OnLoggedOut;
    }

    private void OnBackClicked(object? sender, EventArgs e) => _ = Navigation.PopAsync();

    // Resets the whole nav stack back to a fresh Login page - a logged-out user shouldn't be able
    // to hit "back" from Login and land in the chat session they just signed out of.
    private async void OnLoggedOut()
    {
        var loginPage = _services.GetRequiredService<LoginPage>();
        Navigation.InsertPageBefore(loginPage, Navigation.NavigationStack[0]);
        await Navigation.PopToRootAsync(false);
    }

    public Task ShowAlertAsync(string title, string message, string accept = "OK") =>
        AlertOverlay.ShowAlertAsync(title, message, accept);

    public Task<bool> ShowConfirmAsync(string title, string message, string accept, string cancel) =>
        AlertOverlay.ShowConfirmAsync(title, message, accept, cancel);
}
