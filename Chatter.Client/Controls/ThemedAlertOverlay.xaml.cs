/*
File: ThemedAlertOverlay.xaml.cs

What this does:
- Purpose: Code-behind for ThemedAlertOverlay. Mirrors DisplayAlert's two overloads (info-only, and
  accept/cancel returning bool) so call sites built around DisplayAlert's shape need no rework beyond
  swapping which method they call.
- How: A single TaskCompletionSource per show; the Accept/Cancel button handlers complete it and hide
  the overlay. Only one alert can be visible at a time per overlay instance (matches DisplayAlert's own
  one-at-a-time modal behavior).
*/

using System.Threading.Tasks;

namespace Chatter.Client.Controls;

public partial class ThemedAlertOverlay : ContentView
{
    private TaskCompletionSource<bool>? _tcs;

    public ThemedAlertOverlay()
    {
        InitializeComponent();
    }

    public Task ShowAlertAsync(string title, string message, string accept = "OK")
        => ShowCoreAsync(title, message, accept, null);

    public Task<bool> ShowConfirmAsync(string title, string message, string accept, string cancel)
        => ShowCoreAsync(title, message, accept, cancel);

    private Task<bool> ShowCoreAsync(string title, string message, string accept, string? cancel)
    {
        TitleLabel.Text = title;
        MessageLabel.Text = message;
        AcceptButton.Text = accept;

        if (cancel is null)
        {
            CancelButton.IsVisible = false;
            Grid.SetColumnSpan(AcceptButton, 2);
        }
        else
        {
            CancelButton.Text = cancel;
            CancelButton.IsVisible = true;
            Grid.SetColumnSpan(AcceptButton, 1);
        }

        _tcs = new TaskCompletionSource<bool>();
        IsVisible = true;
        return _tcs.Task;
    }

    private void OnAcceptClicked(object? sender, System.EventArgs e) => Complete(true);

    private void OnCancelClicked(object? sender, System.EventArgs e) => Complete(false);

    private void Complete(bool result)
    {
        IsVisible = false;
        _tcs?.TrySetResult(result);
        _tcs = null;
    }
}
