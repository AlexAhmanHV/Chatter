/*
File: Helpers/IAlertHost.cs

What this does:
- Purpose: Marks a Page as owning a ThemedAlertOverlay so Ui.DisplayAlert can route through it instead
  of the native (unstyled) DisplayAlert.
- How: Implemented by pages that embed a ThemedAlertOverlay in their XAML; each just forwards to it.
*/

using System.Threading.Tasks;

namespace Chatter.Client.Helpers;

public interface IAlertHost
{
    Task ShowAlertAsync(string title, string message, string accept = "OK");

    Task<bool> ShowConfirmAsync(string title, string message, string accept, string cancel);
}
