/*
File: Helpers/IPromptHost.cs

What this does:
- Purpose: Marks a Page as owning a ThemedPromptOverlay so Ui.DisplayPromptAsync can route through
  it instead of the native (unstyled) DisplayPromptAsync.
- How: Implemented by pages that embed a ThemedPromptOverlay in their XAML; each just forwards to it.
*/

using System.Threading.Tasks;

namespace Chatter.Client.Helpers;

public interface IPromptHost
{
    Task<string?> ShowPromptAsync(
        string title,
        string message,
        string accept = "OK",
        string cancel = "Cancel",
        string? placeholder = null,
        int maxLength = -1,
        string initialValue = "");
}
