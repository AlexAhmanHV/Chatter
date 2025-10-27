/*
File: Helpers/Ui.cs

What this does:
- Purpose: Tiny UI helper for showing alerts and getting the current Page in a single-window MAUI app.
- How: Reads Application.Current.Windows[0].Page as the active page, then forwards DisplayAlert calls to it.
*/

using Microsoft.Maui.Controls;
using System.Linq;
using System.Threading.Tasks;

namespace Chatter.Client.Helpers;

public static class Ui
{

    public static Page? CurrentPage =>
        Application.Current?.Windows?.FirstOrDefault()?.Page;

    public static Task DisplayAlert(string title, string message, string cancel) =>
        CurrentPage != null
            ? CurrentPage.DisplayAlert(title, message, cancel)
            : Task.CompletedTask;

    public static Task<bool> DisplayAlert(string title, string message, string accept, string cancel) =>
        CurrentPage != null
            ? CurrentPage.DisplayAlert(title, message, accept, cancel)
            : Task.FromResult(false);
}
