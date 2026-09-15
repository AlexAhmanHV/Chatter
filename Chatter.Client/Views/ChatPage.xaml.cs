/*
File: ChatPage.xaml.cs

What this does:
- Purpose: Code-behind for the main ChatPage view. It wires the XAML UI to the ChatViewModel, resolves dependencies
  from DI, and handles UI events that are simpler to express in code (message Enter-to-send, roster selection, navigation).
- How: Accepts a ChatViewModel via DI (with a parameterless overload for XAML), sets BindingContext, and hooks up handlers
  for MessageEntry.Completed, PeopleList.SelectionChanged, and the Settings toolbar. It also toggles the VM’s IsActive
  during page lifecycle, and routes status picker changes to SetMyStatusCommand.
*/

using System;
using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls;
using Chatter.Client.Helpers;
using Chatter.Client.Messages;
using Chatter.Client.Models;
using Chatter.Client.ViewModels;

namespace Chatter.Client.Views;

public partial class ChatPage : ContentPage
{
    private readonly IServiceProvider _services;

    // Construction: parameterless for XAML — resolve VM + services from our ServiceHelper
    public ChatPage()
        : this(ServiceHelper.Services.GetRequiredService<ChatViewModel>(),
               ServiceHelper.Services)
    {
    }

    // Construction: DI-friendly ctor (lets tests or callers inject a VM)
    public ChatPage(ChatViewModel vm, IServiceProvider services)
    {
        InitializeComponent();
        BindingContext = vm;
        _services = services;

        // UI event hook-up (kept here to avoid XAML handler signature issues)
        if (MessageEntry is not null)
            MessageEntry.Completed += OnMessageCompleted;

        if (PeopleList is not null)
            PeopleList.SelectionChanged += OnOnlineUserSelected;

        if (SettingsItem is not null)
            SettingsItem.Clicked += OnSettingsClicked;

        WeakReferenceMessenger.Default.Register<ScrollToMessageMessage>(this, (_, msg) =>
            MessagesList?.ScrollTo(msg.Value, position: ScrollToPosition.Center, animate: true));
    }

    private void OnDeleteChatTapped(object? sender, EventArgs e)
{
    if (BindingContext is not ChatViewModel vm) return;
    if (sender is not Element el) return;

    // The DataTemplate’s BindingContext is the ChatItem itself
    if (el.BindingContext is ChatItem item)
        vm.DeleteChatCommand.Execute(item);
}

    // Message edit/delete/react — same code-behind pattern as OnDeleteChatTapped above, for the
    // same reason: a compiled {Binding Source={RelativeSource AncestorType=...}}} inside a
    // DataTemplate can't see the page-level ChatViewModel's commands (XamlC warns XC0045).
    private void OnEditMessageInvoked(object? sender, EventArgs e)
    {
        if (BindingContext is not ChatViewModel vm) return;
        if (sender is Element { BindingContext: ChatMessageItem item })
            vm.EditMessageCommand.Execute(item);
    }

    private void OnDeleteMessageInvoked(object? sender, EventArgs e)
    {
        if (BindingContext is not ChatViewModel vm) return;
        if (sender is Element { BindingContext: ChatMessageItem item })
            vm.DeleteMessageCommand.Execute(item);
    }

    private void OnReactButtonClicked(object? sender, EventArgs e)
    {
        if (BindingContext is not ChatViewModel vm) return;
        if (sender is Element { BindingContext: ChatMessageItem item })
            vm.ReactCommand.Execute(item);
    }

    private void OnReactionPillTapped(object? sender, EventArgs e)
    {
        if (BindingContext is not ChatViewModel vm) return;
        if (sender is Element { BindingContext: ReactionItem reaction })
            vm.ToggleReactionCommand.Execute(reaction);
    }

    private void OnAttachmentTapped(object? sender, EventArgs e)
    {
        if (BindingContext is not ChatViewModel vm) return;
        if (sender is Element { BindingContext: ChatMessageItem item })
            vm.ViewAttachmentCommand.Execute(item);
    }

