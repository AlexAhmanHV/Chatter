/*
File: Helpers/IActionSheetHost.cs

What this does:
- Purpose: Marks a Page as owning a ThemedActionSheetOverlay so Ui.DisplayActionSheet can route
  through it instead of the native (unstyled) DisplayActionSheet.
- How: Implemented by pages that embed a ThemedActionSheetOverlay in their XAML; each just forwards
  to it.
*/

using System.Collections.Generic;
using System.Threading.Tasks;

namespace Chatter.Client.Helpers;

public interface IActionSheetHost
{
    Task<string?> ShowActionSheetAsync(string title, string cancel, string? destruction, IReadOnlyList<string> buttons);
}
