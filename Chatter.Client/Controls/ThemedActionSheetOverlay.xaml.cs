/*
File: ThemedActionSheetOverlay.xaml.cs

What this does:
- Purpose: Code-behind for ThemedActionSheetOverlay. Mirrors DisplayActionSheet's shape (title,
  cancel label, an optional destructive option styled in the app's error color, and the list of
  choices) so callers built around DisplayActionSheet need only swap which method they call.
- How: Builds one row Button per option (plus a separator between rows) each time it's shown, since
  the option set is only known at call time. A single TaskCompletionSource per show; a row tap or
  Cancel completes it with the chosen label (or null for Cancel/dismiss) and hides the overlay.
*/

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Chatter.Client.Controls;

public partial class ThemedActionSheetOverlay : ContentView
{
    private TaskCompletionSource<string?>? _tcs;

    public ThemedActionSheetOverlay()
    {
        InitializeComponent();
    }

    public Task<string?> ShowAsync(string title, string cancel, string? destruction, IReadOnlyList<string> buttons)
    {
        TitleLabel.Text = title;
        TitleLabel.IsVisible = !string.IsNullOrEmpty(title);
        CancelButton.Text = cancel;

        var errorColor = (Color)Resources["ColorError"];
        var onSurfaceColor = (Color)Resources["ColorOnSurface"];
        var outlineColor = (Color)Resources["ColorOutline"];

        OptionsStack.Children.Clear();
        for (var i = 0; i < buttons.Count; i++)
        {
            var option = buttons[i];
            var row = new Button
            {
                Text = option,
                Style = (Style)Resources["ActionRow"],
                TextColor = option == destruction ? errorColor : onSurfaceColor,
            };
            row.Clicked += (_, _) => Complete(option);
            OptionsStack.Children.Add(row);

            if (i < buttons.Count - 1)
                OptionsStack.Children.Add(new BoxView { HeightRequest = 1, BackgroundColor = outlineColor });
        }

        _tcs = new TaskCompletionSource<string?>();
        IsVisible = true;
        return _tcs.Task;
    }

    private void OnCancelClicked(object? sender, EventArgs e) => Complete(null);

    private void Complete(string? result)
    {
        IsVisible = false;
        _tcs?.TrySetResult(result);
        _tcs = null;
    }
}
