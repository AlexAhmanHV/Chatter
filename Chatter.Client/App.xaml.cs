// File: App.xaml.cs
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Chatter.Client.Views;

namespace Chatter.Client;

public partial class App : Application
{
    private readonly LoginPage _loginPage;

    public App(LoginPage loginPage)
    {
        InitializeComponent();
        _loginPage = loginPage;

        // Chatter is a dark-only design - force it regardless of the OS theme setting, so the
        // NavigationPage bar/title (and any other AppThemeBinding-driven chrome) never flashes
        // the default template's light colors, only ever the dark palette in Colors.xaml/Styles.xaml.
        UserAppTheme = AppTheme.Dark;
    }

    // .NET MAUI (net8+/net9): prefer overriding CreateWindow instead of setting MainPage
    protected override Window CreateWindow(IActivationState? activationState)
    {
        // If you want a Shell-based app instead, replace NavigationPage with your AppShell.
        var root = new NavigationPage(_loginPage);
        return new Window(root);
    }
}