    private void OnPlayVoiceMessageTapped(object? sender, EventArgs e)
    {
        if (BindingContext is not ChatViewModel vm) return;
        if (sender is Element { BindingContext: ChatMessageItem item })
            vm.PlayVoiceMessageCommand.Execute(item);
    }

    private void OnForwardMessageInvoked(object? sender, EventArgs e)
    {
        if (BindingContext is not ChatViewModel vm) return;
        if (sender is Element { BindingContext: ChatMessageItem item })
            vm.ForwardMessageCommand.Execute(item);
    }

    private void OnReplyToMessageInvoked(object? sender, EventArgs e)
    {
        if (BindingContext is not ChatViewModel vm) return;
        if (sender is Element { BindingContext: ChatMessageItem item })
            vm.ReplyToMessageCommand.Execute(item);
    }

    private void OnReplyPreviewTapped(object? sender, EventArgs e)
    {
        if (BindingContext is not ChatViewModel vm) return;
        if (sender is Element { BindingContext: ChatMessageItem item })
            vm.JumpToRepliedMessageCommand.Execute(item);
    }

    private void OnTogglePinInvoked(object? sender, EventArgs e)
    {
        if (BindingContext is not ChatViewModel vm) return;
        if (sender is Element { BindingContext: ChatMessageItem item })
            vm.TogglePinCommand.Execute(item);
    }

    private void OnViewEditHistoryTapped(object? sender, EventArgs e)
    {
        if (BindingContext is not ChatViewModel vm) return;
        if (sender is Element { BindingContext: ChatMessageItem item })
            vm.ViewEditHistoryCommand.Execute(item);
    }

    private void OnPlayVideoTapped(object? sender, EventArgs e)
    {
        if (BindingContext is not ChatViewModel vm) return;
        if (sender is Element { BindingContext: ChatMessageItem item })
            vm.PlayVideoCommand.Execute(item);
    }

    private void OnPinnedMessageTapped(object? sender, EventArgs e)
    {
        if (BindingContext is not ChatViewModel vm) return;
        if (sender is Element { BindingContext: ChatMessageItem item })
            vm.JumpToMessageCommand.Execute(item);
    }

    private void OnSearchResultTapped(object? sender, EventArgs e)
    {
        if (BindingContext is not ChatViewModel vm) return;
        if (sender is Element { BindingContext: ChatMessageItem item })
            vm.JumpToSearchResultCommand.Execute(item);
    }


    // Sending: press Enter in the message box to invoke SendCommand (when CanSend)
    private void OnMessageCompleted(object? sender, EventArgs e)
    {
        if (BindingContext is ChatViewModel vm && vm.CanSend)
            vm.SendCommand.Execute(null);
    }

    // Navigation: open the Settings page from the toolbar
    private async void OnSettingsClicked(object? sender, EventArgs e)
    {
        var settings = _services.GetRequiredService<SettingsPage>();
        await Navigation.PushAsync(settings);
    }

    // Roster selection: start a DM with the picked person (supports either UserPresenceItem or string)
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

        if (sender is CollectionView cv)
            cv.SelectedItem = null; // clear selection for better UX
    }

    // Lifecycle: mark the VM active while the page is visible and refresh currently visible messages
    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (BindingContext is ChatViewModel vm)
        {
            vm.IsActive = true;
            vm.RefreshVisibleChat();
        }
    }

    // Lifecycle: mark the VM inactive when we leave (prevents some UI-driven updates)
    protected override void OnDisappearing()
    {
        if (BindingContext is ChatViewModel vm)
            vm.IsActive = false;

        base.OnDisappearing();
    }

    // Status changes: map Picker index -> PresenceStatus and forward to the VM command
    private async void OnMyStatusChanged(object? sender, EventArgs e)
    {
        if (BindingContext is not ChatViewModel vm) return;
        if (sender is not Picker p) return;

        var sel = p.SelectedIndex switch
        {
            0 => PresenceStatus.Online,
            1 => PresenceStatus.Away,
            2 => PresenceStatus.Busy,
            _ => PresenceStatus.Online
        };

        await vm.SetMyStatusCommand.ExecuteAsync(sel);
    }
}
