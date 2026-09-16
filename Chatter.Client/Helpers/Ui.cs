/*
File: Helpers/Ui.cs

What this does:
- Purpose: Tiny UI helper for showing alerts/prompts/action sheets and getting the current Page in a
  single-window MAUI app.
- How: Reads Application.Current.Windows[0].Page (unwrapping the root NavigationPage down to whatever
  page is actually on screen) and forwards to it. When that page implements the matching IAlertHost /
  IPromptHost / IActionSheetHost interface (i.e. embeds the matching Themed*Overlay), routes through
  the themed overlay instead of the native (unstyled) dialog so it matches the rest of the Material
  redesign; otherwise falls back to the native DisplayAlert/DisplayPromptAsync/DisplayActionSheet.
*/

using Microsoft.Maui.Controls;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Chatter.Client.Helpers;

public static class Ui
{

    public static Page? CurrentPage =>
        Application.Current?.Windows?.FirstOrDefault()?.Page is NavigationPage nav
            ? nav.CurrentPage
            : Application.Current?.Windows?.FirstOrDefault()?.Page;

    public static Task DisplayAlert(string title, string message, string cancel) =>
        CurrentPage switch
        {
            IAlertHost host => host.ShowAlertAsync(title, message, cancel),
            Page page => page.DisplayAlert(title, message, cancel),
            _ => Task.CompletedTask,
        };

    public static Task<bool> DisplayAlert(string title, string message, string accept, string cancel) =>
        CurrentPage switch
        {
            IAlertHost host => host.ShowConfirmAsync(title, message, accept, cancel),
            Page page => page.DisplayAlert(title, message, accept, cancel),
            _ => Task.FromResult(false),
        };

    public static Task<string?> DisplayPromptAsync(
        string title,
        string message,
        string accept = "OK",
        string cancel = "Cancel",
        string? placeholder = null,
        int maxLength = -1,
        Keyboard? keyboard = null,
        string initialValue = "") =>
        CurrentPage switch
        {
            IPromptHost host => host.ShowPromptAsync(title, message, accept, cancel, placeholder, maxLength, initialValue),
            Page page => page.DisplayPromptAsync(title, message, accept, cancel, placeholder, maxLength, keyboard ?? Keyboard.Default, initialValue),
            _ => Task.FromResult<string?>(null),
        };

    public static Task<string?> DisplayActionSheet(string title, string cancel, string? destruction, params string[] buttons) =>
        CurrentPage switch
        {
            IActionSheetHost host => host.ShowActionSheetAsync(title, cancel, destruction, buttons),
            Page page => page.DisplayActionSheet(title, cancel, destruction, buttons),
            _ => Task.FromResult<string?>(null),
        };
}
