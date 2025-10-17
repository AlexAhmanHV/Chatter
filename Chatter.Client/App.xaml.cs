using Chatter.Client.Views;

namespace Chatter.Client;

public partial class App : Application
{
    public App(LoginPage loginPage)
    {
        InitializeComponent();
        MainPage = new NavigationPage(loginPage);
    }
}
