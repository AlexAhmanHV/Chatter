/*
File: SettingsPage.xaml.cs

What this does:
- Purpose: Code-behind for the Settings screen. It wires the page’s BindingContext to the injected SettingsViewModel so XAML bindings
  (DisplayName, SaveCommand, IsBusy, etc.) work immediately.
- How: Uses constructor injection (from your DI container) and calls InitializeComponent() before assigning the ViewModel.
*/

using System;
using Chatter.Client.ViewModels;

namespace Chatter.Client.Views;

public partial class SettingsPage : ContentPage
{
    public SettingsPage(SettingsViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }

    private void OnBackClicked(object? sender, EventArgs e) => _ = Navigation.PopAsync();
}
