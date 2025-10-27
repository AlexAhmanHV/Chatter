// File: Views/EmojiHelpPage.xaml.cs
/*
What this does:
- Purpose: Code-behind for the EmojiHelpPage modal. Shows the catalog of supported emoji shortcodes so users can discover
  which aliases they can type in chat.
- How: On construct, sets BindingContext to EmojiCatalog.All (List<EmojiItem>). The Close button calls PopModalAsync
  to dismiss the sheet.
*/

using System;
using Microsoft.Maui.Controls;
using Chatter.Client.Services;

namespace Chatter.Client.Views;

public partial class EmojiHelpPage : ContentPage
{
    public EmojiHelpPage()
    {
        InitializeComponent();
        BindingContext = EmojiCatalog.All; // List<EmojiItem>
    }

    private async void OnClose(object sender, EventArgs e) =>
        await Navigation.PopModalAsync();
}