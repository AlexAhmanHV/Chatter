/*
File: EmojiPickerPage.xaml.cs

What this does:
- Purpose: Code-behind for the modal emoji picker. Lets users browse/search supported emoji shortcodes and insert the
  chosen shortcode into the chat compose box.
- How: Initializes the grid with EmojiCatalog.All, filters the list as the user types in the SearchBar, and on tap
  appends the selected item's Shortcode (e.g., ":smile:") to ChatViewModel.OutgoingMessage, then closes the modal.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Maui.Controls;
using Chatter.Client.Services;
using Chatter.Client.ViewModels;

namespace Chatter.Client.Views;

public partial class EmojiPickerPage : ContentPage
{
    private readonly ChatViewModel _vm;
    private List<EmojiItem> _all = new();

    public EmojiPickerPage(ChatViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        _all = EmojiCatalog.All;
        EmojiList.ItemsSource = _all;
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        var q = e.NewTextValue?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(q))
        {
            EmojiList.ItemsSource = _all;
            return;
        }

        q = q.ToLowerInvariant();
        EmojiList.ItemsSource = _all.Where(x =>
            x.Shortcode.ToLowerInvariant().Contains(q) ||
            x.Emoji.Contains(q)).ToList();
    }

    private async void OnPick(object sender, TappedEventArgs e)
    {
        if (e.Parameter is EmojiItem item)
        {
            var cur = _vm.OutgoingMessage ?? string.Empty;
            if (cur.Length > 0 && !char.IsWhiteSpace(cur[^1])) cur += " ";
            _vm.OutgoingMessage = cur + item.Shortcode + " ";
            await Navigation.PopModalAsync();
        }
    }

    private async void OnClose(object sender, EventArgs e) =>
        await Navigation.PopModalAsync();
}
