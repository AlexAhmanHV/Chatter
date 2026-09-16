/*
File: Helpers/Ui.cs

What this does:
- Purpose: Tiny UI helper for showing alerts and getting the current Page in a single-window MAUI app.
- How: Reads Application.Current.Windows[0].Page (unwrapping the root NavigationPage down to whatever
  page is actually on screen) and forwards DisplayAlert calls to it. When that page implements
  IAlertHost (i.e. embeds a ThemedAlertOverlay), routes through the themed overlay instead of the
  native (unstyled) DisplayAlert so alerts match the rest of the Material redesign.
*/

using Microsoft.Maui.Controls;
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
}
