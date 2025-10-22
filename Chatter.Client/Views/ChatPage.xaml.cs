using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls;
using Chatter.Client.Helpers;
using Chatter.Client.Models;
using Chatter.Client.ViewModels;

namespace Chatter.Client.Views;

public partial class ChatPage : ContentPage
{
    private readonly IServiceProvider _services;

    // Parameterless for XAML — resolve from DI
    public ChatPage()
        : this(ServiceHelper.Services.GetRequiredService<ChatViewModel>(),
               ServiceHelper.Services)
    {
    }

    // DI ctor
    public ChatPage(ChatViewModel vm, IServiceProvider services)
    {
        InitializeComponent();
        BindingContext = vm;
        _services = services;

        // Wire events explicitly to avoid XAML signature issues
        if (MessageEntry != null)
            MessageEntry.Completed += OnMessageCompleted;

        if (PeopleList != null)
            PeopleList.SelectionChanged += OnOnlineUserSelected;

        if (SettingsItem != null)
            SettingsItem.Clicked += OnSettingsClicked;
    }

    private void OnMessageCompleted(object? sender, EventArgs e)
    {
        if (BindingContext is ChatViewModel vm && vm.CanSend)
            vm.SendCommand.Execute(null);
    }

    private async void OnSettingsClicked(object? sender, EventArgs e)
    {
        var settings = _services.GetRequiredService<SettingsPage>();
        await Navigation.PushAsync(settings);
    }

    // Works with OnlineUsers (string) OR People (UserPresenceItem)
    private async void OnOnlineUserSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (BindingContext is not ChatViewModel vm) return;

        string? pickedName = e.CurrentSelection?.FirstOrDefault() switch
        {
            UserPresenceItem up => up.Name,
            string s            => s,
            _                   => null
        };

        if (!string.IsNullOrWhiteSpace(pickedName) &&
            !pickedName.Equals(vm.User, StringComparison.OrdinalIgnoreCase))
        {
            await vm.StartDmCommand.ExecuteAsync(pickedName);
        }

        if (sender is CollectionView cv) cv.SelectedItem = null;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (BindingContext is ChatViewModel vm)
        {
            vm.IsActive = true;
            vm.RefreshVisibleChat();
        }
    }

    protected override void OnDisappearing()
    {
        if (BindingContext is ChatViewModel vm)
            vm.IsActive = false;
        base.OnDisappearing();
    }

    private async void OnMyStatusChanged(object sender, EventArgs e)
{
    if (BindingContext is not ChatViewModel vm) return;
    if (sender is not Picker p) return;

    var sel = (p.SelectedIndex) switch
    {
        0 => PresenceStatus.Online,
        1 => PresenceStatus.Away,
        2 => PresenceStatus.Busy,
        _ => PresenceStatus.Online
    };

    await vm.SetMyStatusCommand.ExecuteAsync(sel);
}
}
