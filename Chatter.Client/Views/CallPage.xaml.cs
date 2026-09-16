/*
File: CallPage.xaml.cs

What this does:
- Purpose: Code-behind for CallPage. Wires the HybridWebView to CallViewModel once the page
  loads, and pops itself when the view model reports the call has ended.
*/

using Microsoft.Maui.Controls;
using Chatter.Client.ViewModels;

namespace Chatter.Client.Views;

public partial class CallPage : ContentPage
{
    private readonly CallViewModel _vm;

    public CallPage(CallViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;

        _vm.AttachWebView(CallWebView);
        _vm.CallEnded += OnCallEnded;
    }

    private void OnCallEnded()
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            if (Navigation.NavigationStack.Contains(this))
                await Navigation.PopAsync();
        });
    }

    protected override bool OnBackButtonPressed()
    {
        _vm.HangupCommand.Execute(null);
        return base.OnBackButtonPressed();
    }
}
