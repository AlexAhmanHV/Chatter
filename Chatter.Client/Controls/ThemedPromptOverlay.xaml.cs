/*
File: ThemedPromptOverlay.xaml.cs

What this does:
- Purpose: Code-behind for ThemedPromptOverlay. Mirrors DisplayPromptAsync's shape (title, message
  used as the field's floating label, initial value, max length) so callers built around
  DisplayPromptAsync need only swap which method they call.
- How: A single TaskCompletionSource per show; Accept/Cancel (or pressing Enter in the field)
  complete it and hide the overlay.
*/

using System;
using System.Threading.Tasks;

namespace Chatter.Client.Controls;

public partial class ThemedPromptOverlay : ContentView
{
    private TaskCompletionSource<string?>? _tcs;

    public ThemedPromptOverlay()
    {
        InitializeComponent();
    }

    public Task<string?> ShowAsync(
        string title,
        string message,
        string accept = "OK",
        string cancel = "Cancel",
        string? placeholder = null,
        int maxLength = -1,
        string initialValue = "")
    {
        TitleLabel.Text = title;
        InputField.Title = string.IsNullOrWhiteSpace(placeholder) ? message : placeholder;
        InputField.Text = initialValue;
        InputField.MaxLength = maxLength < 0 ? int.MaxValue : maxLength;
        AcceptButton.Text = accept;
        CancelButton.Text = cancel;

        _tcs = new TaskCompletionSource<string?>();
        IsVisible = true;
        InputField.Focus();
        return _tcs.Task;
    }

    private void OnInputCompleted(object? sender, EventArgs e) => Complete(InputField.Text);

    private void OnAcceptClicked(object? sender, EventArgs e) => Complete(InputField.Text);

    private void OnCancelClicked(object? sender, EventArgs e) => Complete(null);

    private void Complete(string? result)
    {
        IsVisible = false;
        _tcs?.TrySetResult(result);
        _tcs = null;
    }
}
